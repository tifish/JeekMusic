package com.tinyfish.jeekmusic.data.cover

import com.tinyfish.jeekmusic.data.online.ChineseText
import com.tinyfish.jeekmusic.data.online.Http
import com.tinyfish.jeekmusic.data.online.NetEaseMusic
import com.tinyfish.jeekmusic.data.online.OnlineSong
import com.tinyfish.jeekmusic.data.online.QqMusic
import com.tinyfish.jeekmusic.data.settings.OnlineSource
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import javax.inject.Inject
import javax.inject.Singleton

/** 一个在线封面候选。[imageUrls] 按清晰度从高到低，前一个下载失败时用后一个。 */
data class CoverCandidate(
    val source: OnlineSource,
    val title: String,
    val artist: String,
    val album: String?,
    val thumbUrl: String,
    val imageUrls: List<String>,
    /** 和要找的歌对得上的程度：0 对不上，1 歌名和艺术家对得上，2 连括号里的附注也一样。手动搜索时用来跨来源排序。 */
    val score: Int = 0,
)

/**
 * 在线封面（需求 C-3，docs/design.md §7.4）。来源由用户在设置里选，按 [OnlineSource] 的顺序尝试：
 * - 网易云音乐、QQ 音乐：国内访问稳定，中文歌全（非官方接口）
 * - iTunes：中文歌名只有繁体能搜到，含汉字时转成繁体、查台湾区；其余查美国区。限速约 20 次 / 分钟
 * - MusicBrainz：要求标明客户端、每秒不超过 1 次；封面取 Cover Art Archive 发行组的代表封面，图片在 archive.org，国内通常打不开
 * 只在用户打开"在线封面"后才会被调用。
 */
