package com.tinyfish.jeekmusic.ui.folders

import com.tinyfish.jeekmusic.ui.components.ListDownloadButton
import com.tinyfish.jeekmusic.ui.components.verticalScrollbar
import com.tinyfish.jeekmusic.data.filter.TagFilterRepository
import com.tinyfish.jeekmusic.data.settings.TagFilter
import com.tinyfish.jeekmusic.ui.components.FilterEmptyMessage
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.layout.Box
import com.tinyfish.jeekmusic.ui.components.LocateCurrentButton
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.FolderWithCount
import com.tinyfish.jeekmusic.data.library.DirectoryContent
import com.tinyfish.jeekmusic.data.library.LibraryRepository
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.ui.components.CenteredMessage
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
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import androidx.compose.runtime.snapshotFlow
import com.tinyfish.jeekmusic.startup.StartupSnapshot
import javax.inject.Inject

@HiltViewModel
class FolderRootViewModel @Inject constructor(library: LibraryRepository) : ViewModel() {
    val folders: StateFlow<List<FolderWithCount>?> = library.folders
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)
}

/** 根级：并列列出所有音乐文件夹（需求 F-1）；没有文件夹时显示引导（需求 L-4）。 */
@Composable
fun FolderRootScreen(
    onOpen: (TabRoute.Folder) -> Unit,
    onLoaded: () -> Unit,
    viewModel: FolderRootViewModel = hiltViewModel(),
) {
    val folders by viewModel.folders.collectAsStateWithLifecycle()
    val list = folders ?: return
    LaunchedEffect(Unit) { onLoaded() }
    if (list.isEmpty()) {
        EmptyLibrary()
        return
    }
    val listState = rememberLazyListState()
    LazyColumn(Modifier.fillMaxSize().verticalScrollbar(listState, draggable = true).testTag("folder_list"), state = listState) {
        items(list, key = { it.folder.id }) { (folder, count) ->
            FolderRow(
                name = folder.displayName,
                detail = songCount(count),
                onClick = { onOpen(TabRoute.Folder(folder.id, folder.relativePath, folder.displayName)) },
            )
        }
    }
}

