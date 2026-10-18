package com.tinyfish.jeekmusic.ui.components

import android.app.Activity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.FolderDao
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.library.FileOperations
import com.tinyfish.jeekmusic.data.library.FileOperations.RenameResult
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.ui.navigation.MessageBus
import com.tinyfish.jeekmusic.ui.navigation.UiMessage
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * 歌曲列表的多选状态（需求 F-10）。从歌曲"更多"菜单的"多选"进入；多选时点歌曲是选中 / 取消，选中的都取消时退出。
 * 每个列表页各有一个，通过 [LocalTrackSelection] 交给 [TrackRow]。
 */
@Stable
class TrackSelection {
    private val ids = mutableStateMapOf<Long, Unit>()

    var active by mutableStateOf(false)
        private set

    val count: Int get() = ids.size

    fun isSelected(trackId: Long) = trackId in ids

    fun start(track: TrackEntity) {
        ids[track.id] = Unit
        active = true
    }

    fun toggle(track: TrackEntity) {
        if (ids.remove(track.id) == null) ids[track.id] = Unit
        if (ids.isEmpty()) active = false
    }

    fun selectAll(tracks: List<TrackEntity>) = tracks.forEach { ids[it.id] = Unit }

    fun clear() {
        ids.clear()
        active = false
    }

    fun selected(tracks: List<TrackEntity>): List<TrackEntity> = tracks.filter { it.id in ids }.distinctBy { it.id }
}

val LocalTrackSelection = compositionLocalOf<TrackSelection?> { null }

@Composable
fun rememberTrackSelection(): TrackSelection = remember { TrackSelection() }

@HiltViewModel
class FileOpsViewModel @Inject constructor(
    private val ops: FileOperations,
    private val folderDao: FolderDao,
    private val player: PlayerConnection,
    private val messages: MessageBus,
) : ViewModel() {
    suspend fun trashRequest(tracks: List<TrackEntity>) = ops.trashRequest(tracks)

    fun onTrashed(tracks: List<TrackEntity>) {
        player.removeTracks(tracks.map { it.id }.toSet())
        ops.afterTrashed()
        messages.post(UiMessage(R.string.files_trashed, tracks.size.toString()))
    }

    suspend fun rename(track: TrackEntity, name: String): RenameResult = ops.rename(track, name).also {
        if (it == RenameResult.OK) messages.post(UiMessage(R.string.file_renamed))
    }

    fun move(tracks: List<TrackEntity>, folder: LibraryFolderEntity, relativePath: String) = viewModelScope.launch {
        val result = ops.move(tracks, folder, relativePath)
        messages.post(
            when {
                result.exists + result.failed == 0 -> UiMessage(R.string.files_moved, result.moved.toString())
                else -> UiMessage(R.string.files_moved_partly, "${result.moved} / ${result.moved + result.exists + result.failed}")
            },
        )
    }

    suspend fun folders(): List<LibraryFolderEntity> = folderDao.all()

    suspend fun subdirectories(folder: LibraryFolderEntity, relativePath: String) = ops.subdirectories(folder, relativePath)

    suspend fun createDirectory(folder: LibraryFolderEntity, parent: String, name: String) = ops.createDirectory(folder, parent, name)

    fun cueSkipped() = messages.post(UiMessage(R.string.files_cue_skipped))
}

/**
 * 多选时代替列表标题栏的操作栏（需求 F-10）：已选几首、改名（只选了一首时）、移动、删除（移到系统回收站），
 * "更多"里有全选，播放列表里还有"从列表移除"（[onRemoveFromPlaylist]）。返回键退出多选。
 */
