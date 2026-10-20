package com.tinyfish.jeekmusic.data.online

import com.tinyfish.jeekmusic.data.settings.OnlineSource
import org.json.JSONObject
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 网易云音乐的网页接口（非官方，可能随时失效；docs/design.md §8.4）：国内访问稳定，中文歌收录全，歌词常带翻译。
 * 只用搜索和歌词两个不需要登录的接口。
 */
@Singleton
class NetEaseMusic @Inject constructor() {
    private val limit = Http.RateLimit(INTERVAL_MS)

    suspend fun search(query: String, count: Int = 10): List<OnlineSong> {
        val body = limit.run {
            Http.text("$BASE/api/cloudsearch/pc", HEADERS, body = "s=${Http.encode(query)}&type=1&limit=$count&offset=0")
        } ?: return emptyList()
        val songs = JSONObject(body).optJSONObject("result")?.optJSONArray("songs") ?: return emptyList()
        return List(songs.length()) { songs.getJSONObject(it) }.map { s ->
            val album = s.optJSONObject("al")
            val artists = s.optJSONArray("ar")
            // 图片地址是 http 的，换成 https；?param=宽y高 指定尺寸
            val pic = album?.optString("picUrl")?.takeIf { it.startsWith("http") }?.replaceFirst("http://", "https://")
            OnlineSong(
                source = OnlineSource.NETEASE,
                id = s.optLong("id").toString(),
                title = s.optString("name"),
                artist = artists?.let { a -> List(a.length()) { a.getJSONObject(it).optString("name") }.joinToString(", ") }.orEmpty(),
                album = album?.optString("name")?.takeIf { it.isNotEmpty() },
                durationMs = s.optLong("dt").takeIf { it > 0 },
                thumbUrl = pic?.let { "$it?param=300y300" },
                imageUrl = pic?.let { "$it?param=1000y1000" },
            )
        }
    }

    /** LRC 歌词；有翻译时把翻译行接在后面（同一时间戳的两行会显示成原文加翻译，需求 LY-5）。没有歌词时返回 null。 */
    suspend fun lyrics(id: String): String? {
        val body = limit.run { Http.text("$BASE/api/song/lyric?id=$id&lv=1&tv=-1", HEADERS) } ?: return null
        val json = JSONObject(body)
        val lrc = json.optJSONObject("lrc")?.optString("lyric")?.takeIf { it.isNotBlank() } ?: return null
        val translation = json.optJSONObject("tlyric")?.optString("lyric")?.takeIf { it.isNotBlank() }
        return if (translation == null) lrc else lrc.trimEnd() + "\n" + translation
    }

    private companion object {
        const val BASE = "https://music.163.com"
        const val INTERVAL_MS = 300L
        val HEADERS = mapOf("User-Agent" to Http.BROWSER_AGENT, "Referer" to "https://music.163.com/")
    }
}
