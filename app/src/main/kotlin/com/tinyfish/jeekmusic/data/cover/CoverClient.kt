package com.tinyfish.jeekmusic.data.cover

import android.icu.text.Transliterator
import android.os.SystemClock
import com.tinyfish.jeekmusic.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import javax.inject.Inject
import javax.inject.Singleton

enum class CoverSource { ITUNES, MUSICBRAINZ }

/** 一个在线封面候选。[imageUrls] 按清晰度从高到低，前一个下载失败时用后一个。 */
data class CoverCandidate(
    val source: CoverSource,
    val title: String,
    val artist: String,
    val album: String?,
    val thumbUrl: String,
    val imageUrls: List<String>,
)

/**
 * 在线封面（需求 C-3，docs/design.md §7.4）：iTunes Search API 为主，MusicBrainz + Cover Art Archive 补充。
 * 只在用户打开"在线封面"后才会被调用。
 *
 * - iTunes：中文歌名只有繁体能搜到，含汉字时转成繁体、查台湾区；其余查美国区。限速约 20 次 / 分钟。
 * - MusicBrainz：中文资料比 iTunes 全，要求标明客户端、每秒不超过 1 次；封面取发行组（release group）的代表封面。
 */
@Singleton
class CoverClient @Inject constructor() {

    private val matcher = CoverMatcher(::toSimplified)
    private val iTunesLimit = RateLimit(ITUNES_INTERVAL_MS)
    private val musicBrainzLimit = RateLimit(MUSICBRAINZ_INTERVAL_MS)

    /**
     * 手动搜索一个来源：对得上歌名和艺术家的排在前面。两个来源分开搜，界面先显示先返回的。
     * 网络出错时抛出异常。
     */
    suspend fun search(source: CoverSource, title: String, artist: String?): List<CoverCandidate> = withContext(Dispatchers.IO) {
        val found = when (source) {
            CoverSource.ITUNES -> iTunes(title, artist, limit = 12).take(8)
            CoverSource.MUSICBRAINZ -> musicBrainz(title, artist, minScore = 80).take(6)
        }
        found.sortedByDescending { score(it, title, artist) }
    }

    /** 下载缩略图；失败时返回 null（MusicBrainz 的发行组不一定有封面）。 */
    suspend fun thumbnail(candidate: CoverCandidate): ByteArray? = withContext(Dispatchers.IO) { download(candidate.thumbUrl) }

    /**
     * 批量获取：按 iTunes、MusicBrainz 的顺序找第一个歌名和艺术家都对得上的结果，返回封面图片；找不到返回 null。
     * 网络出错时抛出异常，由调用方决定是否重试。
     */
    suspend fun fetchBest(title: String, artist: String, album: String?): ByteArray? = withContext(Dispatchers.IO) {
        fun List<CoverCandidate>.ordered() = filter { matches(it, title, artist) }
            .sortedWith(
                compareByDescending<CoverCandidate> { c -> album != null && c.album != null && matcher.titleMatches(album, c.album) }
                    .thenByDescending { score(it, title, artist) },
            )
        iTunes(title, artist, limit = 10).ordered().firstNotNullOfOrNull { downloadImage(it) }
            ?: musicBrainz(title, artist, minScore = 90).ordered().take(4).firstNotNullOfOrNull { downloadImage(it) }
    }

    /** 下载候选的大图；都失败时返回 null。 */
    suspend fun downloadImage(candidate: CoverCandidate): ByteArray? = withContext(Dispatchers.IO) {
        candidate.imageUrls.firstNotNullOfOrNull { download(it) }
    }

    private fun matches(c: CoverCandidate, title: String, artist: String?): Boolean =
        matcher.titleMatches(title, c.title) && (artist.isNullOrBlank() || matcher.artistMatches(artist, c.artist))

    /** 对不上为 0；对得上为 1；连括号里的附注也一样（不是 Remix、Live 等其他版本）为 2。 */
    private fun score(c: CoverCandidate, title: String, artist: String?): Int = when {
        !matches(c, title, artist) -> 0
        matcher.normalize(title) == matcher.normalize(c.title) -> 2
        else -> 1
    }

    private suspend fun iTunes(title: String, artist: String?, limit: Int): List<CoverCandidate> {
        val term = listOfNotNull(artist?.takeIf { it.isNotBlank() }?.let(CoverMatcher::primaryArtist), title).joinToString(" ")
        val cjk = CoverMatcher.hasCjk(term)
        val query = "term=" + encode(if (cjk) toTraditional(term) else term) +
            "&entity=song&limit=$limit&country=" + (if (cjk) "tw" else "us")
        val body = iTunesLimit.run { request("https://itunes.apple.com/search?$query") } ?: return emptyList()
        val results = JSONObject(body).optJSONArray("results") ?: return emptyList()
        return List(results.length()) { results.getJSONObject(it) }.mapNotNull { r ->
            val art = r.optString("artworkUrl100").takeIf { it.contains("100x100") } ?: return@mapNotNull null
            CoverCandidate(
                source = CoverSource.ITUNES,
                title = r.optString("trackName"),
                artist = r.optString("artistName"),
                album = r.optString("collectionName").takeIf { it.isNotEmpty() },
                thumbUrl = art.replace("100x100", "300x300"),
                imageUrls = listOf(art.replace("100x100", "1000x1000"), art.replace("100x100", "600x600")),
            )
        }.distinctBy { it.thumbUrl }
    }

