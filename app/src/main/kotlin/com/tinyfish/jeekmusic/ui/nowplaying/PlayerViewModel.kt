package com.tinyfish.jeekmusic.ui.nowplaying

import androidx.lifecycle.ViewModel
import com.tinyfish.jeekmusic.data.db.ArtistEntity
import com.tinyfish.jeekmusic.data.db.SearchDao
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.playback.PlayerUiState
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.StateFlow
import javax.inject.Inject

@HiltViewModel
class PlayerViewModel @Inject constructor(
    private val player: PlayerConnection,
    private val searchDao: SearchDao,
) : ViewModel() {

    suspend fun artistsOf(trackId: Long): List<ArtistEntity> = searchDao.artistsOfTrack(trackId)

    fun sleepAfterMinutes(minutes: Int) = player.sleepAfterMinutes(minutes)
    fun sleepAtEndOfTrack() = player.sleepAtEndOfTrack()
    fun cancelSleepTimer() = player.cancelSleepTimer()

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
