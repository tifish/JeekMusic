package com.tinyfish.jeekmusic.playback

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.PowerManager
import androidx.annotation.OptIn
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.ProcessLifecycleOwner
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
 */
class OffloadController(private val context: Context, private val player: Player) {

    var mode: LowPowerMode = LowPowerMode.BACKGROUND
        set(value) {
            field = value
            apply()
        }

    private var screenOn = context.getSystemService(PowerManager::class.java).isInteractive
    private var appForeground = false

    private val screenReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            screenOn = intent.action == Intent.ACTION_SCREEN_ON
            apply()
        }
    }

    private val lifecycleObserver = object : DefaultLifecycleObserver {
        override fun onStart(owner: LifecycleOwner) {
            appForeground = true
            apply()
        }

        override fun onStop(owner: LifecycleOwner) {
            appForeground = false
            apply()
        }
    }

    fun start() {
        context.registerReceiver(screenReceiver, IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_ON)
            addAction(Intent.ACTION_SCREEN_OFF)
        })
        ProcessLifecycleOwner.get().lifecycle.addObserver(lifecycleObserver)
        apply()
    }

    fun stop() {
        runCatching { context.unregisterReceiver(screenReceiver) }
        ProcessLifecycleOwner.get().lifecycle.removeObserver(lifecycleObserver)
    }

    /** 当前是否请求了 offload，供歌曲信息页显示。 */
    val enabled: Boolean
        get() = when (mode) {
            LowPowerMode.ALWAYS -> true
            LowPowerMode.OFF -> false
            LowPowerMode.BACKGROUND -> !screenOn || !appForeground
        }

    @OptIn(UnstableApi::class)
    private fun apply() {
        val preferences = AudioOffloadPreferences.Builder()
            .setAudioOffloadMode(if (enabled) AudioOffloadPreferences.AUDIO_OFFLOAD_MODE_ENABLED else AudioOffloadPreferences.AUDIO_OFFLOAD_MODE_DISABLED)
            .setIsGaplessSupportRequired(true)
            .build()
        if (player.trackSelectionParameters.audioOffloadPreferences == preferences) return
        player.trackSelectionParameters = player.trackSelectionParameters.buildUpon()
            .setAudioOffloadPreferences(preferences)
            .build()
    }
}
