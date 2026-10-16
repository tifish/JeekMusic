package com.tinyfish.jeekmusic.data.lyrics

import com.tinyfish.jeekmusic.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import javax.inject.Inject

data class OnlineLyrics(
    val id: Long,
    val trackName: String,
    val artistName: String,
    val albumName: String?,
    val durationSec: Double?,
    val synced: String?,
    val plain: String?,
) {
    val text: String? get() = synced ?: plain
    val isSynced: Boolean get() = synced != null
}

/**
 * LRCLIB 在线歌词（需求 LY-8，docs/design.md §8.4）。只在用户打开在线歌词后才会被调用。
 * 用系统自带的 HttpURLConnection 和 org.json，不引入额外的网络库。
 */
class LrcLibClient @Inject constructor() {

    /** 精确查询；没有找到时返回 null。 */
    suspend fun get(title: String, artist: String, album: String?, durationSec: Long): OnlineLyrics? = withContext(Dispatchers.IO) {
        val query = buildString {
            append("track_name=").append(encode(title))
            append("&artist_name=").append(encode(artist))
            if (!album.isNullOrEmpty()) append("&album_name=").append(encode(album))
            if (durationSec > 0) append("&duration=").append(durationSec)
        }
        request("/api/get?$query")?.let { parse(JSONObject(it)) }
    }

    suspend fun search(title: String, artist: String?): List<OnlineLyrics> = withContext(Dispatchers.IO) {
        val query = buildString {
            append("track_name=").append(encode(title))
            if (!artist.isNullOrEmpty()) append("&artist_name=").append(encode(artist))
        }
        val body = request("/api/search?$query") ?: return@withContext emptyList()
        val array = JSONArray(body)
        List(array.length()) { parse(array.getJSONObject(it)) }.filter { it.text != null }
    }

    /** 返回响应体；404 返回 null；其他错误抛出异常。 */
    private fun request(path: String): String? {
        val connection = URL(BASE + path).openConnection() as HttpURLConnection
        try {
            connection.connectTimeout = TIMEOUT_MS
            connection.readTimeout = TIMEOUT_MS
            // LRCLIB 要求标明客户端
            connection.setRequestProperty("User-Agent", "JeekMusic/${BuildConfig.VERSION_NAME} (https://github.com/tifish/JeekMusic)")
            return when (val code = connection.responseCode) {
                200 -> connection.inputStream.use { it.readBytes().toString(Charsets.UTF_8) }
                404 -> null
                else -> throw java.io.IOException("LRCLIB HTTP $code")
            }
        } finally {
            connection.disconnect()
        }
    }

    private fun parse(json: JSONObject) = OnlineLyrics(
        id = json.optLong("id"),
        trackName = json.optString("trackName"),
        artistName = json.optString("artistName"),
        albumName = json.optString("albumName").takeIf { it.isNotEmpty() && it != "null" },
        durationSec = json.optDouble("duration").takeIf { !it.isNaN() },
        synced = json.optString("syncedLyrics").takeIf { it.isNotBlank() && it != "null" },
        plain = json.optString("plainLyrics").takeIf { it.isNotBlank() && it != "null" },
    )

    private fun encode(value: String) = URLEncoder.encode(value, "UTF-8")

    private companion object {
        const val BASE = "https://lrclib.net"
        const val TIMEOUT_MS = 10_000
    }
}
