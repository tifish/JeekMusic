package com.tinyfish.jeekmusic.ui.settings

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.sync.SyncService
import kotlinx.coroutines.launch

@Composable
fun SyncSettings(sync: SyncService) {
    val status by sync.status.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    var expanded by rememberSaveable { mutableStateOf(false) }
    var name by remember { mutableStateOf(sync.deviceName) }
    var editing by remember { mutableStateOf(false) }
    var history by remember { mutableStateOf<List<Pair<Int, String>>?>(null) }
    var replaced by remember { mutableStateOf<List<Pair<Int, String>>>(emptyList()) }
    // Recovery is offered only for actual overwritten playlists, not as a backup product.
    LaunchedEffect(status) { replaced = sync.history() }
    Column {
        if (status.isNotEmpty() && status != stringResource(R.string.sync_done)) {
            SettingSummary(status, Modifier.padding(horizontal = 16.dp, vertical = 8.dp).testTag("sync_data_status"))
        }
        if (replaced.isNotEmpty()) SettingItem(stringResource(R.string.sync_history), summary = stringResource(R.string.sync_history_summary),
            onClick = { scope.launch { history = sync.history() } })
        SettingItem(stringResource(R.string.sync_advanced), summary = stringResource(R.string.sync_advanced_summary),
            onClick = { expanded = !expanded }, modifier = Modifier.testTag("sync_advanced"))
        if (expanded) {
            var legacy by remember { mutableStateOf(sync.directoryEnabled) }
            SettingItem(stringResource(R.string.sync_device_name), summary = name, onClick = { editing = true }, modifier = Modifier.testTag("sync_device_name"))
            SwitchRow(stringResource(R.string.preference_directory_adapter), legacy,
                { legacy = it; sync.setDirectoryEnabled(it) }, summary = stringResource(R.string.sync_legacy_help),
                modifier = Modifier.testTag("sync_legacy_directory"))
        }
    }
    if (editing) {
        var draft by remember { mutableStateOf(sync.deviceName) }
        AlertDialog(onDismissRequest = { editing = false }, title = { Text(stringResource(R.string.sync_device_name)) },
            text = { OutlinedTextField(value = draft, onValueChange = { draft = it }, singleLine = true) },
            confirmButton = { TextButton(onClick = { scope.launch { sync.renameDevice(draft); name = sync.deviceName; editing = false } }) { Text(stringResource(R.string.save)) } },
            dismissButton = { TextButton(onClick = { editing = false }) { Text(stringResource(R.string.cancel)) } })
    }
    history?.let { entries ->
        AlertDialog(onDismissRequest = { history = null }, title = { Text(stringResource(R.string.sync_history)) },
            text = {
                if (entries.isEmpty()) Text(stringResource(R.string.sync_history_empty))
                else LazyColumn { items(entries, key = { it.first }) { (id, title) ->
                    TextButton(onClick = { scope.launch { sync.restoreHistory(id); history = null } }) {
                        Text(stringResource(R.string.sync_restore_playlist, title))
                    }
                } }
            }, confirmButton = { TextButton(onClick = { history = null }) { Text(stringResource(R.string.confirm)) } })
    }
}
