package com.tinyfish.jeekmusic.dev

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.playback.PlayerUiState
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import javax.inject.Inject

data class DevUiState(
    val loaded: Boolean = false,
    val tracks: List<ProbeTrack> = emptyList(),
    val probing: Boolean = false,
    val probeProgress: Int = 0,
    val report: TagLibReport? = null,
)

@HiltViewModel
class DevViewModel @Inject constructor(
    private val mediaStore: MediaStoreProbe,
    private val tagLib: TagLibProbe,
    private val player: PlayerConnection,
) : ViewModel() {

    private val _state = MutableStateFlow(DevUiState())
    val state: StateFlow<DevUiState> = _state.asStateFlow()
    val playerState: StateFlow<PlayerUiState> = player.state

    fun load() {
        viewModelScope.launch {
            val tracks = mediaStore.queryAll()
            _state.update { it.copy(loaded = true, tracks = tracks) }
        }
    }

    fun markLoadedWithoutPermission() {
        _state.update { it.copy(loaded = true) }
    }

    fun runTagLibProbe() {
        val tracks = _state.value.tracks
        if (tracks.isEmpty() || _state.value.probing) return
        _state.update { it.copy(probing = true, probeProgress = 0, report = null) }
        viewModelScope.launch {
            val report = tagLib.run(tracks) { done -> _state.update { it.copy(probeProgress = done) } }
            _state.update { it.copy(probing = false, report = report) }
        }
    }

    fun play(index: Int) {
        val items = _state.value.tracks.map { track ->
            MediaItem.Builder()
                .setMediaId("ms:${track.id}")
                .setRequestMetadata(MediaItem.RequestMetadata.Builder().setMediaUri(track.uri).build())
                .setMediaMetadata(
                    MediaMetadata.Builder()
                        .setTitle(track.title)
                        .setArtist(track.artist)
                        .build(),
                )
                .build()
        }
        player.play(items, index)
    }

    fun togglePlayPause() = player.togglePlayPause()
}
