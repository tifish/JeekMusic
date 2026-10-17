package com.tinyfish.jeekmusic.ui.components

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.cover.CoverFetchProgress
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.online.DownloadKind
import com.tinyfish.jeekmusic.data.online.ListDownloadScheduler
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

@HiltViewModel
class ListDownloadViewModel @Inject constructor(
    private val scheduler: ListDownloadScheduler,
    private val settings: SettingsRepository,
) : ViewModel() {
    private val progress = DownloadKind.entries.associateWith { kind ->
        scheduler.progress(kind).stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)
    }

    fun progress(kind: DownloadKind): StateFlow<CoverFetchProgress?> = progress.getValue(kind)

    suspend fun enabled(kind: DownloadKind): Boolean = settings.current().let {
        if (kind == DownloadKind.COVERS) it.onlineCovers else it.onlineLyrics
    }

    suspend fun enable(kind: DownloadKind) {
        if (kind == DownloadKind.COVERS) settings.setOnlineCovers(true) else settings.setOnlineLyrics(true)
    }

    fun start(kind: DownloadKind, tracks: List<TrackEntity>) = scheduler.start(kind, tracks.map { it.id })

    fun cancel(kind: DownloadKind) = scheduler.cancel(kind)
}

/**
 * 列表标题栏上的"下载"按钮（需求 C-6、LY-13）：给这个列表里的所有歌下载封面或歌词。
 * 还没开启在线封面 / 歌词时先说明会发送哪些信息，确认后开启并下载；下载在后台进行，对话框显示进度。
 */
@Composable
fun ListDownloadButton(tracks: suspend () -> List<TrackEntity>, viewModel: ListDownloadViewModel = hiltViewModel()) {
    val scope = rememberCoroutineScope()
    var menuOpen by remember { mutableStateOf(false) }
    var asking by remember { mutableStateOf<DownloadKind?>(null) }
    var showing by remember { mutableStateOf<DownloadKind?>(null) }

    fun start(kind: DownloadKind) = scope.launch {
        viewModel.start(kind, tracks())
        showing = kind
    }

    IconButton(onClick = { menuOpen = true }, modifier = Modifier.testTag("list_download")) {
        Icon(painterResource(R.drawable.ic_download), contentDescription = stringResource(R.string.list_download))
    }
    DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
        DownloadKind.entries.forEach { kind ->
            DropdownMenuItem(
                text = { Text(stringResource(if (kind == DownloadKind.COVERS) R.string.list_download_covers else R.string.list_download_lyrics)) },
                onClick = {
                    menuOpen = false
                    scope.launch { if (viewModel.enabled(kind)) start(kind) else asking = kind }
                },
                modifier = Modifier.testTag("list_download_${kind.name}"),
            )
        }
    }

    asking?.let { kind ->
        val covers = kind == DownloadKind.COVERS
        AlertDialog(
            onDismissRequest = { asking = null },
            title = { Text(stringResource(if (covers) R.string.online_covers_privacy_title else R.string.online_lyrics_privacy_title)) },
            text = { Text(stringResource(if (covers) R.string.online_covers_privacy else R.string.online_lyrics_privacy)) },
            confirmButton = {
                TextButton(onClick = {
                    asking = null
                    scope.launch {
                        viewModel.enable(kind)
                        start(kind)
                    }
                }) { Text(stringResource(R.string.list_download_enable)) }
            },
            dismissButton = { TextButton(onClick = { asking = null }) { Text(stringResource(R.string.cancel)) } },
        )
    }

    showing?.let { kind -> DownloadProgressDialog(kind, viewModel) { showing = null } }
}

@Composable
private fun DownloadProgressDialog(kind: DownloadKind, viewModel: ListDownloadViewModel, onDismiss: () -> Unit) {
    val progress by viewModel.progress(kind).collectAsStateWithLifecycle()
    val p = progress
    val finished = p != null && !p.running && !p.waiting
    val covers = kind == DownloadKind.COVERS
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(if (covers) R.string.list_download_covers else R.string.list_download_lyrics)) },
        text = {
            Column {
                Text(
                    when {
                        p == null || p.waiting -> stringResource(R.string.list_download_waiting)
                        p.checking -> stringResource(R.string.list_download_checking)
                        finished && covers -> stringResource(R.string.list_download_covers_done, p.total, p.found)
                        finished -> stringResource(R.string.list_download_lyrics_done, p.total, p.found)
                        else -> stringResource(R.string.list_download_progress, p.done, p.total, p.found)
                    },
                    modifier = Modifier.testTag("list_download_status"),
                )
                if (p != null && p.running && p.total > 0) {
                    LinearProgressIndicator(
                        progress = { p.done.toFloat() / p.total },
                        modifier = Modifier.fillMaxWidth().padding(top = 16.dp),
                    )
                }
                if (!finished) {
                    Text(
                        stringResource(R.string.list_download_background_hint),
                        Modifier.padding(top = 12.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(if (finished) R.string.confirm else R.string.list_download_background)) }
        },
        dismissButton = {
            if (!finished) {
                TextButton(onClick = { viewModel.cancel(kind); onDismiss() }) { Text(stringResource(R.string.list_download_stop)) }
            }
        },
    )
}
