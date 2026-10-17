package com.tinyfish.jeekmusic.data.online

import android.os.SystemClock
import com.tinyfish.jeekmusic.data.lyrics.LrcLibClient
import com.tinyfish.jeekmusic.data.settings.OnlineSource
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import javax.inject.Inject

/**
 * 检测各在线来源能不能连上（需求 LY-8、C-3）：国内网络下有的来源打不开或很慢，让用户据此选择。
 * 每个来源发一个最轻的请求，记下耗时。
 */
class SourceCheck @Inject constructor(
    private val netEase: NetEaseMusic,
    private val qq: QqMusic,
    private val lrcLib: LrcLibClient,
) {
    /** 一项检测：[imageHost] 为 true 时检测的是来源的图片服务器（MusicBrainz 的封面图片在 archive.org）；[millis] 为 null 表示连不上。 */
    data class Result(val source: OnlineSource, val imageHost: Boolean, val millis: Long?)

    suspend fun check(sources: List<OnlineSource>): List<Result> = withContext(Dispatchers.IO) {
        val probes = sources.flatMap { source ->
            listOf(source to false) + if (source == OnlineSource.MUSICBRAINZ) listOf(source to true) else emptyList()
        }
        coroutineScope {
            probes.map { (source, imageHost) -> async { Result(source, imageHost, time { probe(source, imageHost) }) } }.awaitAll()
        }
    }

    private suspend fun probe(source: OnlineSource, imageHost: Boolean) {
        when {
            imageHost -> Http.text("https://archive.org/robots.txt", timeoutMs = TIMEOUT_MS)
            source == OnlineSource.NETEASE -> netEase.search("test", count = 1)
            source == OnlineSource.QQ -> qq.search("test", count = 1)
            source == OnlineSource.ITUNES -> Http.text("https://itunes.apple.com/search?term=test&limit=1", timeoutMs = TIMEOUT_MS)
            source == OnlineSource.MUSICBRAINZ -> Http.text("https://musicbrainz.org/ws/2/recording?query=test&limit=1&fmt=json", timeoutMs = TIMEOUT_MS)
            source == OnlineSource.LRCLIB -> lrcLib.get("test", "test", null, 0)
        }
    }

    /** 成功时返回耗时（毫秒），出错或超时（连接、读取各有超时）返回 null。 */
    private suspend fun time(block: suspend () -> Unit): Long? {
        val start = SystemClock.elapsedRealtime()
        return if (runCatching { block() }.isSuccess) SystemClock.elapsedRealtime() - start else null
    }

    private companion object {
        const val TIMEOUT_MS = 8_000
    }
}