    private suspend fun musicBrainz(title: String, artist: String?, minScore: Int): List<CoverCandidate> {
        val query = buildString {
            append("recording:\"").append(luceneEscape(title)).append('"')
            if (!artist.isNullOrBlank()) append(" AND artist:\"").append(luceneEscape(CoverMatcher.primaryArtist(artist))).append('"')
        }
        val body = musicBrainzLimit.run {
            request("https://musicbrainz.org/ws/2/recording?fmt=json&limit=10&query=" + encode(query))
        } ?: return emptyList()
        val recordings = JSONObject(body).optJSONArray("recordings") ?: return emptyList()
        val seen = mutableSetOf<String>()
        return buildList {
            for (i in 0 until recordings.length()) {
                val r = recordings.getJSONObject(i)
                if (r.optInt("score") < minScore) continue
                val credits = r.optJSONArray("artist-credit")
                val artistName = credits?.let { a -> List(a.length()) { a.getJSONObject(it).optString("name") }.joinToString(", ") }.orEmpty()
                val releases = r.optJSONArray("releases") ?: continue
                for (j in 0 until releases.length()) {
                    val release = releases.getJSONObject(j)
                    val group = release.optJSONObject("release-group")?.optString("id")?.takeIf { it.isNotEmpty() } ?: continue
                    if (!seen.add(group)) continue
                    val base = "https://coverartarchive.org/release-group/$group"
                    add(
                        CoverCandidate(
                            source = CoverSource.MUSICBRAINZ,
                            title = r.optString("title"),
                            artist = artistName,
                            album = release.optString("title").takeIf { it.isNotEmpty() },
                            thumbUrl = "$base/front-250",
                            imageUrls = listOf("$base/front-1200", "$base/front-500"),
                        ),
                    )
                }
            }
        }
    }

    /** 返回响应体；404 返回 null；其他错误抛出异常。503 是限流（MusicBrainz 偶尔会有），等一下重试。 */
    private fun request(url: String): String? {
        repeat(RETRIES) {
            try {
                return connect(url) { it.readBytes().toString(Charsets.UTF_8) }
            } catch (e: Unavailable) {
                Thread.sleep(RETRY_DELAY_MS)
            }
        }
        return connect(url) { it.readBytes().toString(Charsets.UTF_8) }
    }

    private class Unavailable : IOException()

    /** 下载图片；失败时返回 null。 */
    private fun download(url: String): ByteArray? = runCatching { connect(url) { it.readBytes() } }.getOrNull()
        ?.takeIf { it.size in MIN_IMAGE_BYTES..MAX_IMAGE_BYTES }

    private fun <T> connect(url: String, read: (java.io.InputStream) -> T): T? {
        val connection = URL(url).openConnection() as HttpURLConnection
        try {
            connection.connectTimeout = TIMEOUT_MS
            connection.readTimeout = TIMEOUT_MS
            // MusicBrainz 要求标明客户端
            connection.setRequestProperty("User-Agent", "JeekMusic/${BuildConfig.VERSION_NAME} (https://github.com/tifish/JeekMusic)")
            connection.setRequestProperty("Accept", "application/json, image/*")
            return when (val code = connection.responseCode) {
                200 -> connection.inputStream.use(read)
                404 -> null
                503 -> throw Unavailable()
                else -> throw IOException("HTTP $code for $url")
            }
        } finally {
            connection.disconnect()
        }
    }

    /** 两次请求之间至少间隔 [intervalMs]。 */
    private class RateLimit(private val intervalMs: Long) {
        private val mutex = Mutex()
        private var last = 0L

        suspend fun <T> run(block: () -> T): T = mutex.withLock {
            val wait = last + intervalMs - SystemClock.elapsedRealtime()
            if (wait > 0) delay(wait)
            try {
                block()
            } finally {
                last = SystemClock.elapsedRealtime()
            }
        }
    }

    private companion object {
        const val TIMEOUT_MS = 15_000
        const val ITUNES_INTERVAL_MS = 3_000L
        const val MUSICBRAINZ_INTERVAL_MS = 1_100L
        const val RETRIES = 2
        const val RETRY_DELAY_MS = 2_000L
        const val MIN_IMAGE_BYTES = 1_000
        const val MAX_IMAGE_BYTES = 8_000_000

        val simplified: Transliterator by lazy { Transliterator.getInstance("Traditional-Simplified") }
        val traditional: Transliterator by lazy { Transliterator.getInstance("Simplified-Traditional") }

        // Transliterator 不保证线程安全
        fun toSimplified(text: String): String =
            if (CoverMatcher.hasCjk(text)) synchronized(simplified) { simplified.transliterate(text) } else text
        fun toTraditional(text: String): String = synchronized(traditional) { traditional.transliterate(text) }

        fun encode(value: String): String = URLEncoder.encode(value, "UTF-8")

        /** 去掉会破坏 Lucene 查询的引号和反斜杠。 */
        fun luceneEscape(value: String): String = value.replace("\\", " ").replace("\"", " ").trim()
    }
}
