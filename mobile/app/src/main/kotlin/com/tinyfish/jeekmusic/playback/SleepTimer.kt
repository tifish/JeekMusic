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
 * - N 分钟后、播完当时正在播的这首再停止（[finishSong]）：时间到后转为"再播 1 首"，不渐弱
 * - 播完 N 首后停止：每首自然播完（或单曲循环重新开始）时计数减一，减到 0 时暂停；手动切歌不计数
 *
 * 状态通过 [onChanged] 回调给 Service，再由 session extras 同步给界面。
 */
class SleepTimer(
    private val player: Player,
    private val scope: CoroutineScope,
    private val onChanged: (Status) -> Unit,
) {
    /** [endElapsedMs] 是 SystemClock.elapsedRealtime() 时间，0 表示不按时间；[songsLeft] 为 0 表示不按歌曲数。 */
    data class Status(val endElapsedMs: Long = 0, val songsLeft: Int = 0, val finishSong: Boolean = false)

    private var job: Job? = null
    private var status = Status()

    private val listener = object : Player.Listener {
        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            if (status.songsLeft <= 0) return
            if (reason != Player.MEDIA_ITEM_TRANSITION_REASON_AUTO && reason != Player.MEDIA_ITEM_TRANSITION_REASON_REPEAT) return
            val left = status.songsLeft - 1
            if (left == 0) {
                player.pause()
                cancel()
            } else {
                publish(status.copy(songsLeft = left))
            }
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            // 队列播完了，定时也就没有意义了
            if (playbackState == Player.STATE_ENDED && status.songsLeft > 0) cancel()
        }
    }

    init {
        player.addListener(listener)
    }

    fun startMinutes(minutes: Int, finishSong: Boolean) {
        cancel()
        val totalMs = minutes * 60_000L
        publish(Status(endElapsedMs = SystemClock.elapsedRealtime() + totalMs, finishSong = finishSong))
        job = scope.launch {
            if (finishSong) {
                delay(totalMs)
                publish(Status(songsLeft = 1, finishSong = true))
                return@launch
            }
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
    }

    fun startSongs(count: Int) {
        cancel()
        publish(Status(songsLeft = count.coerceAtLeast(1)))
    }

    fun cancel() {
        job?.cancel()
        job = null
        player.volume = 1f
        publish(Status())
    }

    fun release() {
        job?.cancel()
        player.removeListener(listener)
    }

    private fun publish(newStatus: Status) {
        status = newStatus
        onChanged(newStatus)
    }

    private companion object {
        const val FADE_MS = 30_000L
    }
}
