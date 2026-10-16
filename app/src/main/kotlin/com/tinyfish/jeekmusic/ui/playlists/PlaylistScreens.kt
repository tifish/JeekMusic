package com.tinyfish.jeekmusic.ui.playlists

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.toMutableStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.PlaylistEntity
import com.tinyfish.jeekmusic.data.db.PlaylistEntry
import com.tinyfish.jeekmusic.data.db.PlaylistWithCount
import com.tinyfish.jeekmusic.data.playlist.PlaylistRepository
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.ui.components.CenteredMessage
import com.tinyfish.jeekmusic.ui.components.LocateCurrentButton
import com.tinyfish.jeekmusic.ui.components.PlayButtons
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
import kotlinx.coroutines.launch
import sh.calvin.reorderable.ReorderableItem
import sh.calvin.reorderable.rememberReorderableLazyListState
import javax.inject.Inject

@HiltViewModel
class PlaylistListViewModel @Inject constructor(private val repository: PlaylistRepository) : ViewModel() {
    val playlists: StateFlow<List<PlaylistWithCount>?> = repository.playlists
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    fun create(name: String) = viewModelScope.launch { repository.create(name) }
}

/** 播放列表标签页（需求 PL-1）。 */
@Composable
fun PlaylistListScreen(onOpen: (Long) -> Unit, viewModel: PlaylistListViewModel = hiltViewModel()) {
    val playlists by viewModel.playlists.collectAsStateWithLifecycle()
    var creating by remember { mutableStateOf(false) }
    Box(Modifier.fillMaxSize()) {
        val list = playlists
        when {
            list == null -> Unit
            list.isEmpty() -> CenteredMessage(stringResource(R.string.playlists_empty))
            else -> LazyColumn(Modifier.fillMaxSize().testTag("playlist_list")) {
                items(list, key = { it.playlist.id }) { (playlist, count) ->
                    Row(
                        Modifier.fillMaxWidth().clickable { onOpen(playlist.id) }.padding(horizontal = 16.dp, vertical = 12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Icon(painterResource(R.drawable.ic_queue), contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                        Spacer(Modifier.width(16.dp))
                        Text(playlist.name, Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
                        Text(songCount(count), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
        ExtendedFloatingActionButton(
            onClick = { creating = true },
            icon = { Icon(painterResource(R.drawable.ic_add), contentDescription = null) },
            text = { Text(stringResource(R.string.playlist_new)) },
            modifier = Modifier.align(Alignment.BottomEnd).padding(16.dp).testTag("new_playlist"),
        )
    }
    if (creating) {
        NameDialog(title = stringResource(R.string.playlist_new), initial = "", confirm = stringResource(R.string.create), onDismiss = { creating = false }) {
            viewModel.create(it)
            creating = false
        }
    }
}

@HiltViewModel(assistedFactory = PlaylistDetailViewModel.Factory::class)
class PlaylistDetailViewModel @AssistedInject constructor(
    @Assisted private val playlistId: Long,
    private val repository: PlaylistRepository,
    private val player: PlayerConnection,
) : ViewModel() {

    @AssistedFactory
    interface Factory {
        fun create(playlistId: Long): PlaylistDetailViewModel
    }

    val playlist: StateFlow<PlaylistEntity?> = repository.playlist(playlistId)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    val entries: StateFlow<List<PlaylistEntry>?> = repository.entries(playlistId)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    val currentTrackId: StateFlow<Long?> = player.state.map { it.nowPlaying?.trackId }.distinctUntilChanged()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    /** 只播放可用的条目；点击的条目在可用列表中的位置作为起点。 */
    fun play(entry: PlaylistEntry? = null, shuffle: Boolean = false) {
        val list = entries.value.orEmpty()
        val available = list.mapNotNull { it.track }
        val start = entry?.track?.let { t -> available.indexOfFirst { it.id == t.id } }?.coerceAtLeast(0) ?: 0
        player.play(available, start, shuffle)
    }

    fun remove(entry: PlaylistEntry) = viewModelScope.launch { repository.removeItem(playlistId, entry.item.id) }

    fun reorder(itemIds: List<Long>) = viewModelScope.launch { repository.reorder(playlistId, itemIds) }

    fun rename(name: String) = viewModelScope.launch { repository.rename(playlistId, name) }

    fun delete() = viewModelScope.launch { repository.delete(playlistId) }
}

/** 播放列表详情（需求 PL-2、PL-3）：拖动排序、移除；文件不可用的条目显示为"不可用"。 */
@Composable
fun PlaylistDetailScreen(
    route: TabRoute.PlaylistDetail,
    onBack: () -> Unit,
    viewModel: PlaylistDetailViewModel = hiltViewModel<PlaylistDetailViewModel, PlaylistDetailViewModel.Factory>(
        key = route.toString(),
        creationCallback = { it.create(route.playlistId) },
    ),
) {
    val playlist by viewModel.playlist.collectAsStateWithLifecycle()
    val entries by viewModel.entries.collectAsStateWithLifecycle()
    val currentTrackId by viewModel.currentTrackId.collectAsStateWithLifecycle()
    var menuOpen by remember { mutableStateOf(false) }
    var renaming by remember { mutableStateOf(false) }
    var deleting by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxSize()) {
        SubScreenHeader(
            title = playlist?.name.orEmpty(),
            subtitle = entries?.let { songCount(it.size) },
            onBack = onBack,
            actions = {
                IconButton(onClick = { menuOpen = true }) {
                    Icon(painterResource(R.drawable.ic_more_vert), contentDescription = stringResource(R.string.action_more))
                }
                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    DropdownMenuItem(text = { Text(stringResource(R.string.playlist_rename)) }, onClick = { menuOpen = false; renaming = true })
                    DropdownMenuItem(text = { Text(stringResource(R.string.playlist_delete)) }, onClick = { menuOpen = false; deleting = true })
                }
            },
        )
        val list = entries ?: return@Column
        PlayButtons(onPlayAll = { viewModel.play() }, onShuffle = { viewModel.play(shuffle = true) })

        val items = remember(list) { list.toMutableStateList() }
        val listState = rememberLazyListState()
        val reorderState = rememberReorderableLazyListState(listState) { from, to ->
            items.add(to.index, items.removeAt(from.index))
        }
        Box(Modifier.fillMaxSize()) {
            LazyColumn(Modifier.fillMaxSize().testTag("playlist_tracks"), state = listState) {
                items(items, key = { it.item.id }) { entry ->
                    ReorderableItem(reorderState, key = entry.item.id) {
                        val track = entry.track
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(
                                painterResource(R.drawable.ic_drag_handle),
                                contentDescription = stringResource(R.string.action_reorder),
                                modifier = Modifier.padding(start = 8.dp).size(40.dp).padding(8.dp).draggableHandle(
                                    onDragStopped = { viewModel.reorder(items.map { it.item.id }) },
                                ),
                                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            Box(Modifier.weight(1f)) {
                                if (track != null) {
                                    TrackRow(track = track, isCurrent = track.id == currentTrackId, onClick = { viewModel.play(entry) })
                                } else {
                                    // 文件暂时不可用：保留条目，灰色显示（需求 PL-3）
                                    Column(Modifier.fillMaxWidth().alpha(0.5f).padding(horizontal = 16.dp, vertical = 12.dp)) {
                                        Text(entry.item.trackKey.substringAfterLast('/'), maxLines = 1, overflow = TextOverflow.Ellipsis)
                                        Text(stringResource(R.string.playlist_unavailable), style = MaterialTheme.typography.bodySmall)
                                    }
                                }
                            }
                            IconButton(onClick = { viewModel.remove(entry) }) {
                                Icon(painterResource(R.drawable.ic_close), contentDescription = stringResource(R.string.action_remove))
                            }
                        }
                    }
                }
            }
            LocateCurrentButton(listState, items.indexOfFirst { it.track?.id == currentTrackId }, Modifier.align(Alignment.BottomEnd))
        }
    }

    if (renaming) {
        NameDialog(stringResource(R.string.playlist_rename), playlist?.name.orEmpty(), stringResource(R.string.save), { renaming = false }) {
            viewModel.rename(it)
            renaming = false
        }
    }
    if (deleting) {
        AlertDialog(
            onDismissRequest = { deleting = false },
            title = { Text(stringResource(R.string.playlist_delete)) },
            text = { Text(stringResource(R.string.playlist_delete_message, playlist?.name.orEmpty())) },
            confirmButton = { TextButton(onClick = { viewModel.delete(); deleting = false; onBack() }) { Text(stringResource(R.string.delete)) } },
            dismissButton = { TextButton(onClick = { deleting = false }) { Text(stringResource(R.string.cancel)) } },
        )
    }
}

@Composable
fun NameDialog(title: String, initial: String, confirm: String, onDismiss: () -> Unit, onConfirm: (String) -> Unit) {
    var name by remember { mutableStateOf(initial) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            OutlinedTextField(
                value = name,
                onValueChange = { name = it },
                label = { Text(stringResource(R.string.playlist_name)) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth().testTag("name_field"),
            )
        },
        confirmButton = { TextButton(enabled = name.isNotBlank(), onClick = { onConfirm(name) }) { Text(confirm) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}
