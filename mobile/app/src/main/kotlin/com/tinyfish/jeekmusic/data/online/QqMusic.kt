package com.tinyfish.jeekmusic.data.online

import com.tinyfish.jeekmusic.data.settings.OnlineSource
import org.json.JSONObject
import javax.inject.Inject
import javax.inject.Singleton

/**
 * QQ 音乐的网页接口（非官方，可能随时失效；docs/design.md §8.4）：国内访问稳定，收录了网易云没有的周杰伦等。
 * 只用搜索和歌词两个不需要登录的接口；封面直接按专辑 id 拼出图片地址。
 */
@Singleton
class QqMusic @Inject constructor() {
    private val limit = Http.RateLimit(INTERVAL_MS)

    /** 搜索接口时不时会对某个地址返回 500：几个返回格式相同的地址依次试，记住上次能用的。 */
    @Volatile
    private var workingEndpoint = 0

    suspend fun search(query: String, count: Int = 10): List<OnlineSong> {
        val params = "format=json&p=1&n=$count&w=${Http.encode(query)}"
        var error: Throwable? = null
        var body: String? = null
        for (attempt in SEARCH_ENDPOINTS.indices) {
            val index = (workingEndpoint + attempt) % SEARCH_ENDPOINTS.size
            val result = runCatching { limit.run { Http.text("${SEARCH_ENDPOINTS[index]}?$params", HEADERS) } }
            if (result.isSuccess) {
                workingEndpoint = index
                body = result.getOrNull()
                error = null
                break
            }
            error = result.exceptionOrNull()
        }
        error?.let { throw it }
        if (body == null) return emptyList()
        val list = JSONObject(body).optJSONObject("data")?.optJSONObject("song")?.optJSONArray("list") ?: return emptyList()
        return List(list.length()) { list.getJSONObject(it) }.map { s ->
            val singers = s.optJSONArray("singer")
            val albumMid = s.optString("albummid").takeIf { it.isNotEmpty() }
            OnlineSong(
                source = OnlineSource.QQ,
                id = s.optString("songmid"),
                title = unescape(s.optString("songname")),
                artist = singers?.let { a -> List(a.length()) { unescape(a.getJSONObject(it).optString("name")) }.joinToString(", ") }.orEmpty(),
                album = s.optString("albumname").takeIf { it.isNotEmpty() }?.let(::unescape),
                durationMs = s.optLong("interval").takeIf { it > 0 }?.times(1000),
                thumbUrl = albumMid?.let { "$COVER/T002R300x300M000$it.jpg" },
                imageUrl = albumMid?.let { "$COVER/T002R800x800M000$it.jpg" },
            )
        }.filter { it.id.isNotEmpty() }
    }

    /** LRC 歌词；有翻译时把翻译行接在后面。没有歌词时返回 null。 */
    suspend fun lyrics(songMid: String): String? {
        val body = limit.run {
            Http.text("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?format=json&nobase64=1&songmid=${Http.encode(songMid)}", HEADERS)
        } ?: return null
        val json = JSONObject(body)
        val lrc = unescape(json.optString("lyric")).takeIf { it.isNotBlank() } ?: return null
        val translation = unescape(json.optString("trans")).takeIf { it.isNotBlank() }
        return if (translation == null) lrc else lrc.trimEnd() + "\n" + translation
    }

    companion object {
        private const val COVER = "https://y.gtimg.cn/music/photo_new"
        private val SEARCH_ENDPOINTS = listOf(
            "https://c.y.qq.com/soso/fcgi-bin/search_for_qq_cp",
            "https://c6.y.qq.com/soso/fcgi-bin/client_search_cp",
            "https://c.y.qq.com/soso/fcgi-bin/client_search_cp",
        )
        private const val INTERVAL_MS = 300L
        private val HEADERS = mapOf("User-Agent" to Http.BROWSER_AGENT, "Referer" to "https://y.qq.com/")
        private val ENTITY = Regex("""&(#\d+|#x[0-9a-fA-F]+|amp|lt|gt|quot|apos);""")

        /** 接口返回的文本里有 HTML 实体（&#39; &amp; 等）。 */
        fun unescape(text: String): String = text.replace(ENTITY) { m ->
            when (val e = m.groupValues[1]) {
                "amp" -> "&"
                "lt" -> "<"
                "gt" -> ">"
                "quot" -> "\""
                "apos" -> "'"
                else -> runCatching {
                    val code = if (e.startsWith("#x")) e.substring(2).toInt(16) else e.substring(1).toInt()
                    String(Character.toChars(code))
                }.getOrDefault(m.value)
            }
        }
    }
}
