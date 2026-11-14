package com.tinyfish.jeekmusic.ui.components

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.analysis.*
import com.tinyfish.jeekmusic.data.db.TrackEntity
import kotlinx.coroutines.launch

@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun BatchTagDialog(tracks: List<TrackEntity>, vm: TrackMenuViewModel, onDismiss: () -> Unit) {
    val scope = rememberCoroutineScope()
    var infos by remember(tracks) { mutableStateOf<List<TagInfo>?>(null) }
    var edits by remember(tracks) { mutableStateOf<Map<TagDimension, Tag?>>(emptyMap()) }
    var busy by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf(false) }
    LaunchedEffect(tracks) { infos = tracks.map { vm.tagInfo(it) } }
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(stringResource(R.string.tags_batch_title, tracks.size)) },
        text = {
            Column(Modifier.verticalScrollWithScrollbar(rememberScrollState())) {
                Text(stringResource(R.string.tags_batch_hint))
                infos?.let { current -> TagDimension.entries.forEach { dimension ->
                    Text(dimensionLabel(dimension), Modifier.padding(top = 12.dp), color = MaterialTheme.colorScheme.primary)
                    val values = current.map { it.tags[dimension] }.distinct()
                    val sources = current.map { dimension in it.manual }.distinct()
                    Text((if (values.size == 1) values.single()?.let { tagLabel(it) } ?: stringResource(R.string.tag_none)
                        else stringResource(R.string.tags_mixed)) + " · " +
                        stringResource(if (sources.size > 1) R.string.tags_mixed_source else if (sources.singleOrNull() == true) R.string.tag_manual else R.string.tags_automatic),
                        style = MaterialTheme.typography.bodySmall)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilterChip(selected = dimension !in edits, enabled = !busy,
                            onClick = { edits = edits - dimension }, label = { Text(stringResource(R.string.tags_unchanged)) }, colors = selectedChipColors())
                        FilterChip(selected = dimension in edits && edits[dimension] == null, enabled = !busy,
                            onClick = { edits = edits + (dimension to null) }, label = { Text(stringResource(R.string.tags_restore_auto)) }, colors = selectedChipColors(), modifier = Modifier.testTag("batch_auto_${dimension.name}"))
                        Tag.of(dimension).forEach { tag ->
                            FilterChip(selected = edits[dimension] == tag, enabled = !busy,
                                onClick = { edits = edits + (dimension to tag) }, label = { Text(tagLabel(tag)) }, colors = selectedChipColors(), modifier = Modifier.testTag("batch_${tag.name}"))
                        }
                    }
                } }
                if (failed) Text(stringResource(R.string.tags_batch_failed), color = MaterialTheme.colorScheme.error)
            }
        },
        confirmButton = { TextButton(enabled = !busy && infos != null && edits.isNotEmpty() && tracks.size <= 1000,
            onClick = {
                busy = true; failed = false
                scope.launch {
                    try { vm.applyTags(tracks, edits); onDismiss() }
                    catch (e: kotlinx.coroutines.CancellationException) { throw e }
                    catch (_: Exception) { failed = true }
                    finally { busy = false }
                }
            }, modifier = Modifier.testTag("batch_apply")) { Text(stringResource(R.string.tags_apply)) } },
        dismissButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}
