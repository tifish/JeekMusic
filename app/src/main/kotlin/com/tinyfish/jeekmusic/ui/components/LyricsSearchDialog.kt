package com.tinyfish.jeekmusic.ui.components

import androidx.compose.foundation.lazy.rememberLazyListState
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
import androidx.compose.ui.platform.testTag
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import com.tinyfish.jeekmusic.data.lyrics.OnlineLyrics
import com.tinyfish.jeekmusic.data.settings.LyricsSaveLocation
import com.tinyfish.jeekmusic.data.settings.OnlineSource
import com.tinyfish.jeekmusic.ui.navigation.MessageBus
import com.tinyfish.jeekmusic.ui.navigation.UiMessage
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.math.abs

/** 打开搜索歌词对话框的界面（播放页、歌曲的"更多"菜单）要提供的操作。 */
interface LyricsSearchHost {
    suspend fun lyricsSources(): List<OnlineSource>

    /** 搜索一个来源；网络出错时返回 null。 */
    suspend fun searchLyrics(source: OnlineSource, title: String, artist: String?): List<OnlineLyrics>?

    /** 保存选中的歌词（替换现有的），在 ViewModel 的作用域里进行：对话框选中后立即关闭。 */
    fun saveLyrics(track: TrackEntity, lyrics: OnlineLyrics)
}

/** 保存选中的在线歌词并提示保存到了哪里（需求 LY-10、LY-15）。 */
suspend fun saveChosenLyrics(repository: LyricsRepository, messages: MessageBus, track: TrackEntity, lyrics: OnlineLyrics) {
    val text = lyrics.text ?: return
    val result = repository.save(track, text)
    messages.post(
        UiMessage(
            when (result) {
                LyricsSaveLocation.SIDECAR -> R.string.lyrics_saved_folder
                LyricsSaveLocation.HIDDEN_FOLDER -> if (repository.embedDeferred(track)) R.string.lyrics_saved_after_playing else R.string.lyrics_saved_hidden
                LyricsSaveLocation.EMBED -> R.string.lyrics_saved_embed
                LyricsSaveLocation.APP_CACHE -> R.string.lyrics_saved_cache
            },
        ),
    )
}

/**
 * 手动搜索在线歌词（需求 LY-8、LY-15）：设置里启用的各来源分别搜索，谁先返回先显示谁（国内打不开的来源不会拖住其他来源）。
 * 结果列表展示标题、艺术家、专辑、时长差、"同步 / 纯文本"和来源，按时长差排序，由用户选择；选中的替换这首歌现有的歌词。
 * [onlineLyrics] 为 false（还没开启在线歌词）时先说明会发送哪些信息，确认后调用 [onEnable] 开启并搜索。
 */
@Composable
fun LyricsSearchDialog(
    track: TrackEntity,
    host: LyricsSearchHost,
    onDismiss: () -> Unit,
    onlineLyrics: Boolean = true,
    onEnable: () -> Unit = {},
) {
    val scope = rememberCoroutineScope()
    var enabled by remember { mutableStateOf(onlineLyrics) }
    var title by remember { mutableStateOf(track.titleTag ?: track.title) }
    var artist by remember { mutableStateOf(track.artistDisplay) }
    var results by remember { mutableStateOf<List<OnlineLyrics>>(emptyList()) }
    var pending by remember { mutableIntStateOf(0) }
    // 所有来源都出错（而不是都没找到）时提示检查网络
    var failures by remember { mutableIntStateOf(0) }
    var searchJob by remember { mutableStateOf<Job?>(null) }
    val searching = pending > 0
    val resultsState = rememberLazyListState()

    fun search() {
        searchJob?.cancel()
        results = emptyList()
        failures = 0
        searchJob = scope.launch {
            val sources = host.lyricsSources()
            pending = sources.size
            val query = title to artist.ifBlank { null }
            sources.forEach { source ->
                launch {
                    val found = host.searchLyrics(source, query.first, query.second)
                    if (found == null) failures++
                    // 对得上歌名和艺术家的排前面，再按时长差、来源的顺序排
                    results = (results + found.orEmpty()).sortedWith(
                        compareBy<OnlineLyrics> { !it.matched }
                            .thenBy { r -> abs((r.durationSec ?: 0.0) - track.durationMs / 1000.0) }
                            .thenBy { it.source.ordinal },
                    )
                    pending--
                }
            }
        }
    }

    if (!enabled) {
        AlertDialog(
            onDismissRequest = onDismiss,
            title = { Text(stringResource(R.string.online_lyrics_privacy_title)) },
            text = { Text(stringResource(R.string.online_lyrics_privacy)) },
            confirmButton = {
                TextButton(onClick = { onEnable(); enabled = true }, modifier = Modifier.testTag("enable_lyrics")) {
                    Text(stringResource(R.string.cover_enable_and_search))
                }
            },
            dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
        )
        return
    }

    LaunchedEffect(Unit) { if (title.isNotBlank()) search() }

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
                    else -> LazyColumn(Modifier.heightIn(max = 320.dp).verticalScrollbar(resultsState), state = resultsState) {
                        items(results, key = { "${it.source}:${it.id}" }) { r ->
                            Column(
                                Modifier.fillMaxWidth().clickable {
                                    host.saveLyrics(track, r)
                                    onDismiss()
                                }.padding(vertical = 8.dp),
                            ) {
                                Text(r.trackName, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
                                Row {
                                    val diff = r.durationSec?.let { (it * 1000 - track.durationMs).toLong() }
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
