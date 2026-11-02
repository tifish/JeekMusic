package com.tinyfish.jeekmusic.ui.main

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import com.tinyfish.jeekmusic.R

/** Read-only diagnostics are available in release and debug without an exported control port. */
@Composable
fun UpdateDiagnosticsAction(viewModel: UpdateViewModel = hiltViewModel()) {
    val context = LocalContext.current
    var diagnostics by remember { mutableStateOf<String?>(null) }
    Column(Modifier.fillMaxWidth().clickable {
        diagnostics = viewModel.updater.installer.diagnostics().toString(2)
    }.padding(horizontal = 16.dp, vertical = 14.dp).testTag("update_diagnostics")) {
        Text(stringResource(R.string.update_diagnostics), style = MaterialTheme.typography.bodyLarge)
        Text(stringResource(R.string.update_diagnostics_summary), style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    diagnostics?.let { json ->
        AlertDialog(
            onDismissRequest = { diagnostics = null },
            title = { Text(stringResource(R.string.update_diagnostics)) },
            text = { Text(stringResource(R.string.update_diagnostics_summary)) },
            confirmButton = { TextButton(onClick = {
                (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager)
                    .setPrimaryClip(ClipData.newPlainText("JeekMusic update diagnostics", json))
                diagnostics = null
            }, modifier = Modifier.testTag("update_copy_diagnostics")) { Text(stringResource(R.string.update_copy_diagnostics)) } },
            dismissButton = { TextButton(onClick = { diagnostics = null }) { Text(stringResource(R.string.close)) } },
        )
    }
}
