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
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import com.tinyfish.jeekmusic.data.lyrics.OnlineLyrics
import com.tinyfish.jeekmusic.data.lyrics.SaveResult
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.data.settings.SleepPrefs
import com.tinyfish.jeekmusic.ui.navigation.MessageBus
import com.tinyfish.jeekmusic.ui.navigation.UiMessage
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
    private val settings: SettingsRepository,
    private val messages: MessageBus,
) : ViewModel() {

    private val offsetChanged = MutableStateFlow(0)

    val onlineLyricsEnabled: StateFlow<Boolean> = settings.settings.map { it.onlineLyrics }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), false)

    suspend fun currentTrack(): TrackEntity? = player.state.value.nowPlaying?.trackId?.let { trackDao.byId(it) }

    suspend fun searchLyrics(title: String, artist: String?): List<OnlineLyrics>? =
        runCatching { lyricsRepository.search(title, artist) }.getOrNull()

    /** 保存选中的在线歌词并刷新播放页（需求 LY-10）。 */
    fun saveLyrics(track: TrackEntity, lyrics: OnlineLyrics) = viewModelScope.launch {
        val text = lyrics.text ?: return@launch
        val result = lyricsRepository.save(track, text)
        messages.post(UiMessage(if (result == SaveResult.MUSIC_FOLDER) R.string.lyrics_saved_folder else R.string.lyrics_saved_cache))
        offsetChanged.value++
    }

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
            val lyrics = lyricsRepository.load(track) ?: run {
                emit(LyricsState.None)
                lyricsRepository.autoFetch(track)
            }
            emit(if (lyrics == null) LyricsState.None else LyricsState.Loaded(lyrics, track.trackKey, lyricsRepository.offset(track.trackKey)))
        }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), LyricsState.Loading)

    fun setLyricsOffset(trackKey: String, offsetMs: Long) = viewModelScope.launch {
        lyricsRepository.setOffset(trackKey, offsetMs)
        offsetChanged.value++
    }

    suspend fun artistsOf(trackId: Long): List<ArtistEntity> = searchDao.artistsOfTrack(trackId)

    val sleepPrefs: StateFlow<SleepPrefs> = settings.settings.map { it.sleep }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), SleepPrefs())

    /** 按时间定时；同时记住"播完整首"的选择（和自定义的分钟数）供下次使用。 */
    fun sleepAfterMinutes(minutes: Int, finishSong: Boolean, custom: Boolean) {
        player.sleepAfterMinutes(minutes, finishSong)
        viewModelScope.launch {
            val prefs = sleepPrefs.value.copy(finishSong = finishSong)
            settings.setSleepPrefs(if (custom) prefs.copy(customMinutes = minutes) else prefs)
        }
    }

    fun sleepAfterSongs(count: Int) {
        player.sleepAfterSongs(count)
        viewModelScope.launch { settings.setSleepPrefs(sleepPrefs.value.copy(songCount = count)) }
    }
    fun cancelSleepTimer() = player.cancelSleepTimer()

    val state: StateFlow<PlayerUiState> = player.state

    fun position(intervalMs: Long): Flow<Long> = player.position(intervalMs)

    fun togglePlayPause() = player.togglePlayPause()
    fun next() = player.next()
    fun previous() = player.previous()
    fun seekTo(positionMs: Long) = player.seekTo(positionMs)
    fun toggleShuffle() = player.toggleShuffle()

    /** 标题栏上的随机 / 顺序切换（需求 P-5）：切换后提示现在是哪种模式。 */
    fun toggleShuffleAndAnnounce() {
        val shuffle = !state.value.shuffle
        player.toggleShuffle()
        messages.post(UiMessage(if (shuffle) R.string.mode_shuffle_on else R.string.mode_sequential_on))
    }
    fun cycleRepeat() = player.cycleRepeat()
    fun skipTo(index: Int) = player.skipToQueueItem(index)
    fun remove(index: Int) = player.removeQueueItem(index)
    fun move(from: Int, to: Int) = player.moveQueueItem(from, to)
}