@Composable
fun SelectionBar(
    selection: TrackSelection,
    tracks: () -> List<TrackEntity>,
    onRemoveFromPlaylist: ((List<TrackEntity>) -> Unit)? = null,
    viewModel: FileOpsViewModel = hiltViewModel(),
) {
    val scope = rememberCoroutineScope()
    var menuOpen by remember { mutableStateOf(false) }
    var renaming by remember { mutableStateOf<TrackEntity?>(null) }
    var moving by remember { mutableStateOf<List<TrackEntity>?>(null) }
    var trashing by remember { mutableStateOf<List<TrackEntity>>(emptyList()) }
    // 有"所有文件访问权限"时系统不再弹确认，所以总是先由 App 确认一次
    var confirmTrash by remember { mutableStateOf<List<TrackEntity>?>(null) }
    val trashLauncher = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK) {
            viewModel.onTrashed(trashing)
            selection.clear()
        }
    }

    BackHandler(enabled = selection.active) { selection.clear() }

    /** CUE 整轨里的歌共用一个文件，不能单独做文件操作。 */
    fun files(): List<TrackEntity> {
        val chosen = selection.selected(tracks())
        return chosen.filter { it.cueTrackNo == null }.also { if (it.size < chosen.size) viewModel.cueSkipped() }
    }

    Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 4.dp).testTag("selection_bar"), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = selection::clear) {
            Icon(painterResource(R.drawable.ic_close), contentDescription = stringResource(R.string.selection_exit))
        }
        Text(
            pluralStringResource(R.plurals.selection_count, selection.count, selection.count),
            Modifier.weight(1f),
            style = MaterialTheme.typography.titleMedium,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        IconButton(onClick = { files().singleOrNull()?.let { renaming = it } }, enabled = selection.count == 1, modifier = Modifier.testTag("selection_rename")) {
            Icon(painterResource(R.drawable.ic_edit), contentDescription = stringResource(R.string.file_rename))
        }
        IconButton(onClick = { files().takeIf { it.isNotEmpty() }?.let { moving = it } }, modifier = Modifier.testTag("selection_move")) {
            Icon(painterResource(R.drawable.ic_drive_file_move), contentDescription = stringResource(R.string.file_move))
        }
        IconButton(
            onClick = {
                files().takeIf { it.isNotEmpty() }?.let { confirmTrash = it }
            },
            modifier = Modifier.testTag("selection_delete"),
        ) {
            Icon(painterResource(R.drawable.ic_delete), contentDescription = stringResource(R.string.file_delete))
        }
        IconButton(onClick = { menuOpen = true }) {
            Icon(painterResource(R.drawable.ic_more_vert), contentDescription = stringResource(R.string.action_more))
        }
        DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
            DropdownMenuItem(text = { Text(stringResource(R.string.selection_all)) }, onClick = { menuOpen = false; selection.selectAll(tracks()) })
            if (onRemoveFromPlaylist != null) {
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.selection_remove_from_playlist)) },
                    onClick = {
                        menuOpen = false
                        onRemoveFromPlaylist(selection.selected(tracks()))
                        selection.clear()
                    },
                )
            }
        }
    }

    confirmTrash?.let { chosen ->
        AlertDialog(
            onDismissRequest = { confirmTrash = null },
            title = { Text(pluralStringResource(R.plurals.file_delete_title, chosen.size, chosen.size)) },
            text = { Text(stringResource(R.string.file_delete_message)) },
            confirmButton = {
                TextButton(
                    onClick = {
                        confirmTrash = null
                        scope.launch {
                            val sender = viewModel.trashRequest(chosen) ?: return@launch
                            trashing = chosen
                            trashLauncher.launch(IntentSenderRequest.Builder(sender).build())
                        }
                    },
                    modifier = Modifier.testTag("confirm_trash"),
                ) { Text(stringResource(R.string.delete)) }
            },
            dismissButton = { TextButton(onClick = { confirmTrash = null }) { Text(stringResource(R.string.cancel)) } },
        )
    }

    renaming?.let { track ->
        RenameDialog(track, viewModel, onDismiss = { renaming = null }, onDone = { renaming = null; selection.clear() })
    }
    moving?.let { chosen ->
        MoveDialog(viewModel, onDismiss = { moving = null }) { folder, relativePath ->
            viewModel.move(chosen, folder, relativePath)
            moving = null
            selection.clear()
        }
    }
}

