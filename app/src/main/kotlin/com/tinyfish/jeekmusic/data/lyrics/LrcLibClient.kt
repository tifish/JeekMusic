package com.tinyfish.jeekmusic.data.lyrics

import com.tinyfish.jeekmusic.data.online.Http
import com.tinyfish.jeekmusic.data.settings.OnlineSource
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import javax.inject.Inject
import javax.inject.Singleton

data class OnlineLyrics(
    val source: OnlineSource,
    /** 来源里的 id；和 [source] 一起才唯一。 */
    val id: String,
    val trackName: String,
    val artistName: String,
    val albumName: String?,
    val durationSec: Double?,
    val synced: String?,
    val plain: String?,
    /** 歌名和艺术家对得上；手动搜索时用来排序。 */
    val matched: Boolean = false,
) {
    val text: String? get() = synced ?: plain
    val isSynced: Boolean get() = synced != null
}

/**
 * LRCLIB 在线歌词（需求 LY-8，docs/design.md §8.4）：开放的歌词库，国内访问不稳定。
 * 只在用户打开在线歌词并启用这个来源后才会被调用。
 */
@Singleton
class LrcLibClient @Inject constructor() {

    /** 精确查询；没有找到时返回 null。 */
    suspend fun get(title: String, artist: String, album: String?, durationSec: Long): OnlineLyrics? = withContext(Dispatchers.IO) {
        val query = buildString {
            append("track_name=").append(Http.encode(title))
            append("&artist_name=").append(Http.encode(artist))
            if (!album.isNullOrEmpty()) append("&album_name=").append(Http.encode(album))
            if (durationSec > 0) append("&duration=").append(durationSec)
        }
        Http.text("$BASE/api/get?$query", timeoutMs = TIMEOUT_MS)?.let { parse(JSONObject(it)) }?.takeIf { it.text != null }
    }

    suspend fun search(title: String, artist: String?): List<OnlineLyrics> = withContext(Dispatchers.IO) {
        val query = buildString {
            append("track_name=").append(Http.encode(title))
            if (!artist.isNullOrEmpty()) append("&artist_name=").append(Http.encode(artist))
        }
        val body = Http.text("$BASE/api/search?$query", timeoutMs = TIMEOUT_MS) ?: return@withContext emptyList()
        val array = JSONArray(body)
        List(array.length()) { parse(array.getJSONObject(it)) }.filter { it.text != null }
    }

    private fun parse(json: JSONObject) = OnlineLyrics(
        source = OnlineSource.LRCLIB,
        id = json.optLong("id").toString(),
        trackName = json.optString("trackName"),
        artistName = json.optString("artistName"),
        albumName = json.optString("albumName").takeIf { it.isNotEmpty() && it != "null" },
        durationSec = json.optDouble("duration").takeIf { !it.isNaN() },
        synced = json.optString("syncedLyrics").takeIf { it.isNotBlank() && it != "null" },
        plain = json.optString("plainLyrics").takeIf { it.isNotBlank() && it != "null" },
    )

    private companion object {
        const val BASE = "https://lrclib.net"
        const val TIMEOUT_MS = 10_000
    }
}
