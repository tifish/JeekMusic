package com.tinyfish.jeekmusic.ui.components

import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
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
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.cover.CoverCandidate
import com.tinyfish.jeekmusic.data.cover.CoverSource
import com.tinyfish.jeekmusic.data.db.TrackEntity
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/**
 * 在线搜索封面（需求 C-3）：iTunes 和 MusicBrainz 的结果以缩略图网格列出，点一张即按设置的方式保存。
 * 还没开启在线封面时先说明会发送哪些信息，确认后开启并搜索。
 */
@Composable
fun CoverSearchDialog(track: TrackEntity, viewModel: TrackMenuViewModel, onlineCovers: Boolean, onDismiss: () -> Unit) {
    val scope = rememberCoroutineScope()
    var enabled by remember { mutableStateOf(onlineCovers) }
    var title by remember { mutableStateOf(track.titleTag ?: track.title) }
    var artist by remember { mutableStateOf(track.artistDisplay) }
    // 有缩略图的候选；两个来源和各张缩略图谁先返回就先显示谁
    val results = remember { mutableStateListOf<Pair<CoverCandidate, ImageBitmap>>() }
    val ranks = remember { HashMap<CoverCandidate, Int>() }
    var pending by remember { mutableIntStateOf(0) }
    var searchJob by remember { mutableStateOf<Job?>(null) }
    var saving by remember { mutableStateOf(false) }
    val searching = pending > 0

    fun search() {
        searchJob?.cancel()
        results.clear()
        ranks.clear()
        pending = 0
        searchJob = scope.launch {
            val query = title to artist.ifBlank { null }
            CoverSource.entries.forEach { source ->
                pending++
                launch {
                    val found = viewModel.searchCovers(source, query.first, query.second)
                    found.forEachIndexed { i, c ->
                        // 各来源内对得上的已排在前面；iTunes 在前
                        ranks[c] = source.ordinal * 100 + i
                        pending++
                        launch {
                            val thumb = viewModel.coverThumbnail(c)?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }?.asImageBitmap()
                            if (thumb != null) {
                                val at = results.indexOfFirst { (ranks[it.first] ?: 0) > (ranks[c] ?: 0) }
                                results.add(if (at < 0) results.size else at, c to thumb)
                            }
                            pending--
                        }
                    }
                    pending--
                }
            }
        }
    }

    if (!enabled) {
        AlertDialog(
            onDismissRequest = onDismiss,
            title = { Text(stringResource(R.string.online_covers_privacy_title)) },
            text = { Text(stringResource(R.string.online_covers_privacy)) },
            confirmButton = {
                TextButton(onClick = { viewModel.enableOnlineCovers(); enabled = true }, modifier = Modifier.testTag("enable_covers")) {
                    Text(stringResource(R.string.cover_enable_and_search))
                }
            },
            dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
        )
        return
    }

    LaunchedEffect(Unit) { if (title.isNotBlank()) search() }

    AlertDialog(
        onDismissRequest = { if (!saving) onDismiss() },
        title = { Text(stringResource(R.string.cover_search_title)) },
        text = {
            Column {
                OutlinedTextField(value = title, onValueChange = { title = it }, label = { Text(stringResource(R.string.lyrics_title_field)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(value = artist, onValueChange = { artist = it }, label = { Text(stringResource(R.string.lyrics_artist_field)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                TextButton(onClick = ::search, enabled = title.isNotBlank() && !searching && !saving, modifier = Modifier.align(Alignment.End)) {
                    Text(stringResource(R.string.lyrics_search_button))
                }
                when {
                    saving || (searching && results.isEmpty()) -> CircularProgressIndicator(Modifier.align(Alignment.CenterHorizontally).padding(16.dp))
                    results.isEmpty() -> Text(stringResource(R.string.cover_search_failed))
                    else -> LazyVerticalGrid(
                        columns = GridCells.Fixed(3),
                        modifier = Modifier.heightIn(max = 360.dp).testTag("cover_results"),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        items(results, key = { it.first.thumbUrl }) { (candidate, thumb) ->
                            CoverCell(candidate, thumb) {
                                saving = true
                                scope.launch {
                                    viewModel.saveCover(track, candidate)
                                    onDismiss()
                                }
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = onDismiss, enabled = !saving) { Text(stringResource(R.string.cancel)) } },
    )
}

@Composable
private fun CoverCell(candidate: CoverCandidate, thumb: ImageBitmap, onClick: () -> Unit) {
    Column(Modifier.clickable(onClick = onClick)) {
        Box(Modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(6.dp))) {
            Image(thumb, contentDescription = candidate.album, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxWidth().aspectRatio(1f))
        }
        Text(
            candidate.album ?: candidate.title,
            style = MaterialTheme.typography.labelSmall,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            if (candidate.source == CoverSource.ITUNES) "iTunes" else "MusicBrainz",
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
        )
    }
}
