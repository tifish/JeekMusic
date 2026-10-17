package com.tinyfish.jeekmusic.ui.settings

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.tinyfish.jeekmusic.R

/** 第三方库及其许可证（docs/design.md §13）。 */
private val LICENSES = listOf(
    "JeekMusic" to "MIT",
    "Android Jetpack (AndroidX, Compose, Room, DataStore, WorkManager, Navigation 3)" to "Apache-2.0",
    "Media3 / ExoPlayer" to "Apache-2.0",
    "Kotlin, kotlinx.coroutines" to "Apache-2.0",
    "Dagger / Hilt" to "Apache-2.0",
    "Coil" to "Apache-2.0",
    "Reorderable (Calvin Liang)" to "Apache-2.0",
    "Kyant0/taglib" to "Apache-2.0",
    "TagLib" to "LGPL-2.1 / MPL-1.1",
    "LiteRT (TensorFlow Lite)" to "Apache-2.0",
    "YAMNet audio event classifier (Google, AudioSet)" to "Apache-2.0",
    "LRCLIB (lrclib.net)" to "Online lyrics database, used only when online lyrics are turned on",
)

@Composable
fun LicensesDialog(onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.settings_licenses)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                LICENSES.forEach { (name, license) ->
                    Column(Modifier.padding(vertical = 6.dp)) {
                        Text(name, style = MaterialTheme.typography.bodyMedium)
                        Text(license, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.confirm)) } },
    )
}
