package com.tinyfish.jeekmusic.ui.main

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.BuildConfig
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.update.AppUpdater
import com.tinyfish.jeekmusic.update.UpdateState
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject

@HiltViewModel
class UpdateViewModel @Inject constructor(val updater: AppUpdater) : ViewModel()

/** 发现新版本时的提示、下载进度和失败提示（需求 U-6）。放在最外层，设置页里手动检查时也能显示。 */
@Composable
fun UpdatePrompt(viewModel: UpdateViewModel = hiltViewModel()) {
    val updater = viewModel.updater
    val state by updater.state.collectAsStateWithLifecycle()
    when (val s = state) {
        UpdateState.Idle -> Unit
        is UpdateState.Available -> AlertDialog(
            onDismissRequest = { updater.dismiss(skip = false) },
            title = { Text(stringResource(R.string.update_available_title, s.info.versionName)) },
            text = {
                Column {
                    Text(
                        stringResource(R.string.update_current_version, BuildConfig.VERSION_NAME),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    if (s.info.notes.isNotEmpty()) {
                        Text(
                            s.info.notes,
                            Modifier.padding(top = 8.dp).heightIn(max = 240.dp).verticalScroll(rememberScrollState()),
                            style = MaterialTheme.typography.bodyMedium,
                        )
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = { updater.downloadAndInstall(s.info) }, modifier = Modifier.testTag("update_install")) {
                    Text(stringResource(R.string.update_install))
                }
            },
            dismissButton = {
                Row {
                    TextButton(onClick = { updater.dismiss(skip = true) }) { Text(stringResource(R.string.update_skip)) }
                    TextButton(onClick = { updater.dismiss(skip = false) }) { Text(stringResource(R.string.update_later)) }
                }
            },
        )
        is UpdateState.Downloading -> AlertDialog(
            onDismissRequest = {},
            title = { Text(stringResource(R.string.update_downloading, s.info.versionName)) },
            text = {
                Column {
                    LinearProgressIndicator(progress = { s.progress }, modifier = Modifier.fillMaxWidth())
                    Text("${(s.progress * 100).toInt()}%", Modifier.padding(top = 8.dp), style = MaterialTheme.typography.bodySmall)
                }
            },
            confirmButton = {},
        )
        is UpdateState.Installing -> Unit // 系统的安装确认界面接管
        is UpdateState.Failed -> AlertDialog(
            onDismissRequest = { updater.dismiss(skip = false) },
            title = { Text(stringResource(R.string.update_failed)) },
            text = { Text(s.message ?: stringResource(R.string.update_failed_unknown)) },
            confirmButton = { TextButton(onClick = { updater.downloadAndInstall(s.info) }) { Text(stringResource(R.string.retry)) } },
            dismissButton = { TextButton(onClick = { updater.dismiss(skip = false) }) { Text(stringResource(R.string.close)) } },
        )
    }
}
