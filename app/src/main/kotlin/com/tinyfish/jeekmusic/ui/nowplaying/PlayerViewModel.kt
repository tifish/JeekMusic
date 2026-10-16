package com.tinyfish.jeekmusic.ui.nowplaying

import androidx.lifecycle.ViewModel
import com.tinyfish.jeekmusic.data.db.ArtistEntity
import com.tinyfish.jeekmusic.data.db.SearchDao
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.playback.PlayerUiState
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.Flow
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.lyrics.Lyrics
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.transformLatest
import kotlinx.coroutines.launch
import javax.inject.Inject

/** 播放页的歌词状态。 */
sealed interface LyricsState {
    data object Loading : LyricsState
    data object None : LyricsState
    data class Loaded(val lyrics: Lyrics, val trackKey: String, val offsetMs: Long) : LyricsState
}

@OptIn(ExperimentalCoroutinesApi::class)
@HiltViewModel
class PlayerViewModel @Inject constructor(
    private val player: PlayerConnection,
    private val searchDao: SearchDao,
    private val trackDao: TrackDao,
    private val lyricsRepository: LyricsRepository,
) : ViewModel() {

    private val offsetChanged = MutableStateFlow(0)

    /** 切歌时加载歌词（docs/design.md §8.1）；调整偏移后重新读取偏移。 */
    val lyrics: StateFlow<LyricsState> = combine(
        player.state.map { it.nowPlaying?.trackId }.distinctUntilChanged(),
        offsetChanged,
    ) { id, _ -> id }
        .transformLatest { id ->
            if (id == null || id < 0) {
                emit(LyricsState.None)
                return@transformLatest
            }
            val track = trackDao.byId(id)
            if (track == null) {
                emit(LyricsState.None)
                return@transformLatest
            }
            val lyrics = lyricsRepository.load(track)
            emit(if (lyrics == null) LyricsState.None else LyricsState.Loaded(lyrics, track.trackKey, lyricsRepository.offset(track.trackKey)))
        }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), LyricsState.Loading)

    fun setLyricsOffset(trackKey: String, offsetMs: Long) = viewModelScope.launch {
        lyricsRepository.setOffset(trackKey, offsetMs)
        offsetChanged.value++
    }

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
