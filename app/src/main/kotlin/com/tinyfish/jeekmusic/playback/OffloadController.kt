package com.tinyfish.jeekmusic.playback

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.PowerManager
import android.util.Log
import androidx.annotation.OptIn
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.ProcessLifecycleOwner
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionParameters.AudioOffloadPreferences
import androidx.media3.common.util.UnstableApi
import com.tinyfish.jeekmusic.data.settings.LowPowerMode

/**
 * 低功耗播放（docs/design.md §7.2）：按设置在合适的时候开启音频 offload，把解码交给音频 DSP。
 * - BACKGROUND（默认）：熄屏或 App 在后台时开启
 * - ALWAYS：始终开启
 * - OFF：始终关闭
 * 要求支持无缝播放，设备或格式不支持时 ExoPlayer 自动走普通路径。
 *
 * **不在一首歌播放到一半时切换**：切换 offload 要重建音频输出，正在出声时会听到短暂的中断（需求 P-12）。
 * 需要切换时先记下来，等到本来就会断开或换歌的时候再切：暂停 / 停止、用户拖动进度或切歌、自动播放到下一首。
 */
class OffloadController(private val context: Context, private val player: Player) {

    var mode: LowPowerMode = LowPowerMode.BACKGROUND
        set(value) {
            field = value
            apply("mode")
        }

    private var screenOn = context.getSystemService(PowerManager::class.java).isInteractive

    /** 播放出错后暂时不用 offload，到下一首再恢复（[suspendUntilNextItem]）。 */
    private var suspendedUntilNextItem = false

    /** 这次播放服务运行期间不再用 offload（[disableForSession]）。 */
    private var disabledForSession = false

    /** 出错后重试这首歌时调用：先用普通播放，下一首再按设置来（需求 P-14）。 */
    fun suspendUntilNextItem() {
        suspendedUntilNextItem = true
        apply("error", atBoundary = true)
    }

    /** 发现 offload 下提前换歌（需求 P-14）：服务运行期间改用普通播放，重启后恢复。 */
    fun disableForSession() {
        disabledForSession = true
        apply("early transition", atBoundary = true)
    }
    private var appForeground = false

    private val screenReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            screenOn = intent.action == Intent.ACTION_SCREEN_ON
            apply("screen")
        }
    }

    private val lifecycleObserver = object : DefaultLifecycleObserver {
        override fun onStart(owner: LifecycleOwner) {
            appForeground = true
            apply("foreground")
        }

        override fun onStop(owner: LifecycleOwner) {
            appForeground = false
            apply("background")
        }
    }

    /** 等到不出声或换歌的时候，把还没生效的设置应用上去。 */
    private val playerListener = object : Player.Listener {
        override fun onIsPlayingChanged(isPlaying: Boolean) {
            if (!isPlaying) apply("paused")
        }

        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            suspendedUntilNextItem = false
            apply("next item", atBoundary = true)
        }

        override fun onPositionDiscontinuity(oldPosition: Player.PositionInfo, newPosition: Player.PositionInfo, reason: Int) {
            // 用户拖动进度条时本来就会断一下
            if (reason == Player.DISCONTINUITY_REASON_SEEK) apply("seek", atBoundary = true)
        }
    }

    fun start() {
        context.registerReceiver(screenReceiver, IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_ON)
            addAction(Intent.ACTION_SCREEN_OFF)
        })
        ProcessLifecycleOwner.get().lifecycle.addObserver(lifecycleObserver)
        player.addListener(playerListener)
        apply("start")
    }

    fun stop() {
        runCatching { context.unregisterReceiver(screenReceiver) }
        ProcessLifecycleOwner.get().lifecycle.removeObserver(lifecycleObserver)
        player.removeListener(playerListener)
    }

    /** 按当前档位、屏幕和前后台状态，应该开启 offload 吗。 */
    private val wanted: Boolean
        get() = when {
            suspendedUntilNextItem || disabledForSession -> false
            mode == LowPowerMode.ALWAYS -> true
            mode == LowPowerMode.OFF -> false
            else -> !screenOn || !appForeground
        }

    /** 当前是否请求了 offload（可能还在等待合适的时机切换），供歌曲信息页显示。 */
    val enabled: Boolean
        @OptIn(UnstableApi::class)
        get() = player.trackSelectionParameters.audioOffloadPreferences.audioOffloadMode == AudioOffloadPreferences.AUDIO_OFFLOAD_MODE_ENABLED

    /**
     * 正在出声时不切换，等下一个时机（见类注释）；[atBoundary] 表示现在正好是换歌或拖动进度的时候，可以切。
     */
    @OptIn(UnstableApi::class)
    private fun apply(reason: String, atBoundary: Boolean = false) {
        val preferences = AudioOffloadPreferences.Builder()
            .setAudioOffloadMode(if (wanted) AudioOffloadPreferences.AUDIO_OFFLOAD_MODE_ENABLED else AudioOffloadPreferences.AUDIO_OFFLOAD_MODE_DISABLED)
            .setIsGaplessSupportRequired(true)
            .build()
        if (player.trackSelectionParameters.audioOffloadPreferences == preferences) return
        if (player.isPlaying && !atBoundary) {
            Log.i(TAG, "offload ${if (wanted) "on" else "off"} deferred ($reason)")
            return
        }
        Log.i(TAG, "offload ${if (wanted) "on" else "off"} ($reason)")
        player.trackSelectionParameters = player.trackSelectionParameters.buildUpon()
            .setAudioOffloadPreferences(preferences)
            .build()
    }

    private companion object {
        const val TAG = "OffloadController"
    }
}
