package com.tinyfish.jeekmusic.dev

import android.content.Context
import android.os.SystemClock
import com.kyant.taglib.TagLib
import com.tinyfish.jeekmusic.data.scanner.CharsetFixer
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import java.util.concurrent.atomic.AtomicInteger
import javax.inject.Inject

data class TagLibReport(
    val total: Int,
    val succeeded: Int,
    val failed: Int,
    val elapsedMs: Long,
    val multiArtist: Int,
    val mojibakeFixed: Int,
    val withLyrics: Int,
    val withCueSheet: Int,
    val samples: List<String>,
) {
    val avgMs: Double get() = if (total == 0) 0.0 else elapsedMs.toDouble() / total
}

/**
 * M0 技术验证：用 TagLib 读取全部歌曲的标签，统计耗时、多值艺术家、GBK 乱码修复、内嵌歌词和 CUE。
 * 与设计中的扫描器一样，用 4 路并发解析（docs/design.md §4.2）。
 */
class TagLibProbe @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    suspend fun run(tracks: List<ProbeTrack>, onProgress: (Int) -> Unit): TagLibReport =
        withContext(Dispatchers.IO.limitedParallelism(PARALLELISM)) {
            val done = AtomicInteger()
            val start = SystemClock.elapsedRealtime()
            val results = coroutineScope {
                tracks.map { track ->
                    async {
                        readOne(track).also { onProgress(done.incrementAndGet()) }
                    }
                }.awaitAll()
            }
            val elapsed = SystemClock.elapsedRealtime() - start

            val ok = results.filterNotNull()
            TagLibReport(
                total = tracks.size,
                succeeded = ok.size,
                failed = tracks.size - ok.size,
                elapsedMs = elapsed,
                multiArtist = ok.count { it.artists.size > 1 },
                mojibakeFixed = ok.count { it.fixedFrom != null },
                withLyrics = ok.count { it.hasLyrics },
                withCueSheet = ok.count { it.hasCueSheet },
                samples = buildSamples(ok),
            )
        }

    private class Result(
        val fileName: String,
        val artists: List<String>,
        val title: String?,
        val fixedFrom: String?,
        val hasLyrics: Boolean,
        val hasCueSheet: Boolean,
    )

    private fun readOne(track: ProbeTrack): Result? = runCatching {
        context.contentResolver.openFileDescriptor(track.uri, "r")?.use { pfd ->
            // TagLib 的 FileStream 会关闭传入的 fd，所以每次调用都传一个 dup 出来的副本
            val metadata = TagLib.getMetadata(pfd.dup().detachFd(), readPictures = false) ?: return null
            val map = metadata.propertyMap
            val rawArtists = map["ARTIST"]?.toList().orEmpty()
            val rawTitle = map["TITLE"]?.firstOrNull()
            val artists = CharsetFixer.fixAll(rawArtists)
            val title = rawTitle?.let(CharsetFixer::fix)
            val changed = artists != rawArtists || title != rawTitle
            Result(
                fileName = track.fileName,
                artists = artists,
                title = title,
                fixedFrom = if (changed) (rawTitle ?: rawArtists.firstOrNull()) else null,
                hasLyrics = !map["LYRICS"].isNullOrEmpty(),
                hasCueSheet = !map["CUESHEET"].isNullOrEmpty(),
            )
        }
    }.getOrNull()

    private fun buildSamples(ok: List<Result>): List<String> = buildList {
        ok.filter { it.fixedFrom != null }.take(SAMPLE_COUNT).forEach {
            add("乱码修复：${it.fixedFrom} → ${it.title ?: it.artists.firstOrNull()}")
        }
        ok.filter { it.artists.size > 1 }.take(SAMPLE_COUNT).forEach {
            add("多值艺术家：${it.artists.joinToString(" | ")}（${it.fileName}）")
        }
        ok.filter { it.hasCueSheet }.take(SAMPLE_COUNT).forEach {
            add("内嵌 CUE：${it.fileName}")
        }
    }

    private companion object {
        const val PARALLELISM = 4
        const val SAMPLE_COUNT = 5
    }
}