@Composable
private fun EmptyLibrary() {
    val addFolder = rememberAddFolder()
    Column(
        Modifier.fillMaxSize().padding(32.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(painterResource(R.drawable.ic_folder), contentDescription = null, modifier = Modifier.size(64.dp), tint = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.size(16.dp))
        Text(stringResource(R.string.empty_library_title), style = MaterialTheme.typography.titleLarge)
        Spacer(Modifier.size(8.dp))
        Text(
            stringResource(R.string.empty_library_message),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
        Spacer(Modifier.size(24.dp))
        Button(onClick = addFolder, modifier = Modifier.testTag("add_folder")) {
            Text(stringResource(R.string.action_add_folder))
        }
    }
}

@Composable
private fun FolderRow(name: String, detail: String, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(painterResource(R.drawable.ic_folder), contentDescription = null, tint = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.width(16.dp))
        Text(name, Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
        Text(detail, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@HiltViewModel(assistedFactory = FolderViewModel.Factory::class)
class FolderViewModel @AssistedInject constructor(
    @Assisted private val route: TabRoute.Folder,
    private val library: LibraryRepository,
    private val player: PlayerConnection,
    private val snapshot: StartupSnapshot,
    tagFilters: TagFilterRepository,
) : ViewModel() {

    val filter: StateFlow<TagFilter> = tagFilters.filter.stateIn(viewModelScope, SharingStarted.Eagerly, TagFilter())

    /** 上次的滚动位置，重启 App 后也能恢复（需求 F-8）。 */
    val initialScroll: Pair<Int, Int> = snapshot.folderScroll(route)

    fun saveScroll(index: Int, offset: Int) = viewModelScope.launch { snapshot.saveFolderScroll(route, index, offset) }

    @AssistedFactory
    interface Factory {
        fun create(route: TabRoute.Folder): FolderViewModel
    }

    val content: StateFlow<DirectoryContent?> = library.directory(route.folderId, route.relativePath)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    val currentTrackId: StateFlow<Long?> = player.state.map { it.nowPlaying?.trackId }.distinctUntilChanged()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    /** 点击歌曲：当前文件夹的歌曲（不含子文件夹）作为队列（需求 F-5）。 */
    fun play(index: Int) {
        content.value?.tracks?.let { player.play(it, index) }
    }

    /** 这个文件夹及子文件夹里的歌（按当前筛选），批量下载封面、歌词用。 */
    suspend fun tracksRecursive() = library.tracksUnder(route.folderId, route.relativePath)

    fun playAllRecursive(shuffle: Boolean) {
        viewModelScope.launch {
            player.play(library.tracksUnder(route.folderId, route.relativePath), shuffle = shuffle)
        }
    }
}

/** 文件夹内：先列子文件夹，再列歌曲（需求 F-2）。 */
@Composable
fun FolderScreen(
    route: TabRoute.Folder,
    onOpen: (TabRoute.Folder) -> Unit,
    onBack: () -> Unit,
    /** 内容第一次加载出来；启动时恢复到子文件夹的话，首屏就是这里（docs/design.md §9.3）。 */
    onLoaded: () -> Unit = {},
    viewModel: FolderViewModel = hiltViewModel<FolderViewModel, FolderViewModel.Factory>(
        key = route.toString(),
        creationCallback = { it.create(route) },
    ),
) {
    val content by viewModel.content.collectAsStateWithLifecycle()
    val currentTrackId by viewModel.currentTrackId.collectAsStateWithLifecycle()
    var menuOpen by remember { mutableStateOf(false) }
    val loaded = content != null
    LaunchedEffect(loaded) { if (loaded) onLoaded() }

    Column(Modifier.fillMaxSize()) {
        // 歌曲数包括子文件夹里的，和上一级列表里显示的一致；筛选时只算符合筛选的（需求 F-9）
        val total = content?.let { data -> data.tracks.size + data.folders.sumOf { it.trackCount } }
        SubScreenHeader(
            title = route.title,
            subtitle = listOfNotNull(route.relativePath, total?.let { songCount(it) }).joinToString(" · "),
            onBack = onBack,
            actions = {
                ListDownloadButton(tracks = viewModel::tracksRecursive)
                IconButton(onClick = { menuOpen = true }) {
                    Icon(painterResource(R.drawable.ic_more_vert), contentDescription = stringResource(R.string.action_more))
                }
                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.action_play_all_recursive)) },
                        onClick = { menuOpen = false; viewModel.playAllRecursive(shuffle = false) },
                    )
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.action_shuffle)) },
                        onClick = { menuOpen = false; viewModel.playAllRecursive(shuffle = true) },
                    )
                }
            },
        )
        val data = content ?: return@Column
        if (data.folders.isEmpty() && data.tracks.isEmpty()) {
            val filter by viewModel.filter.collectAsStateWithLifecycle()
            if (filter.isActive) FilterEmptyMessage(filter) else CenteredMessage(stringResource(R.string.folder_empty))
            return@Column
        }
        val listState = rememberLazyListState(viewModel.initialScroll.first, viewModel.initialScroll.second)
        LaunchedEffect(listState) {
            snapshotFlow { listState.firstVisibleItemIndex to listState.firstVisibleItemScrollOffset }
                .distinctUntilChanged()
                .collectLatest { (index, offset) ->
                    delay(500) // 停止滚动后再保存
                    viewModel.saveScroll(index, offset)
                }
        }
        val currentRow = data.tracks.indexOfFirst { it.id == currentTrackId }.let { if (it >= 0) it + data.folders.size else -1 }
        Box(Modifier.fillMaxSize()) {
        LazyColumn(Modifier.fillMaxSize().verticalScrollbar(listState, draggable = true).testTag("folder_content"), state = listState) {
            items(data.folders, key = { "dir:" + it.relativePath }, contentType = { "folder" }) { node ->
                FolderRow(
                    name = node.name,
                    detail = songCount(node.trackCount),
                    onClick = { onOpen(TabRoute.Folder(route.folderId, node.relativePath, node.name)) },
                )
            }
            itemsIndexed(data.tracks, key = { _, t -> t.id }, contentType = { _, _ -> "track" }) { index, track ->
                TrackRow(track = track, isCurrent = track.id == currentTrackId, onClick = { viewModel.play(index) })
            }
        }
        LocateCurrentButton(listState, currentRow, Modifier.align(Alignment.BottomEnd))
        }
    }
}
