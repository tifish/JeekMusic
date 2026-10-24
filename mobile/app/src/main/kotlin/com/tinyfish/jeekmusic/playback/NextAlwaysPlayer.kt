package com.tinyfish.jeekmusic.playback

import androidx.annotation.OptIn
import androidx.media3.common.ForwardingPlayer
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.Timeline
import androidx.media3.common.util.UnstableApi

/**
 * 让"下一首"始终可用（需求 P-11）：系统默认在队列最后一首（且没开循环）时隐藏通知、锁屏上的"下一首"按钮，
 * 只有一首歌时也没有。这里在队列不为空时总是声明可以跳到下一首；已经是最后一首时跳回第一首。
 *
 * 显式上一首/下一首成功导航后请求播放（需求 P-1），保留上一首回到本曲开头的阈值。
 * 只包装交给 MediaSession 的播放器：通知、锁屏、蓝牙耳机和 App 界面都经过会话，行为一致。
 */
@OptIn(UnstableApi::class)
class NextAlwaysPlayer(
    private val delegate: Player,
    /** 经 MediaController 请求播放，保留 MediaSession 的平台播放许可检查；连接完成后须再检查请求是否有效。 */
    private val requestPlay: (isStillValid: () -> Boolean) -> Unit,
) : ForwardingPlayer(delegate) {

    private var navigationVersion = 0L
    private val navigationListener = object : Player.Listener {
        override fun onTimelineChanged(timeline: Timeline, reason: Int) {
            if (reason == TIMELINE_CHANGE_REASON_PLAYLIST_CHANGED) navigationVersion++
        }
        override fun onPositionDiscontinuity(oldPosition: Player.PositionInfo, newPosition: Player.PositionInfo, reason: Int) { navigationVersion++ }
        override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) { navigationVersion++ }
        override fun onPlaybackSuppressionReasonChanged(reason: Int) { navigationVersion++ }
        override fun onPlayerError(error: PlaybackException) { navigationVersion++ }
    }

    init {
        delegate.addListener(navigationListener)
    }

    override fun getAvailableCommands(): Player.Commands = withNext(super.getAvailableCommands())

    override fun isCommandAvailable(command: Int): Boolean =
        (command in NEXT_COMMANDS && mediaItemCount > 0) || super.isCommandAvailable(command)

    override fun seekToNext() {
        next(COMMAND_SEEK_TO_NEXT) { super.seekToNext() }
    }

    override fun seekToNextMediaItem() {
        next(COMMAND_SEEK_TO_NEXT_MEDIA_ITEM) { super.seekToNextMediaItem() }
    }

    override fun seekToPrevious() {
        val target = if (hasPreviousMediaItem() &&
            ((isCurrentMediaItemLive && !isCurrentMediaItemSeekable) || currentPosition <= maxSeekToPreviousPosition)
        ) previousMediaItemIndex else currentMediaItemIndex
        navigate(COMMAND_SEEK_TO_PREVIOUS, target) { super.seekToPrevious() }
    }

    override fun seekToPreviousMediaItem() {
        navigate(COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM, previousMediaItemIndex) { super.seekToPreviousMediaItem() }
    }

    override fun pause() {
        navigationVersion++
        super.pause()
    }

    override fun stop() {
        navigationVersion++
        super.stop()
    }

    override fun setPlayWhenReady(playWhenReady: Boolean) {
        navigationVersion++
        super.setPlayWhenReady(playWhenReady)
    }

    override fun release() {
        navigationVersion++
        delegate.removeListener(navigationListener)
        super.release()
    }

    /**
     * 会话直接用"可用命令变化"事件里的参数（不会再调 [getAvailableCommands]），
     * 而这个参数来自内部播放器、不含"下一首"：转发前补上，否则按钮会被会话隐藏。
     */
    override fun addListener(listener: Player.Listener) {
        super.addListener(CommandsListener(listener, ::withNext))
    }

    override fun removeListener(listener: Player.Listener) {
        // CommandsListener 按被包装的监听器判断相等，ForwardingPlayer 也是按相等移除
        super.removeListener(CommandsListener(listener, ::withNext))
    }

    private fun next(command: Int, seek: () -> Unit) {
        if (hasNextMediaItem()) {
            navigate(command, nextMediaItemIndex, seek)
        } else {
            navigate(COMMAND_SEEK_TO_MEDIA_ITEM, 0) { super.seekTo(0, 0) }
        }
    }

    /** Media3 的 seek 同步更新当前索引/位置；先确认到达目标，不能因忽略或失败的 seek 播放旧曲。 */
    private fun navigate(command: Int, targetIndex: Int, seek: () -> Unit) {
        navigationVersion++
        if (targetIndex !in 0 until mediaItemCount || isPlayingAd || !delegate.isCommandAvailable(command)) return
        val target = getMediaItemAt(targetIndex)
        val restarting = targetIndex == currentMediaItemIndex
        seek()
        if (currentMediaItemIndex != targetIndex || !isCurrentTarget(target) ||
            (restarting && currentPosition != 0L) || playerError != null
        ) return

        // 已经请求播放时继续由底层处理焦点/抑制状态；暂停、恢复的未准备队列和曲尾则走会话的正常播放入口。
        if (playWhenReady && playbackState != STATE_IDLE && playbackState != STATE_ENDED) return
        val version = navigationVersion
        requestPlay {
            version == navigationVersion && currentMediaItemIndex == targetIndex && isCurrentTarget(target) &&
                playerError == null && playbackSuppressionReason == PLAYBACK_SUPPRESSION_REASON_NONE &&
                delegate.isCommandAvailable(COMMAND_PLAY_PAUSE)
        }
    }

    // 换歌监听器可能同步刷新封面元数据；播放目标按歌曲/文件/CUE 区间识别，不能被封面刷新误判为导航失败。
    private fun isCurrentTarget(target: MediaItem): Boolean = currentMediaItem?.let {
        it.mediaId == target.mediaId && it.localConfiguration == target.localConfiguration &&
            it.clippingConfiguration == target.clippingConfiguration
    } == true

    private fun withNext(commands: Player.Commands): Player.Commands =
        if (mediaItemCount == 0) commands else commands.buildUpon().addAll(*NEXT_COMMANDS).build()

    private companion object {
        val NEXT_COMMANDS = intArrayOf(COMMAND_SEEK_TO_NEXT, COMMAND_SEEK_TO_NEXT_MEDIA_ITEM)
    }
}
