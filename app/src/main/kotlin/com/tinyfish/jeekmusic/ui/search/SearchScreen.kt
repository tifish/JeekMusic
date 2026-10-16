package com.tinyfish.jeekmusic.ui.search

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.ArtistEntity
import com.tinyfish.jeekmusic.data.db.SearchDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.ui.components.CenteredMessage
import com.tinyfish.jeekmusic.ui.components.SectionHeader
import com.tinyfish.jeekmusic.ui.components.TrackRow
import com.tinyfish.jeekmusic.ui.components.songCount
import com.tinyfish.jeekmusic.ui.navigation.LocalNavigator
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.mapLatest
import kotlinx.coroutines.flow.stateIn
import javax.inject.Inject

data class SearchResults(val query: String, val artists: List<ArtistEntity>, val tracks: List<TrackEntity>)

@OptIn(FlowPreview::class, kotlinx.coroutines.ExperimentalCoroutinesApi::class)
@HiltViewModel
class SearchViewModel @Inject constructor(
    private val dao: SearchDao,
    private val player: PlayerConnection,
) : ViewModel() {

    val query = MutableStateFlow("")

    /** 标题、艺术家、专辑、文件名模糊匹配（需求 S-1）。1 万首规模下 LIKE 足够快（docs/design.md §5.6）。 */
    val results: StateFlow<SearchResults?> = query
        .debounce(200)
        .map { it.trim() }
        .distinctUntilChanged()
        .mapLatest { q ->
            if (q.isEmpty()) {
                null
            } else {
                val pattern = "%" + MediaStoreSource.escapeLike(q) + "%"
                SearchResults(q, dao.artists(pattern, 20), dao.tracks(pattern, 200))
            }
        }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    val currentTrackId: StateFlow<Long?> = player.state.map { it.nowPlaying?.trackId }.distinctUntilChanged()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    fun play(tracks: List<TrackEntity>, index: Int) = player.play(tracks, index)
}

@Composable
fun SearchScreen(onBack: () -> Unit, viewModel: SearchViewModel = hiltViewModel()) {
    val query by viewModel.query.collectAsStateWithLifecycle()
    val results by viewModel.results.collectAsStateWithLifecycle()
    val currentTrackId by viewModel.currentTrackId.collectAsStateWithLifecycle()
    val navigator = LocalNavigator.current
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }

    Column(Modifier.fillMaxSize().systemBarsPadding()) {
        Row(Modifier.fillMaxWidth().padding(4.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) {
                Icon(painterResource(R.drawable.ic_arrow_back), contentDescription = stringResource(R.string.action_back))
            }
            TextField(
                value = query,
                onValueChange = { viewModel.query.value = it },
                placeholder = { Text(stringResource(R.string.search_hint)) },
                singleLine = true,
                colors = TextFieldDefaults.colors(
                    focusedContainerColor = Color.Transparent,
                    unfocusedContainerColor = Color.Transparent,
                ),
                trailingIcon = {
                    if (query.isNotEmpty()) {
                        IconButton(onClick = { viewModel.query.value = "" }) {
                            Icon(painterResource(R.drawable.ic_close), contentDescription = stringResource(R.string.cancel))
                        }
                    }
                },
                modifier = Modifier.weight(1f).focusRequester(focus).testTag("search_field"),
            )
        }
        val data = results ?: return@Column
        if (data.artists.isEmpty() && data.tracks.isEmpty()) {
            CenteredMessage(stringResource(R.string.search_no_results))
            return@Column
        }
        LazyColumn(Modifier.fillMaxSize().testTag("search_results")) {
            if (data.artists.isNotEmpty()) {
                item { SectionHeader(stringResource(R.string.search_artists)) }
                items(data.artists, key = { "a:" + it.nameKey }) { artist ->
                    Row(
                        Modifier.fillMaxWidth().clickable { navigator.openArtist(artist.nameKey) }.padding(horizontal = 16.dp, vertical = 12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Icon(painterResource(R.drawable.ic_person), contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                        Text(artist.name, Modifier.weight(1f).padding(start = 16.dp))
                        Text(songCount(artist.trackCount), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
            if (data.tracks.isNotEmpty()) {
                item { SectionHeader(stringResource(R.string.search_songs)) }
                itemsIndexed(data.tracks, key = { _, t -> "t:" + t.id }) { index, track ->
                    TrackRow(track, isCurrent = track.id == currentTrackId, onClick = { viewModel.play(data.tracks, index) })
                }
            }
        }
    }
}
