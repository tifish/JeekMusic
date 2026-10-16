package com.tinyfish.jeekmusic.ui.main

import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.backup.Backup
import com.tinyfish.jeekmusic.data.backup.BackupManager
import com.tinyfish.jeekmusic.ui.navigation.MessageBus
import com.tinyfish.jeekmusic.ui.navigation.UiMessage
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

@HiltViewModel
class RestorePromptViewModel @Inject constructor(
    private val backups: BackupManager,
    private val messages: MessageBus,
) : ViewModel() {
    val offer: StateFlow<Backup?> = backups.offer

    fun restore() = viewModelScope.launch {
        val report = backups.restoreOffered() ?: return@launch
        messages.post(UiMessage(R.string.restore_done, "${report.playlists} / ${report.items} / ${report.missing}"))
    }

    fun dismiss() = backups.dismissOffer()
}

/** 添加文件夹后发现备份时的恢复提示（需求 B-3）。放在主界面上，始终处于组合中。 */
@Composable
fun RestorePrompt(viewModel: RestorePromptViewModel = hiltViewModel()) {
    val backup by viewModel.offer.collectAsStateWithLifecycle()
    val offer = backup ?: return
    AlertDialog(
        onDismissRequest = viewModel::dismiss,
        title = { Text(stringResource(R.string.restore_found_title)) },
        text = {
            Text(pluralStringResource(R.plurals.restore_found_message, offer.playlists.size, offer.exportedAt.take(16).replace('T', ' '), offer.playlists.size))
        },
        confirmButton = { TextButton(onClick = { viewModel.restore() }) { Text(stringResource(R.string.restore)) } },
        dismissButton = { TextButton(onClick = viewModel::dismiss) { Text(stringResource(R.string.cancel)) } },
    )
}