@Singleton
class CoverClient @Inject constructor(
    private val settings: SettingsRepository,
    private val netEase: NetEaseMusic,
    private val qq: QqMusic,
) {
    private val matcher = ChineseText.matcher
    private val iTunesLimit = Http.RateLimit(ITUNES_INTERVAL_MS)
    private val musicBrainzLimit = Http.RateLimit(MUSICBRAINZ_INTERVAL_MS)

    /** 设置里启用的封面来源，按尝试的顺序。 */
    suspend fun enabledSources(): List<OnlineSource> = settings.current().coverSources.let { set -> OnlineSource.FOR_COVERS.filter { it in set } }

    /**
     * 手动搜索一个来源：对得上歌名和艺术家的排在前面。各来源分开搜，界面先显示先返回的。
     * 网络出错时抛出异常。
     */
    suspend fun search(source: OnlineSource, title: String, artist: String?): List<CoverCandidate> = withContext(Dispatchers.IO) {
        candidates(source, title, artist, strict = false).take(8)
            .map { it.copy(score = score(it, title, artist)) }
            .sortedByDescending { it.score }
    }

    /** 下载缩略图；失败时返回 null（MusicBrainz 的发行组不一定有封面）。 */
    suspend fun thumbnail(candidate: CoverCandidate): ByteArray? = withContext(Dispatchers.IO) { download(candidate.thumbUrl) }

    /**
     * 批量获取：按启用的来源依次找第一个歌名和艺术家都对得上的结果，返回封面图片；都找不到返回 null。
     * 某个来源出错（比如国内打不开）时跳过它试下一个；全部出错时抛出最后一个异常，由调用方决定是否重试。
     */
    suspend fun fetchBest(title: String, artist: String, album: String?): ByteArray? = withContext(Dispatchers.IO) {
        var error: Throwable? = null
        var anySucceeded = false
        for (source in enabledSources()) {
            val found = runCatching { candidates(source, title, artist, strict = true) }
                .onFailure { error = it }
                .getOrNull() ?: continue
            anySucceeded = true
            val ordered = found.filter { matches(it, title, artist) }.sortedWith(
                compareByDescending<CoverCandidate> { c -> album != null && c.album != null && matcher.titleMatches(album, c.album) }
                    .thenByDescending { score(it, title, artist) },
            )
            ordered.take(4).firstNotNullOfOrNull { downloadImage(it) }?.let { return@withContext it }
        }
        if (!anySucceeded) error?.let { throw it }
        null
    }

    /** 下载候选的大图；都失败时返回 null。 */
    suspend fun downloadImage(candidate: CoverCandidate): ByteArray? = withContext(Dispatchers.IO) {
        candidate.imageUrls.firstNotNullOfOrNull { download(it) }
    }

    private suspend fun candidates(source: OnlineSource, title: String, artist: String?, strict: Boolean): List<CoverCandidate> {
        val query = listOfNotNull(artist?.takeIf { it.isNotBlank() }?.let(CoverMatcher::primaryArtist), title).joinToString(" ")
        return when (source) {
            OnlineSource.NETEASE -> netEase.search(query).mapNotNull(::fromSong)
            OnlineSource.QQ -> qq.search(query).mapNotNull(::fromSong)
            OnlineSource.ITUNES -> iTunes(query, limit = 12)
            OnlineSource.MUSICBRAINZ -> musicBrainz(title, artist, minScore = if (strict) 90 else 80)
            OnlineSource.LRCLIB -> emptyList()
        }.distinctBy { it.thumbUrl }
    }

    private fun fromSong(song: OnlineSong): CoverCandidate? {
        val thumb = song.thumbUrl ?: return null
        val image = song.imageUrl ?: return null
        return CoverCandidate(song.source, song.title, song.artist, song.album, thumb, listOf(image, thumb))
    }

    private fun matches(c: CoverCandidate, title: String, artist: String?): Boolean =
        matcher.titleMatches(title, c.title) && (artist.isNullOrBlank() || matcher.artistMatches(artist, c.artist))

    /** 对不上为 0；对得上为 1；连括号里的附注也一样（不是 Remix、Live 等其他版本）为 2。 */
    private fun score(c: CoverCandidate, title: String, artist: String?): Int = when {
        !matches(c, title, artist) -> 0
        matcher.normalize(title) == matcher.normalize(c.title) -> 2
        else -> 1
    }

    private suspend fun iTunes(term: String, limit: Int): List<CoverCandidate> {
        val cjk = CoverMatcher.hasCjk(term)
        val query = "term=" + Http.encode(if (cjk) ChineseText.toTraditional(term) else term) +
            "&entity=song&limit=$limit&country=" + (if (cjk) "tw" else "us")
        val body = iTunesLimit.run { Http.text("https://itunes.apple.com/search?$query") } ?: return emptyList()
        val results = JSONObject(body).optJSONArray("results") ?: return emptyList()
        return List(results.length()) { results.getJSONObject(it) }.mapNotNull { r ->
            val art = r.optString("artworkUrl100").takeIf { it.contains("100x100") } ?: return@mapNotNull null
            CoverCandidate(
                source = OnlineSource.ITUNES,
                title = r.optString("trackName"),
                artist = r.optString("artistName"),
                album = r.optString("collectionName").takeIf { it.isNotEmpty() },
                thumbUrl = art.replace("100x100", "300x300"),
                imageUrls = listOf(art.replace("100x100", "1000x1000"), art.replace("100x100", "600x600")),
            )
        }
    }

    private suspend fun musicBrainz(title: String, artist: String?, minScore: Int): List<CoverCandidate> {
        val query = buildString {
            append("recording:\"").append(luceneEscape(title)).append('"')
            if (!artist.isNullOrBlank()) append(" AND artist:\"").append(luceneEscape(CoverMatcher.primaryArtist(artist))).append('"')
        }
        val body = musicBrainzLimit.run {
            Http.text("https://musicbrainz.org/ws/2/recording?fmt=json&limit=10&query=" + Http.encode(query))
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
                            source = OnlineSource.MUSICBRAINZ,
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

    /** 下载图片；失败或大小不像图片时返回 null。 */
    private fun download(url: String): ByteArray? = Http.bytes(url, mapOf("User-Agent" to Http.BROWSER_AGENT))
        ?.takeIf { it.size in MIN_IMAGE_BYTES..MAX_IMAGE_BYTES }

    private companion object {
        const val ITUNES_INTERVAL_MS = 3_000L
        const val MUSICBRAINZ_INTERVAL_MS = 1_100L
        const val MIN_IMAGE_BYTES = 1_000
        const val MAX_IMAGE_BYTES = 8_000_000

        /** 去掉会破坏 Lucene 查询的引号和反斜杠。 */
        fun luceneEscape(value: String): String = value.replace("\\", " ").replace("\"", " ").trim()
    }
}
