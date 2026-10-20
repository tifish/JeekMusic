package com.tinyfish.jeekmusic.playback

import android.os.SystemClock
import androidx.annotation.OptIn
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.Timeline
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.analytics.AnalyticsListener
import androidx.media3.exoplayer.audio.AudioSink
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * 发现低功耗播放（offload）时"没声音、歌跳过一段"（需求 P-14，docs/design.md §7.2）。
 *
 * 有的设备上 offload 时音频 DSP 一下子取走几十秒的数据，报告的播放进度远远超前于实际听到的：开始一段没声音，
 * 这段歌被跳过，或者最后几十秒被截掉、提前换歌。播放器自己的进度也跟着超前，看不出来，所以按真实时间核对：
 * 从这首开始（或上次拖动）起实际播放了多久，加上起点，就是听到的位置（[heardMs]）。
 * - 播放中每秒核对一次，进度超前 [AHEAD_MS] 以上：退回听到的位置（每首最多一次）
 * - 自动换歌时这首还差 [AHEAD_MS] 以上，记为提前换歌
 * 两种情况发生时如果在用 offload，就认为这台设备的 offload 不可靠，调用 [onOffloadUnreliable]。
 */
@OptIn(UnstableApi::class)
class PlaybackDiagnostics(
    private val player: ExoPlayer,
    private val log: PlaybackLog,
    private val scope: CoroutineScope,
    private val onOffloadUnreliable: () -> Unit,
) : Player.Listener, AnalyticsListener {

    /**
     * 当前的音频输出是否是 offload。来自 [AnalyticsListener.onAudioTrackInitialized]；
     * `ExoPlayer.AudioOffloadListener.onOffloadedPlayback` 不会通过 `addAudioOffloadListener` 回调，不能用。
     */
    var offloaded = false
        private set

    private var anchorPositionMs = 0L
    private var playedMs = 0L
    private var playingSince = -1L
    private var rewoundThisItem = false
    private var checkJob: Job? = null
    private val window = Timeline.Window()

    fun start() {
        player.addListener(this)
        player.addAnalyticsListener(this)
        reanchor(player.currentPosition)
    }

    fun stop() {
        checkJob?.cancel()
        player.removeListener(this)
        player.removeAnalyticsListener(this)
    }

    override fun onAudioTrackInitialized(eventTime: AnalyticsListener.EventTime, audioTrackConfig: AudioSink.AudioTrackConfig) {
        if (offloaded == audioTrackConfig.offload) return
        offloaded = audioTrackConfig.offload
        log.add("offload ${if (offloaded) "on" else "off"}: ${title()} (encoding ${audioTrackConfig.encoding}, ${audioTrackConfig.sampleRate} Hz)")
    }

    override fun onIsPlayingChanged(isPlaying: Boolean) {
        log.add(
            "${if (isPlaying) "playing" else "stopped"} at ${player.currentPosition / 1000}s: ${title()}" +
                if (isPlaying) "" else " (state ${player.playbackState}, playWhenReady ${player.playWhenReady}, suppressed ${player.playbackSuppressionReason})",
        )
        val now = SystemClock.elapsedRealtime()
        checkJob?.cancel()
        if (isPlaying) {
            playingSince = now
            checkJob = scope.launch {
                while (isActive) {
                    delay(CHECK_INTERVAL_MS)
                    checkAhead()
                }
            }
        } else if (playingSince >= 0) {
            playedMs += now - playingSince
            playingSince = -1
        }
    }

    override fun onPositionDiscontinuity(oldPosition: Player.PositionInfo, newPosition: Player.PositionInfo, reason: Int) {
        if (reason == Player.DISCONTINUITY_REASON_AUTO_TRANSITION) {
            checkEnded(oldPosition)
        } else {
            log.add("jump (reason $reason): ${oldPosition.mediaItem?.mediaMetadata?.title ?: "?"} ${oldPosition.positionMs / 1000}s -> ${newPosition.mediaItem?.mediaMetadata?.title ?: "?"} ${newPosition.positionMs / 1000}s")
        }
        reanchor(newPosition.positionMs)
    }

    override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
        rewoundThisItem = false
        // 换了队列（比如启动时恢复到上次的位置）：不一定有位置跳变的回调，按现在的位置重新开始计
        if (reason == Player.MEDIA_ITEM_TRANSITION_REASON_PLAYLIST_CHANGED) {
            log.add("queue changed at ${player.currentPosition / 1000}s: ${title()}")
            reanchor(player.currentPosition)
        }
    }

    /** 从这首开始（或上次拖动）起实际听到的位置。 */
    private fun heardMs(): Long =
        anchorPositionMs + playedMs + (if (playingSince >= 0) SystemClock.elapsedRealtime() - playingSince else 0)

    /** 播放中：进度超前于听到的位置，说明有一段数据被取走了却没放出来。 */
    private fun checkAhead() {
        val position = player.currentPosition
        val heard = heardMs()
        val ahead = position - heard
        // 进度落后于真实时间（开始出声的延迟、卡顿）是正常的：听到的不会比进度多，以进度为准
        if (ahead < 0) {
            reanchor(position)
            return
        }
        if (ahead < AHEAD_MS) return
        val rewind = !rewoundThisItem
        log.add("AHEAD: ${title()} pos ${position / 1000}s, heard ${heard / 1000}s (${ahead / 1000}s ahead), offload=$offloaded${if (rewind) ", rewind" else ""}")
        if (offloaded) onOffloadUnreliable()
        if (rewind) {
            rewoundThisItem = true
            player.seekTo(heard)
        } else {
            reanchor(position)
        }
    }

    private fun checkEnded(old: Player.PositionInfo) {
        val timeline = player.currentTimeline
        if (old.mediaItemIndex !in 0 until timeline.windowCount) return
        val durationMs = timeline.getWindow(old.mediaItemIndex, window).durationMs
        if (durationMs == C.TIME_UNSET) return
        val heard = heardMs()
        val missing = durationMs - heard
        val name = old.mediaItem?.mediaMetadata?.title ?: "?"
        if (missing > AHEAD_MS) {
            // 播放器自己的进度（pos）也到了结尾：进度比实际出声跑得快；没到结尾：数据提前读完了
            log.add("EARLY next: $name heard ${heard / 1000}s of ${durationMs / 1000}s (${missing / 1000}s short), pos ${old.positionMs / 1000}s, offload=$offloaded")
            if (offloaded) onOffloadUnreliable()
        } else {
            log.add("next: $name ended (${durationMs / 1000}s), offload=$offloaded")
        }
    }

    private fun reanchor(positionMs: Long) {
        anchorPositionMs = positionMs
        playedMs = 0
        playingSince = if (player.isPlaying) SystemClock.elapsedRealtime() else -1
    }

    private fun title() = player.currentMediaItem?.mediaMetadata?.title ?: "?"

    private companion object {
        /** 真实时间的误差（开始出声的延迟、计时误差）远小于这个值。 */
        const val AHEAD_MS = 4_000L

        /** 用 delay（不唤醒 CPU）：熄屏 offload 时 CPU 休眠就不检查，醒着时照常核对。 */
        const val CHECK_INTERVAL_MS = 1_000L
    }
}