@Composable
private fun RenameDialog(track: TrackEntity, viewModel: FileOpsViewModel, onDismiss: () -> Unit, onDone: () -> Unit) {
    val scope = rememberCoroutineScope()
    val ext = track.fileName.substringAfterLast('.', "")
    var name by remember { mutableStateOf(track.fileName.substringBeforeLast('.')) }
    var error by remember { mutableStateOf<Int?>(null) }
    var busy by remember { mutableStateOf(false) }
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(stringResource(R.string.file_rename)) },
        text = {
            Column {
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it; error = null },
                    singleLine = true,
                    suffix = { if (ext.isNotEmpty()) Text(".$ext") },
                    isError = error != null,
                    supportingText = { error?.let { Text(stringResource(it)) } },
                    modifier = Modifier.fillMaxWidth().testTag("rename_field"),
                )
                Text(stringResource(R.string.file_rename_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        },
        confirmButton = {
            TextButton(
                onClick = {
                    busy = true
                    scope.launch {
                        when (viewModel.rename(track, name)) {
                            RenameResult.OK -> onDone()
                            RenameResult.INVALID -> error = R.string.file_name_invalid
                            RenameResult.EXISTS -> error = R.string.file_name_exists
                            RenameResult.FAILED -> error = R.string.file_rename_failed
                        }
                        busy = false
                    }
                },
                enabled = !busy && name.isNotBlank(),
                modifier = Modifier.testTag("rename_confirm"),
            ) { Text(stringResource(R.string.save)) }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !busy) { Text(stringResource(R.string.cancel)) } },
    )
}

/** 选择移动的目标：先选音乐文件夹，再逐级进入子文件夹，可以新建文件夹。 */
@Composable
private fun MoveDialog(viewModel: FileOpsViewModel, onDismiss: () -> Unit, onMove: (LibraryFolderEntity, String) -> Unit) {
    val scope = rememberCoroutineScope()
    var roots by remember { mutableStateOf<List<LibraryFolderEntity>>(emptyList()) }
    var folder by remember { mutableStateOf<LibraryFolderEntity?>(null) }
    var path by remember { mutableStateOf("") }
    var dirs by remember { mutableStateOf<List<String>>(emptyList()) }
    var creating by remember { mutableStateOf(false) }
    val listState = rememberLazyListState()

    LaunchedEffect(Unit) { roots = viewModel.folders() }
    LaunchedEffect(folder, path) { dirs = folder?.let { viewModel.subdirectories(it, path) }.orEmpty() }

    val current = folder
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.file_move_to)) },
        text = {
            Column {
                Text(
                    current?.let { it.displayName + "/" + path.removePrefix(it.relativePath) } ?: stringResource(R.string.file_move_choose_folder),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                LazyColumn(Modifier.heightIn(max = 360.dp).verticalScrollbar(listState), state = listState) {
                    if (current == null) {
                        items(roots, key = { it.id }) { root ->
                            FolderLine(root.displayName) { folder = root; path = root.relativePath }
                        }
                    } else {
                        item {
                            FolderLine("..") {
                                if (path == current.relativePath) folder = null else path = path.trimEnd('/').substringBeforeLast('/', "").let { if (it.isEmpty()) "" else "$it/" }
                            }
                        }
                        items(dirs, key = { it }) { name -> FolderLine(name) { path = "$path$name/" } }
                    }
                }
            }
        },
        confirmButton = {
            Row {
                TextButton(onClick = { creating = true }, enabled = current != null) { Text(stringResource(R.string.file_new_folder)) }
                TextButton(onClick = { current?.let { onMove(it, path) } }, enabled = current != null, modifier = Modifier.testTag("move_here")) {
                    Text(stringResource(R.string.file_move_here))
                }
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )

    if (creating && current != null) {
        var name by remember { mutableStateOf("") }
        AlertDialog(
            onDismissRequest = { creating = false },
            title = { Text(stringResource(R.string.file_new_folder)) },
            text = { OutlinedTextField(value = name, onValueChange = { name = it }, singleLine = true, modifier = Modifier.fillMaxWidth()) },
            confirmButton = {
                TextButton(onClick = {
                    scope.launch {
                        viewModel.createDirectory(current, path, name)?.let { path = it }
                        creating = false
                    }
                }, enabled = name.isNotBlank()) { Text(stringResource(R.string.confirm)) }
            },
            dismissButton = { TextButton(onClick = { creating = false }) { Text(stringResource(R.string.cancel)) } },
        )
    }
}

@Composable
private fun FolderLine(name: String, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(painterResource(R.drawable.ic_folder), contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(name, Modifier.padding(start = 16.dp), maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}
