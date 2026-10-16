package com.tinyfish.jeekmusic.ui.moods

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.analysis.AnalysisScheduler
import com.tinyfish.jeekmusic.analysis.Mood
import com.tinyfish.jeekmusic.analysis.MoodRepository
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.ui.components.CenteredMessage
import com.tinyfish.jeekmusic.ui.components.LocateCurrentButton
import com.tinyfish.jeekmusic.ui.components.PlayButtons
import com.tinyfish.jeekmusic.ui.components.TrackRow
import com.tinyfish.jeekmusic.ui.components.songCount
import com.tinyfish.jeekmusic.ui.navigation.TabRoute
import dagger.assisted.Assisted
import dagger.assisted.AssistedFactory
import dagger.assisted.AssistedInject
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

@HiltViewModel(assistedFactory = MoodViewModel.Factory::class)
class MoodViewModel @AssistedInject constructor(
    @Assisted mood: String,
    moods: MoodRepository,
    private val scheduler: AnalysisScheduler,
    private val player: PlayerConnection,
) : ViewModel() {

    @AssistedFactory
    interface Factory {
        fun create(mood: String): MoodViewModel
    }

    val tracks: StateFlow<List<TrackEntity>?> = moods.tracks(Mood.valueOf(mood))
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    val pending: StateFlow<Int> = moods.pendingCount()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), 0)

    val currentTrackId: StateFlow<Long?> = player.state.map { it.nowPlaying?.trackId }.distinctUntilChanged()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    /** 先播放点击的歌，其余随机打乱；不改变全局的随机开关（需求 M-9）。 */
    fun play(index: Int) {
        val list = tracks.value ?: return
        val first = list.getOrNull(index) ?: return
        player.play(listOf(first) + (list - first).shuffled(), 0)
    }

    fun playAll(shuffle: Boolean) {
        tracks.value?.let { player.play(if (shuffle) it.shuffled() else it) }
    }

    fun analyzeNow() = viewModelScope.launch { scheduler.runNow() }
}

/** 智能标签页（需求 M-6、M-9）。 */
@Composable
fun MoodScreen(
    route: TabRoute.MoodTracks,
    viewModel: MoodViewModel = hiltViewModel<MoodViewModel, MoodViewModel.Factory>(
        key = route.toString(),
        creationCallback = { it.create(route.mood) },
    ),
) {
    val tracks by viewModel.tracks.collectAsStateWithLifecycle()
    val pending by viewModel.pending.collectAsStateWithLifecycle()
    val currentTrackId by viewModel.currentTrackId.collectAsStateWithLifecycle()
    val listState = rememberLazyListState()

    Column(Modifier.fillMaxSize()) {
        if (pending > 0) {
            Row(
                Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    pluralStringResource(R.plurals.mood_pending, pending, pending),
                    Modifier.weight(1f),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                TextButton(onClick = viewModel::analyzeNow, modifier = Modifier.testTag("analyze_now")) {
                    Text(stringResource(R.string.mood_analyze_now))
                }
            }
        }
        val list = tracks ?: return@Column
        if (list.isEmpty()) {
            CenteredMessage(stringResource(R.string.mood_empty))
            return@Column
        }
        Text(songCount(list.size), Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        PlayButtons(onPlayAll = { viewModel.playAll(false) }, onShuffle = { viewModel.playAll(true) })
        Box(Modifier.fillMaxSize()) {
            LazyColumn(Modifier.fillMaxSize().testTag("mood_tracks"), state = listState) {
                itemsIndexed(list, key = { _, t -> t.id }) { index, track ->
                    TrackRow(track, isCurrent = track.id == currentTrackId, onClick = { viewModel.play(index) })
                }
            }
            LocateCurrentButton(listState, list.indexOfFirst { it.id == currentTrackId }, Modifier.align(Alignment.BottomEnd))
        }
    }
}
