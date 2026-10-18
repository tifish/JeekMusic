package com.tinyfish.jeekmusic.playback

import android.os.SystemClock
import androidx.annotation.OptIn
import androidx.media3.common.C
import androidx.media3.common.Player
import androidx.media3.common.Timeline
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer

/**
 * 发现"歌还没放完就换到下一首"（需求 P-14，docs/design.md §7.2）。
 *
 * 低功耗播放（offload）时，有的设备上音频 DSP 报告的播放进度比实际听到的超前，播放器以为这首放完了就换歌，
 * 最后几秒被截掉。这时播放器自己的进度也是"放完了"，看不出来，所以按真实时间核对：从这首开始（或上次拖动）
 * 起实际播放了多久，加上起点，和这首的时长比。自动换歌时还差 [EARLY_MS] 以上，就算提前换歌：记入日志；
 * 如果当时在用 offload，这次服务运行期间改用普通播放。
 */
@OptIn(UnstableApi::class)
class PlaybackDiagnostics(
    private val player: ExoPlayer,
    private val offload: OffloadController,
    private val log: PlaybackLog,
) : Player.Listener, ExoPlayer.AudioOffloadListener {

    /** 实际是否在用 offload（设备、格式都支持时才会）。 */
    var offloaded = false
        private set

    private var anchorPositionMs = 0L
    private var playedMs = 0L
    private var playingSince = -1L
    private val window = Timeline.Window()

    fun start() {
        player.addListener(this)
        player.addAudioOffloadListener(this)
        reanchor(player.currentPosition)
    }

    fun stop() {
        player.removeListener(this)
        player.removeAudioOffloadListener(this)
    }

    override fun onOffloadedPlayback(offloadedPlayback: Boolean) {
        if (offloaded == offloadedPlayback) return
        offloaded = offloadedPlayback
        log.add("offload ${if (offloadedPlayback) "on" else "off"}: ${title()}")
    }

    override fun onIsPlayingChanged(isPlaying: Boolean) {
        log.add(
            "${if (isPlaying) "playing" else "stopped"} at ${player.currentPosition / 1000}s: ${title()}" +
                if (isPlaying) "" else " (state ${player.playbackState}, playWhenReady ${player.playWhenReady}, suppressed ${player.playbackSuppressionReason})",
        )
        val now = SystemClock.elapsedRealtime()
        if (isPlaying) {
            playingSince = now
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

    /** 换了队列（比如启动时恢复到上次的位置）：不一定有位置跳变的回调，按现在的位置重新开始计。 */
    override fun onMediaItemTransition(mediaItem: androidx.media3.common.MediaItem?, reason: Int) {
        if (reason == Player.MEDIA_ITEM_TRANSITION_REASON_PLAYLIST_CHANGED) {
            log.add("queue changed at ${player.currentPosition / 1000}s: ${title()}")
            reanchor(player.currentPosition)
        }
    }

    private fun checkEnded(old: Player.PositionInfo) {
        val timeline = player.currentTimeline
        if (old.mediaItemIndex !in 0 until timeline.windowCount) return
        val durationMs = timeline.getWindow(old.mediaItemIndex, window).durationMs
        if (durationMs == C.TIME_UNSET) return
        val heard = anchorPositionMs + playedMs + (if (playingSince >= 0) SystemClock.elapsedRealtime() - playingSince else 0)
        val missing = durationMs - heard
        val name = old.mediaItem?.mediaMetadata?.title ?: "?"
        if (missing > EARLY_MS) {
            // 播放器自己的进度（pos）也到了结尾：进度比实际出声跑得快；没到结尾：数据提前读完了
            log.add("EARLY next: $name heard ${heard / 1000}s of ${durationMs / 1000}s (${missing / 1000}s short), pos ${old.positionMs / 1000}s, offload=$offloaded")
            if (offloaded) offload.disableForSession()
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
        const val EARLY_MS = 3_000L
    }
}
