package com.tinyfish.jeekmusic.ui.components

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.analysis.LanguageFilter
import com.tinyfish.jeekmusic.analysis.LanguageTask
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.ui.main.MainViewModel
import kotlinx.coroutines.launch

@Composable fun languageFilterLabel(value: LanguageFilter): String = stringResource(when(value) {
    LanguageFilter.CHINESE -> R.string.language_chinese
    LanguageFilter.NON_CHINESE -> R.string.language_non_chinese
    LanguageFilter.ENGLISH -> R.string.language_english
    LanguageFilter.NON_ENGLISH -> R.string.language_non_english
})

@Composable private fun LanguageDownloadDialog(onConfirm: () -> Unit, onDismiss: () -> Unit) = AlertDialog(
    onDismissRequest = onDismiss, title = { Text(stringResource(R.string.language_download_title)) },
    text = { Text(stringResource(R.string.language_download_hint)) },
    confirmButton = { TextButton(onClick = onConfirm) { Text(stringResource(R.string.language_download)) } },
    dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
)

@Composable private fun languageTaskLabel(task: LanguageTask): String = when(task.state) {
    "preparing" -> stringResource(R.string.language_downloading, task.downloaded / 1_000_000)
    "running" -> stringResource(R.string.language_progress, task.processed, task.total)
    "cancelling" -> stringResource(R.string.language_cancelling)
    "cancelled" -> stringResource(R.string.language_cancelled)
    "completed", "completed_with_errors" -> stringResource(R.string.language_complete, task.processed, task.total, task.failed)
    "failed" -> stringResource(R.string.language_failed)
    "download_required" -> stringResource(R.string.language_model_required)
    else -> stringResource(R.string.language_idle)
}

@OptIn(ExperimentalLayoutApi::class)
@Composable fun LanguageFilterControls(vm: MainViewModel) {
    val filter by vm.tagFilter.collectAsStateWithLifecycle()
    val task by vm.languageAnalysis.task.collectAsStateWithLifecycle()
    var download by remember { mutableStateOf(false) }
    Text(stringResource(R.string.language_group), Modifier.padding(top = 12.dp), style = MaterialTheme.typography.labelMedium)
    Text(stringResource(R.string.language_filter_hint), style = MaterialTheme.typography.bodySmall)
    for (group in listOf(true, false)) FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        LanguageFilter.entries.filter { it.chineseGroup == group }.forEach { value ->
            FilterChip(selected = value in filter.languages, onClick = { vm.toggleLanguage(value) },
                label = { Text(languageFilterLabel(value)) }, colors = selectedChipColors(), modifier = Modifier.testTag("language_${value.name}"))
        }
    }
    Text(languageTaskLabel(task), style = MaterialTheme.typography.bodySmall)
    if (task.error != null) Text(task.error!!, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
    TextButton(onClick = { if (task.busy) vm.languageAnalysis.cancel() else if (!vm.languageAnalysis.modelReady || task.state == "download_required") download = true else vm.languageAnalysis.start() },
        enabled = task.state != "cancelling", modifier = Modifier.testTag("language_analyze")) {
        Text(stringResource(if (task.busy) R.string.language_cancel else R.string.language_analyze_library))
    }
    if (download) LanguageDownloadDialog(onConfirm = { download = false; vm.languageAnalysis.start(true) }, onDismiss = { download = false })
}

@Composable internal fun LanguageEditDialog(tracks: List<TrackEntity>, vm: TrackMenuViewModel, onDismiss: () -> Unit) {
    val scope = rememberCoroutineScope()
    var current by remember(tracks) { mutableStateOf<List<com.tinyfish.jeekmusic.analysis.SingingLanguage?>>(emptyList()) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf(false) }
    var download by remember { mutableStateOf(false) }
    val task by vm.languageAnalysis.task.collectAsStateWithLifecycle()
    LaunchedEffect(tracks, task) { current = tracks.map { vm.languages.result(it) } }
    val choices = listOf("CHINESE" to R.string.language_chinese, "ENGLISH" to R.string.language_english,
        "CHINESE_ENGLISH" to R.string.language_mixed, "OTHER" to R.string.language_other,
        "INSTRUMENTAL" to R.string.tag_instrumental, "UNKNOWN" to R.string.language_unknown, "AUTO" to R.string.tags_restore_auto)
    AlertDialog(onDismissRequest = { if (!busy) onDismiss() }, title = { Text(stringResource(R.string.language_edit)) },
        text = { Column(Modifier.verticalScrollWithScrollbar(rememberScrollState())) {
            Text(stringResource(R.string.language_filter_hint))
            val value = current.distinct()
            Text(if (value.size > 1) stringResource(R.string.tags_mixed) else value.firstOrNull()?.let { result ->
                val label = if (result.state == "instrumental") stringResource(R.string.tag_instrumental) else if (result.state == "unknown") stringResource(R.string.language_unknown)
                    else result.languages.map { code -> when(code) {
                        "zh", "yue" -> stringResource(R.string.language_chinese)
                        "en" -> stringResource(R.string.language_english)
                        else -> stringResource(R.string.language_other)
                    } }.distinct().joinToString(" / ")
                label + " · " + stringResource(if (result.source == "MANUAL") R.string.tag_manual else R.string.tags_automatic)
            } ?: stringResource(R.string.language_unknown))
            choices.forEach { (value, label) -> TextButton(enabled = !busy, onClick = {
                busy = true; error = false
                scope.launch {
                    try { vm.languages.setManual(tracks, value); current = tracks.map { vm.languages.result(it) } }
                    catch (e: kotlinx.coroutines.CancellationException) { throw e }
                    catch (_: Exception) { error = true }
                    finally { busy = false }
                }
            }, modifier = Modifier.testTag("language_set_$value")) { Text(stringResource(label)) } }
            TextButton(enabled = !busy && !task.busy, onClick = {
                if (!vm.languageAnalysis.modelReady) download = true else vm.languageAnalysis.start(targets = tracks, force = true)
            }) { Text(stringResource(R.string.language_reanalyze)) }
            if (error) Text(stringResource(R.string.tags_batch_failed), color = MaterialTheme.colorScheme.error)
        } }, confirmButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text(stringResource(R.string.confirm)) } })
    if (download) LanguageDownloadDialog(onConfirm = { download = false; vm.languageAnalysis.start(true, tracks, true) }, onDismiss = { download = false })
}
