package com.tinyfish.jeekmusic.playback

import androidx.annotation.OptIn
import androidx.media3.common.AudioAttributes
import androidx.media3.common.DeviceInfo
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Metadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.PlaybackParameters
import androidx.media3.common.Player
import androidx.media3.common.Timeline
import androidx.media3.common.TrackSelectionParameters
import androidx.media3.common.Tracks
import androidx.media3.common.VideoSize
import androidx.media3.common.text.Cue
import androidx.media3.common.text.CueGroup
import androidx.media3.common.util.UnstableApi

/**
 * 转发全部回调的 Player.Listener，只改写"可用命令变化"的参数（[NextAlwaysPlayer] 用它补上"下一首"）。
 *
 * 不能用 Kotlin 的 `by` 委托：Player.Listener 的方法都是 Java 默认方法，`by` 不会转发它们，
 * 播放状态等事件会丢掉（NextAlwaysPlayerTest 验证过）。方法列表按 Media3 1.11 的 Player.Listener 逐个写出；
 * 升级 Media3 后如果新增了回调，NextAlwaysPlayerTest 会提醒补上。
 */
@OptIn(UnstableApi::class)
@Suppress("DEPRECATION", "OVERRIDE_DEPRECATION")
internal class CommandsListener(
    private val delegate: Player.Listener,
    private val transform: (Player.Commands) -> Player.Commands,
) : Player.Listener {
    override fun onEvents(p0: Player, p1: Player.Events) = delegate.onEvents(p0, p1)
    override fun onTimelineChanged(p0: Timeline, p1: Int) = delegate.onTimelineChanged(p0, p1)
    override fun onMediaItemTransition(p0: MediaItem?, p1: Int) = delegate.onMediaItemTransition(p0, p1)
    override fun onTracksChanged(p0: Tracks) = delegate.onTracksChanged(p0)
    override fun onMediaMetadataChanged(p0: MediaMetadata) = delegate.onMediaMetadataChanged(p0)
    override fun onPlaylistMetadataChanged(p0: MediaMetadata) = delegate.onPlaylistMetadataChanged(p0)
    override fun onIsLoadingChanged(p0: Boolean) = delegate.onIsLoadingChanged(p0)
    override fun onLoadingChanged(p0: Boolean) = delegate.onLoadingChanged(p0)
    override fun onAvailableCommandsChanged(p0: Player.Commands) = delegate.onAvailableCommandsChanged(transform(p0))
    override fun onTrackSelectionParametersChanged(p0: TrackSelectionParameters) = delegate.onTrackSelectionParametersChanged(p0)
    override fun onPlayerStateChanged(p0: Boolean, p1: Int) = delegate.onPlayerStateChanged(p0, p1)
    override fun onPlaybackStateChanged(p0: Int) = delegate.onPlaybackStateChanged(p0)
    override fun onPlayWhenReadyChanged(p0: Boolean, p1: Int) = delegate.onPlayWhenReadyChanged(p0, p1)
    override fun onPlaybackSuppressionReasonChanged(p0: Int) = delegate.onPlaybackSuppressionReasonChanged(p0)
    override fun onIsPlayingChanged(p0: Boolean) = delegate.onIsPlayingChanged(p0)
    override fun onRepeatModeChanged(p0: Int) = delegate.onRepeatModeChanged(p0)
    override fun onShuffleModeEnabledChanged(p0: Boolean) = delegate.onShuffleModeEnabledChanged(p0)
    override fun onPlayerError(p0: PlaybackException) = delegate.onPlayerError(p0)
    override fun onPlayerErrorChanged(p0: PlaybackException?) = delegate.onPlayerErrorChanged(p0)
    override fun onPositionDiscontinuity(p0: Int) = delegate.onPositionDiscontinuity(p0)
    override fun onPositionDiscontinuity(p0: Player.PositionInfo, p1: Player.PositionInfo, p2: Int) = delegate.onPositionDiscontinuity(p0, p1, p2)
    override fun onPlaybackParametersChanged(p0: PlaybackParameters) = delegate.onPlaybackParametersChanged(p0)
    override fun onSeekBackIncrementChanged(p0: Long) = delegate.onSeekBackIncrementChanged(p0)
    override fun onSeekForwardIncrementChanged(p0: Long) = delegate.onSeekForwardIncrementChanged(p0)
    override fun onMaxSeekToPreviousPositionChanged(p0: Long) = delegate.onMaxSeekToPreviousPositionChanged(p0)
    override fun onAudioSessionIdChanged(p0: Int) = delegate.onAudioSessionIdChanged(p0)
    override fun onAudioAttributesChanged(p0: AudioAttributes) = delegate.onAudioAttributesChanged(p0)
    override fun onVolumeChanged(p0: Float) = delegate.onVolumeChanged(p0)
    override fun onSkipSilenceEnabledChanged(p0: Boolean) = delegate.onSkipSilenceEnabledChanged(p0)
    override fun onDeviceInfoChanged(p0: DeviceInfo) = delegate.onDeviceInfoChanged(p0)
    override fun onDeviceVolumeChanged(p0: Int, p1: Boolean) = delegate.onDeviceVolumeChanged(p0, p1)
    override fun onVideoSizeChanged(p0: VideoSize) = delegate.onVideoSizeChanged(p0)
    override fun onSurfaceSizeChanged(p0: Int, p1: Int) = delegate.onSurfaceSizeChanged(p0, p1)
    override fun onRenderedFirstFrame() = delegate.onRenderedFirstFrame()
    override fun onCues(p0: List<Cue>) = delegate.onCues(p0)
    override fun onCues(p0: CueGroup) = delegate.onCues(p0)
    override fun onMetadata(p0: Metadata) = delegate.onMetadata(p0)

    override fun equals(other: Any?) = other is CommandsListener && other.delegate == delegate

    override fun hashCode() = delegate.hashCode()
}
