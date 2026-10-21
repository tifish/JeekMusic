package com.tinyfish.jeekmusic.ui.settings

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.sync.SyncService
import kotlinx.coroutines.launch
import java.text.DateFormat
import java.util.Date

@Composable
fun SyncSettings(sync: SyncService) {
    val status by sync.status.collectAsStateWithLifecycle()
    val devices by sync.devices.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    var name by remember { mutableStateOf(sync.deviceName) }
    var editing by remember { mutableStateOf(false) }
    var history by remember { mutableStateOf<List<Pair<Int, String>>?>(null) }
    Column {
        SettingItem(stringResource(R.string.sync_device_name), summary = name, onClick = { editing = true })
        SettingItem(stringResource(R.string.sync_now), summary = status.ifEmpty { stringResource(R.string.sync_summary) },
            onClick = { scope.launch { sync.syncNow() } })
        for ((device, at) in devices) SettingItem(device, summary = DateFormat.getDateTimeInstance().format(Date(at)), onClick = {})
        SettingItem(stringResource(R.string.sync_history), summary = stringResource(R.string.sync_history_summary),
            onClick = { scope.launch { history = sync.history() } })
    }
    if (editing) {
        var draft by remember { mutableStateOf(name) }
        AlertDialog(onDismissRequest = { editing = false }, title = { Text(stringResource(R.string.sync_device_name)) },
            text = { OutlinedTextField(value = draft, onValueChange = { draft = it }, singleLine = true) },
            confirmButton = { TextButton(onClick = { scope.launch { sync.renameDevice(draft); name = sync.deviceName; editing = false; sync.syncNow() } }) { Text(stringResource(R.string.save)) } },
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
