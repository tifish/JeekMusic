package com.tinyfish.jeekmusic.ui.nowplaying

import androidx.lifecycle.ViewModel
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.playback.PlayerUiState
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.StateFlow
import javax.inject.Inject

@HiltViewModel
class PlayerViewModel @Inject constructor(
    private val player: PlayerConnection,
) : ViewModel() {

    val state: StateFlow<PlayerUiState> = player.state

    fun position(intervalMs: Long): Flow<Long> = player.position(intervalMs)

    fun togglePlayPause() = player.togglePlayPause()
    fun next() = player.next()
    fun previous() = player.previous()
    fun seekTo(positionMs: Long) = player.seekTo(positionMs)
    fun toggleShuffle() = player.toggleShuffle()
    fun cycleRepeat() = player.cycleRepeat()
    fun skipTo(index: Int) = player.skipToQueueItem(index)
    fun remove(index: Int) = player.removeQueueItem(index)
    fun move(from: Int, to: Int) = player.moveQueueItem(from, to)
}
