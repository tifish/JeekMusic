package com.tinyfish.jeekmusic.playback

import androidx.annotation.OptIn
import androidx.media3.common.ForwardingPlayer
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi

/**
 * 让"下一首"始终可用（需求 P-11）：系统默认在队列最后一首（且没开循环）时隐藏通知、锁屏上的"下一首"按钮，
 * 只有一首歌时也没有。这里在队列不为空时总是声明可以跳到下一首；已经是最后一首时跳回第一首。
 *
 * 只包装交给 MediaSession 的播放器：通知、锁屏、蓝牙耳机和 App 界面都经过会话，行为一致。
 */
@OptIn(UnstableApi::class)
class NextAlwaysPlayer(player: Player) : ForwardingPlayer(player) {

    override fun getAvailableCommands(): Player.Commands = withNext(super.getAvailableCommands())

    override fun isCommandAvailable(command: Int): Boolean =
        (command in NEXT_COMMANDS && mediaItemCount > 0) || super.isCommandAvailable(command)

    override fun seekToNext() {
        if (hasNextMediaItem() || mediaItemCount == 0) super.seekToNext() else seekToFirst()
    }

    override fun seekToNextMediaItem() {
        if (hasNextMediaItem() || mediaItemCount == 0) super.seekToNextMediaItem() else seekToFirst()
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

    private fun seekToFirst() {
        seekTo(0, 0)
        if (playbackState == STATE_ENDED || playbackState == STATE_IDLE) prepare()
    }

    private fun withNext(commands: Player.Commands): Player.Commands =
        if (mediaItemCount == 0) commands else commands.buildUpon().addAll(*NEXT_COMMANDS).build()

    private companion object {
        val NEXT_COMMANDS = intArrayOf(COMMAND_SEEK_TO_NEXT, COMMAND_SEEK_TO_NEXT_MEDIA_ITEM)
    }
}
