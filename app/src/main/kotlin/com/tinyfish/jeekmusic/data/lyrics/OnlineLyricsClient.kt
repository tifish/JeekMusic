package com.tinyfish.jeekmusic.data.lyrics

import com.tinyfish.jeekmusic.data.cover.CoverMatcher
import com.tinyfish.jeekmusic.data.online.ChineseText
import com.tinyfish.jeekmusic.data.online.NetEaseMusic
import com.tinyfish.jeekmusic.data.online.OnlineSong
import com.tinyfish.jeekmusic.data.online.QqMusic
import com.tinyfish.jeekmusic.data.settings.OnlineSource
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton
import kotlin.math.abs

/**
 * 在线歌词的各个来源（需求 LY-8，docs/design.md §8.4）：LRCLIB 直接按歌名、艺术家、时长查；
 * 网易云音乐、QQ 音乐先搜歌，再取那首歌的歌词（常带翻译）。来源由用户在设置里选。
 */
@Singleton
class OnlineLyricsClient @Inject constructor(
    private val settings: SettingsRepository,
    private val lrcLib: LrcLibClient,
    private val netEase: NetEaseMusic,
    private val qq: QqMusic,
) {
    private val matcher = ChineseText.matcher

    /** 设置里启用的歌词来源，按尝试的顺序。 */
    suspend fun enabledSources(): List<OnlineSource> = settings.current().lyricsSources.let { set -> OnlineSource.FOR_LYRICS.filter { it in set } }

    /**
     * 自动获取用：按启用的来源依次找歌名、艺术家都对得上、时长相差不到 [DURATION_TOLERANCE_SEC] 秒的歌词。
     * 某个来源出错（比如国内打不开）时试下一个；都没找到返回 null；全部出错时抛出最后一个异常（不算"没找到"）。
     */
    suspend fun best(title: String, artist: String, album: String?, durationSec: Long): OnlineLyrics? = withContext(Dispatchers.IO) {
        var error: Throwable? = null
        var anySucceeded = false
        for (source in enabledSources()) {
            val found = runCatching {
                when (source) {
                    OnlineSource.LRCLIB -> lrcLib.get(title, artist, album, durationSec)
                    else -> songs(source, title, artist)
                        .filter { matches(it, title, artist) && durationClose(it.durationMs, durationSec) }
                        .take(2)
                        .firstNotNullOfOrNull { lyricsOf(it) }
                }
            }.onFailure { error = it }
            if (found.isSuccess) anySucceeded = true
            found.getOrNull()?.let { return@withContext it }
        }
        if (!anySucceeded) error?.let { throw it }
        null
    }

    /**
     * 手动搜索一个来源；网易云、QQ 音乐取前几首的歌词一起返回，没有歌词的不列出。
     * 标出歌名、艺术家对得上的（[OnlineLyrics.matched]），界面上排在前面。网络出错时抛出异常。
     */
    suspend fun search(source: OnlineSource, title: String, artist: String?): List<OnlineLyrics> = withContext(Dispatchers.IO) {
        val found = when (source) {
            OnlineSource.LRCLIB -> lrcLib.search(title, artist)
            else -> coroutineScope {
                songs(source, title, artist).take(SEARCH_COUNT).map { song -> async { runCatching { lyricsOf(song) }.getOrNull() } }
                    .awaitAll().filterNotNull()
            }
        }
        found.map { it.copy(matched = matcher.titleMatches(title, it.trackName) && (artist.isNullOrBlank() || matcher.artistMatches(artist, it.artistName))) }
    }

    private suspend fun songs(source: OnlineSource, title: String, artist: String?): List<OnlineSong> {
        val query = listOfNotNull(artist?.takeIf { it.isNotBlank() }?.let(CoverMatcher::primaryArtist), title).joinToString(" ")
        return when (source) {
            OnlineSource.NETEASE -> netEase.search(query)
            OnlineSource.QQ -> qq.search(query)
            else -> emptyList()
        }
    }

    private suspend fun lyricsOf(song: OnlineSong): OnlineLyrics? {
        val text = when (song.source) {
            OnlineSource.NETEASE -> netEase.lyrics(song.id)
            OnlineSource.QQ -> qq.lyrics(song.id)
            else -> null
        } ?: return null
        val parsed = LrcParser.parse(text)
        if (parsed.lines.isEmpty()) return null
        return OnlineLyrics(
            source = song.source,
            id = song.id,
            trackName = song.title,
            artistName = song.artist,
            albumName = song.album,
            durationSec = song.durationMs?.div(1000.0),
            synced = text.takeIf { parsed.synced },
            plain = text.takeUnless { parsed.synced },
        )
    }

    private fun matches(song: OnlineSong, title: String, artist: String): Boolean =
        matcher.titleMatches(title, song.title) && matcher.artistMatches(artist, song.artist)

    private fun durationClose(onlineMs: Long?, localSec: Long): Boolean =
        onlineMs == null || localSec <= 0 || abs(onlineMs / 1000.0 - localSec) <= DURATION_TOLERANCE_SEC

    private companion object {
        const val DURATION_TOLERANCE_SEC = 5
        const val SEARCH_COUNT = 6
    }
}
