package com.tinyfish.jeekmusic.ui.nowplaying

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.lyrics.OnlineLyrics
import com.tinyfish.jeekmusic.ui.components.formatDuration
import kotlinx.coroutines.launch
import kotlin.math.abs

/**
 * 手动搜索在线歌词（需求 LY-8）：结果列表展示标题、艺术家、专辑、时长差和"同步 / 纯文本"，由用户选择。
 */
@Composable
fun LyricsSearchDialog(viewModel: PlayerViewModel, onDismiss: () -> Unit) {
    val scope = rememberCoroutineScope()
    var track by remember { mutableStateOf<TrackEntity?>(null) }
    var title by remember { mutableStateOf("") }
    var artist by remember { mutableStateOf("") }
    var results by remember { mutableStateOf<List<OnlineLyrics>?>(null) }
    var searching by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf(false) }

    fun search() {
        searching = true
        failed = false
        scope.launch {
            val found = viewModel.searchLyrics(title, artist.ifBlank { null })
            failed = found == null
            results = found.orEmpty().sortedBy { r -> track?.let { abs((r.durationSec ?: 0.0) - it.durationMs / 1000.0) } ?: 0.0 }
            searching = false
        }
    }

    LaunchedEffect(Unit) {
        track = viewModel.currentTrack()?.also {
            title = it.titleTag ?: it.title
            artist = it.artistDisplay
        }
        if (title.isNotBlank()) search()
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.lyrics_search_title)) },
        text = {
            Column {
                OutlinedTextField(value = title, onValueChange = { title = it }, label = { Text(stringResource(R.string.lyrics_title_field)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(value = artist, onValueChange = { artist = it }, label = { Text(stringResource(R.string.lyrics_artist_field)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                TextButton(onClick = ::search, enabled = title.isNotBlank() && !searching, modifier = Modifier.align(Alignment.End)) {
                    Text(stringResource(R.string.lyrics_search_button))
                }
                when {
                    searching -> CircularProgressIndicator(Modifier.align(Alignment.CenterHorizontally).padding(16.dp))
                    failed -> Text(stringResource(R.string.lyrics_search_failed), color = MaterialTheme.colorScheme.error)
                    results?.isEmpty() == true -> Text(stringResource(R.string.search_no_results))
                    else -> LazyColumn(Modifier.heightIn(max = 320.dp)) {
                        items(results.orEmpty(), key = { it.id }) { r ->
                            val current = track
                            Column(
                                Modifier.fillMaxWidth().clickable(enabled = current != null) {
                                    if (current != null) viewModel.saveLyrics(current, r)
                                    onDismiss()
                                }.padding(vertical = 8.dp),
                            ) {
                                Text(r.trackName, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
                                Row {
                                    val diff = current?.let { t -> r.durationSec?.let { (it * 1000 - t.durationMs).toLong() } }
                                    Text(
                                        listOfNotNull(
                                            r.artistName,
                                            r.albumName,
                                            r.durationSec?.let { formatDuration((it * 1000).toLong()) + (diff?.let { d -> " (%+ds)".format(d / 1000) } ?: "") },
                                            stringResource(if (r.isSynced) R.string.lyrics_synced else R.string.lyrics_plain),
                                        ).joinToString(" · "),
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        maxLines = 2,
                                    )
                                }
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}
