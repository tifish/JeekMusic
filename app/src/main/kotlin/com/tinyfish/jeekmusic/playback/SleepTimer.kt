package com.tinyfish.jeekmusic.playback

import android.os.SystemClock
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * 睡眠定时（需求 P-8，docs/design.md §7.6）：
 * - N 分钟后停止：最后 30 秒线性降低音量，然后暂停并恢复音量
 * - 播完当前这首后停止：在切换到下一首（或单曲循环重新开始）时暂停
 *
 * 状态通过 [onChanged] 回调给 Service，再由 session extras 同步给界面。
 */
class SleepTimer(
    private val player: Player,
    private val scope: CoroutineScope,
    private val onChanged: (endElapsedMs: Long?, endOfTrack: Boolean) -> Unit,
) {
    private var job: Job? = null
    private var endOfTrack = false

    private val listener = object : Player.Listener {
        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            if (endOfTrack && (reason == Player.MEDIA_ITEM_TRANSITION_REASON_AUTO || reason == Player.MEDIA_ITEM_TRANSITION_REASON_REPEAT)) {
                player.pause()
                cancel()
            }
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            if (endOfTrack && playbackState == Player.STATE_ENDED) cancel()
        }
    }

    init {
        player.addListener(listener)
    }

    fun startMinutes(minutes: Int) {
        cancel()
        val totalMs = minutes * 60_000L
        val endAt = SystemClock.elapsedRealtime() + totalMs
        job = scope.launch {
            delay((totalMs - FADE_MS).coerceAtLeast(0))
            val steps = 30
            for (i in 1..steps) {
                player.volume = 1f - i.toFloat() / steps
                delay(FADE_MS / steps)
            }
            player.pause()
            player.volume = 1f
            cancel()
        }
        onChanged(endAt, false)
    }

    fun startEndOfTrack() {
        cancel()
        endOfTrack = true
        onChanged(null, true)
    }

    fun cancel() {
        job?.cancel()
        job = null
        endOfTrack = false
        player.volume = 1f
        onChanged(null, false)
    }

    fun release() {
        job?.cancel()
        player.removeListener(listener)
    }

    private companion object {
        const val FADE_MS = 30_000L
    }
}
