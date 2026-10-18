package com.tinyfish.jeekmusic.ui.albums

import com.tinyfish.jeekmusic.ui.components.rememberTrackSelection
import com.tinyfish.jeekmusic.ui.components.SelectionBar
import com.tinyfish.jeekmusic.ui.components.ListMoreButton
import com.tinyfish.jeekmusic.ui.components.rememberListDownload
import com.tinyfish.jeekmusic.ui.components.verticalScrollbar
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.library.LibraryRepository
import com.tinyfish.jeekmusic.playback.MediaItemMapper
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.ui.components.Artwork
import com.tinyfish.jeekmusic.ui.components.LocateCurrentButton
import com.tinyfish.jeekmusic.ui.components.SectionHeader
import com.tinyfish.jeekmusic.ui.components.SubScreenHeader
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
import javax.inject.Inject

/** 专辑页头信息（docs/design.md §5.4）。 */
data class AlbumInfo(val name: String, val artist: String, val year: Int?, val coverTrackId: Long, val multiDisc: Boolean)

@HiltViewModel(assistedFactory = AlbumViewModel.Factory::class)
class AlbumViewModel @AssistedInject constructor(
    @Assisted albumKey: String,
    library: LibraryRepository,
    private val player: PlayerConnection,
) : ViewModel() {

    @AssistedFactory
    interface Factory {
        fun create(albumKey: String): AlbumViewModel
    }

    val tracks: StateFlow<List<TrackEntity>?> = library.albumTracks(albumKey)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    val info: StateFlow<AlbumInfo?> = tracks.map { list ->
        val first = list?.firstOrNull() ?: return@map null
        AlbumInfo(
            name = first.album.orEmpty(),
            artist = first.albumArtist ?: list.flatMap { it.artistDisplay.split(" / ") }.filter { it.isNotEmpty() }.distinct().joinToString(" / "),
            year = list.mapNotNull { it.year }.minOrNull(),
            coverTrackId = first.id,
            multiDisc = list.mapNotNull { it.discNo }.distinct().size > 1,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    val currentTrackId: StateFlow<Long?> = player.state.map { it.nowPlaying?.trackId }.distinctUntilChanged()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    fun play(index: Int) {
        tracks.value?.let { player.play(it, index) }
    }
}

/** 专辑页（需求 AL-1 ~ AL-5）：封面、专辑信息、按碟号和音轨号排序的歌曲。 */
@Composable
fun AlbumScreen(
    route: TabRoute.Album,
    onBack: () -> Unit,
    viewModel: AlbumViewModel = hiltViewModel<AlbumViewModel, AlbumViewModel.Factory>(
        key = route.toString(),
        creationCallback = { it.create(route.albumKey) },
    ),
) {
    val tracks by viewModel.tracks.collectAsStateWithLifecycle()
    val info by viewModel.info.collectAsStateWithLifecycle()
    val currentTrackId by viewModel.currentTrackId.collectAsStateWithLifecycle()
    val listState = rememberLazyListState()
    val selection = rememberTrackSelection()
    val download = rememberListDownload(tracks = { viewModel.tracks.value.orEmpty() })

    Column(Modifier.fillMaxSize()) {
        if (selection.active) SelectionBar(selection, tracks = { viewModel.tracks.value.orEmpty() }) else SubScreenHeader(
            title = info?.name.orEmpty(),
            onBack = onBack,
            actions = { ListMoreButton(download, selection) },
        )
        val list = tracks ?: return@Column
        val album = info ?: return@Column
        // 列表中的条目：页头、按钮、（碟号标题）、歌曲；记下每首歌所在的位置，供定位按钮使用
        val rows = buildList<Any> {
            var disc: Int? = null
            list.forEachIndexed { i, track ->
                if (album.multiDisc && track.discNo != disc) {
                    disc = track.discNo
                    add("disc:${track.discNo ?: 0}")
                }
                add(IndexedValue(i, track))
            }
        }
        val currentRow = rows.indexOfFirst { it is IndexedValue<*> && (it.value as TrackEntity).id == currentTrackId }
            .let { if (it >= 0) it + 2 else -1 }

        Box(Modifier.fillMaxSize()) {
            LazyColumn(Modifier.fillMaxSize().verticalScrollbar(listState, draggable = true).testTag("album_tracks"), state = listState) {
                item(key = "header") {
                    Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Artwork(MediaItemMapper.artworkUri(album.coverTrackId, large = true), 112.dp, cornerRadius = 12.dp)
                        Spacer(Modifier.width(16.dp))
                        Column {
                            Text(album.name, style = MaterialTheme.typography.titleLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
                            Text(
                                album.artist.ifEmpty { stringResource(R.string.unknown_artist) },
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.primary,
                                maxLines = 2,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                listOfNotNull(album.year?.toString(), songCount(list.size)).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
                items(rows, key = { if (it is IndexedValue<*>) (it.value as TrackEntity).id else it }) { row ->
                    if (row is String) {
                        SectionHeader(stringResource(R.string.disc_number, row.removePrefix("disc:").toInt()))
                    } else {
                        @Suppress("UNCHECKED_CAST")
                        val (index, track) = row as IndexedValue<TrackEntity>
                        TrackRow(
                            track = track,
                            isCurrent = track.id == currentTrackId,
                            onClick = { viewModel.play(index) },
                            selection = selection,
                            subtitle = track.artistDisplay.ifEmpty { stringResource(R.string.unknown_artist) },
                            leading = {
                                Text(
                                    track.trackNo?.toString() ?: "–",
                                    Modifier.width(32.dp),
                                    style = MaterialTheme.typography.labelLarge,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            },
                        )
                    }
                }
            }
            LocateCurrentButton(listState, currentRow, Modifier.align(Alignment.BottomEnd))
        }
    }
}
