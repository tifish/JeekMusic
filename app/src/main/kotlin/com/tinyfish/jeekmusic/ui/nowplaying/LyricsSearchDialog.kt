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
import androidx.compose.runtime.mutableIntStateOf
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
import com.tinyfish.jeekmusic.ui.components.sourceName
import kotlinx.coroutines.Job
import com.tinyfish.jeekmusic.ui.components.formatDuration
import kotlinx.coroutines.launch
import kotlin.math.abs

/**
 * 手动搜索在线歌词（需求 LY-8）：设置里启用的各来源分别搜索，谁先返回先显示谁（国内打不开的来源不会拖住其他来源）。
 * 结果列表展示标题、艺术家、专辑、时长差、"同步 / 纯文本"和来源，按时长差排序，由用户选择。
 */
@Composable
fun LyricsSearchDialog(viewModel: PlayerViewModel, onDismiss: () -> Unit) {
    val scope = rememberCoroutineScope()
    var track by remember { mutableStateOf<TrackEntity?>(null) }
    var title by remember { mutableStateOf("") }
    var artist by remember { mutableStateOf("") }
    var results by remember { mutableStateOf<List<OnlineLyrics>>(emptyList()) }
    var pending by remember { mutableIntStateOf(0) }
    // 所有来源都出错（而不是都没找到）时提示检查网络
    var failures by remember { mutableIntStateOf(0) }
    var searchJob by remember { mutableStateOf<Job?>(null) }
    val searching = pending > 0

    fun search() {
        searchJob?.cancel()
        results = emptyList()
        failures = 0
        searchJob = scope.launch {
            val sources = viewModel.lyricsSources()
            pending = sources.size
            val query = title to artist.ifBlank { null }
            sources.forEach { source ->
                launch {
                    val found = viewModel.searchLyrics(source, query.first, query.second)
                    if (found == null) failures++
                    // 对得上歌名和艺术家的排前面，再按时长差、来源的顺序排
                    results = (results + found.orEmpty()).sortedWith(
                        compareBy<OnlineLyrics> { !it.matched }
                            .thenBy { r -> track?.let { abs((r.durationSec ?: 0.0) - it.durationMs / 1000.0) } ?: 0.0 }
                            .thenBy { it.source.ordinal },
                    )
                    pending--
                }
            }
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
                    searching && results.isEmpty() -> CircularProgressIndicator(Modifier.align(Alignment.CenterHorizontally).padding(16.dp))
                    results.isEmpty() && failures > 0 -> Text(stringResource(R.string.lyrics_search_failed), color = MaterialTheme.colorScheme.error)
                    results.isEmpty() -> Text(stringResource(R.string.search_no_results))
                    else -> LazyColumn(Modifier.heightIn(max = 320.dp)) {
                        items(results, key = { "${it.source}:${it.id}" }) { r ->
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
                                            sourceName(r.source),
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
