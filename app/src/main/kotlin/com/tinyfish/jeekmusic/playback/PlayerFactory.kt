package com.tinyfish.jeekmusic.playback

import android.content.Context
import androidx.annotation.OptIn
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject

/**
 * 按"不做任何有损处理"的原则配置播放器（docs/design.md §7.1）：
 * - 浮点输出：高位深音源不截断到 16-bit，同时旁路 ExoPlayer 的音频处理链
 * - 不改变速度、音调，不跳过静音，不挂任何 AudioProcessor
 */
class PlayerFactory @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    @OptIn(UnstableApi::class)
    fun create(): ExoPlayer {
        val renderersFactory = DefaultRenderersFactory(context)
            .setEnableAudioFloatOutput(true)

        val audioAttributes = AudioAttributes.Builder()
            .setUsage(C.USAGE_MEDIA)
            .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
            .build()

        return ExoPlayer.Builder(context, renderersFactory)
            .setAudioAttributes(audioAttributes, /* handleAudioFocus = */ true)
            .setHandleAudioBecomingNoisy(true)
            .setWakeMode(C.WAKE_MODE_LOCAL)
            .setSkipSilenceEnabled(false)
            .build()
    }
}
