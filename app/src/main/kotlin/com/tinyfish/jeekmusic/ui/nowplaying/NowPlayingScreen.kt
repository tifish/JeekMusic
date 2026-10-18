package com.tinyfish.jeekmusic.ui.nowplaying

import com.tinyfish.jeekmusic.ui.components.ToggleIconButton
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.TextButton
import androidx.compose.runtime.rememberCoroutineScope
import com.tinyfish.jeekmusic.data.db.ArtistEntity
import com.tinyfish.jeekmusic.ui.components.LocalTrackMenuById
import com.tinyfish.jeekmusic.ui.navigation.LocalNavigator
import kotlinx.coroutines.launch
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.ui.components.LyricsSearchDialog
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.playback.PlayerUiState
import com.tinyfish.jeekmusic.playback.RepeatMode
import com.tinyfish.jeekmusic.ui.components.Artwork
import com.tinyfish.jeekmusic.ui.components.formatDuration

/**
 * 播放页（需求 NP-1 ~ NP-3）：顶部是小封面和歌曲信息，中间是歌词（M3；目前和没有歌词时一样显示大封面），
 * 底部是进度和控制。
 */
@Composable
fun NowPlayingScreen(viewModel: PlayerViewModel, onCollapse: () -> Unit) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    var showQueue by remember { mutableStateOf(false) }
    var showSleepTimer by remember { mutableStateOf(false) }
    var lyricsSearchTrack by remember { mutableStateOf<TrackEntity?>(null) }
    var artistChoices by remember { mutableStateOf<List<ArtistEntity>>(emptyList()) }
    val navigator = LocalNavigator.current
    val openMenu = LocalTrackMenuById.current
    val scope = rememberCoroutineScope()
    BackHandler(onBack = onCollapse)

    // 点击艺术家：只有一个时直接跳转，多个时让用户选（需求 NP-1）
    val openArtist: () -> Unit = {
        state.nowPlaying?.let { np ->
            scope.launch {
                val artists = viewModel.artistsOf(np.trackId)
                if (artists.size == 1) navigator.openArtist(artists.first().nameKey) else artistChoices = artists
            }
        }
    }

    val nowPlaying = state.nowPlaying
    Column(
        Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.surface)
            .systemBarsPadding()
            .testTag("now_playing"),
    ) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onCollapse) {
                Icon(painterResource(R.drawable.ic_expand_more), contentDescription = stringResource(R.string.action_collapse))
            }
        }
        Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp), verticalAlignment = Alignment.CenterVertically) {
            Artwork(nowPlaying?.artworkUri, 56.dp)
            Spacer(Modifier.width(16.dp))
            Column(Modifier.weight(1f)) {
                Text(nowPlaying?.title.orEmpty(), style = MaterialTheme.typography.titleLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(
                    nowPlaying?.artist ?: stringResource(R.string.unknown_artist),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.primary,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.clickable(onClick = openArtist).testTag("now_playing_artist"),
                )
                nowPlaying?.album?.let {
                    Text(
                        it,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.clickable(enabled = nowPlaying.albumKey != null) {
                            nowPlaying.albumKey?.let(navigator::openAlbum)
                        },
                    )
                }
            }
        }

        // 中间是歌词或大封面（需求 NP-2）：默认有歌词显示歌词、没有显示封面；自动下载到歌词后自然切到歌词。
        // 点封面切到歌词、点歌词切到封面；手动的选择只对当前这首歌有效，换歌后恢复默认
        val lyricsState by viewModel.lyrics.collectAsStateWithLifecycle()
        val loaded = lyricsState as? LyricsState.Loaded
        val lyricsPosition = remember { viewModel.position(200) }
        val online by viewModel.onlineLyricsEnabled.collectAsStateWithLifecycle()
        var manualLyrics by remember(nowPlaying?.trackId) { mutableStateOf<Boolean?>(null) }
        val showLyrics = manualLyrics ?: (loaded != null)
        val searchButton: @Composable () -> Unit = {
            // 开启在线歌词后，没有歌词时提供"搜索歌词"
            if (online && lyricsState == LyricsState.None) {
                TextButton(onClick = { scope.launch { lyricsSearchTrack = viewModel.currentTrack() } }, modifier = Modifier.testTag("search_lyrics")) {
                    Text(stringResource(R.string.lyrics_search))
                }
            }
        }
        when {
            showLyrics && loaded != null -> {
                LyricsView(
                    lyrics = loaded.lyrics,
                    offsetMs = loaded.offsetMs,
                    position = lyricsPosition,
                    onSeek = viewModel::seekTo,
                    onTap = { manualLyrics = false },
                    modifier = Modifier.weight(1f),
                )
                if (loaded.lyrics.synced) {
                    LyricsOffsetBar(loaded.offsetMs, onChange = { viewModel.setLyricsOffset(loaded.trackKey, it) }, modifier = Modifier.fillMaxWidth())
                }
            }
            // 点了封面但这首歌没有歌词：显示"暂无歌词"，再点一下回到封面
            showLyrics -> Column(
                Modifier.weight(1f).fillMaxWidth().tapToToggle { manualLyrics = false }.testTag("no_lyrics"),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center,
            ) {
                if (lyricsState == LyricsState.None) {
                    Text(stringResource(R.string.lyrics_none), style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                searchButton()
            }
            else -> Column(
                Modifier.weight(1f).fillMaxWidth().padding(32.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center,
            ) {
                Artwork(
                    nowPlaying?.artworkUri,
                    size = 320.dp,
                    modifier = Modifier.weight(1f, fill = false).aspectRatio(1f).tapToToggle { manualLyrics = true }.testTag("now_playing_cover"),
                    cornerRadius = 16.dp,
                )
                searchButton()
            }
        }

        SeekBar(viewModel, state)
        Controls(viewModel, state)
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ToggleIconButton(
                checked = state.sleepTimer.active,
                onClick = { showSleepTimer = true },
                icon = R.drawable.ic_bedtime,
                contentDescription = stringResource(R.string.menu_sleep_timer),
                modifier = Modifier.testTag("sleep_timer"),
            )
            SleepTimerLabel(state.sleepTimer, Modifier.weight(1f))
            IconButton(onClick = { showQueue = true }, modifier = Modifier.testTag("open_queue")) {
                Icon(painterResource(R.drawable.ic_queue), contentDescription = stringResource(R.string.action_queue))
            }
            IconButton(onClick = { nowPlaying?.let { openMenu(it.trackId) } }, modifier = Modifier.testTag("now_playing_more")) {
                Icon(painterResource(R.drawable.ic_more_vert), contentDescription = stringResource(R.string.action_more))
            }
        }
    }

    if (showQueue) QueueSheet(viewModel, state, onDismiss = { showQueue = false })
    if (showSleepTimer) SleepTimerDialog(viewModel, state.sleepTimer, onDismiss = { showSleepTimer = false })
    lyricsSearchTrack?.let { LyricsSearchDialog(it, viewModel, onDismiss = { lyricsSearchTrack = null }) }
    if (artistChoices.isNotEmpty()) {
        AlertDialog(
            onDismissRequest = { artistChoices = emptyList() },
            title = { Text(stringResource(R.string.choose_artist)) },
            text = {
                Column {
                    artistChoices.forEach { artist ->
                        Text(
                            artist.name.ifEmpty { stringResource(R.string.unknown_artist) },
                            Modifier.fillMaxWidth().clickable { artistChoices = emptyList(); navigator.openArtist(artist.nameKey) }.padding(vertical = 12.dp),
                        )
                    }
                }
            },
            confirmButton = {},
            dismissButton = { TextButton(onClick = { artistChoices = emptyList() }) { Text(stringResource(R.string.cancel)) } },
        )
    }
}

@Composable
private fun SeekBar(viewModel: PlayerViewModel, state: PlayerUiState) {
    // 播放页可见时每 200ms 刷新进度
    val position by remember { viewModel.position(200) }.collectAsStateWithLifecycle(0L)
    val duration = state.durationMs.coerceAtLeast(1)
    // 松手后保持在目标位置，直到播放位置追上（避免先跳回旧位置再跳过去）
    val seek = rememberSeekState(position, duration)
    val fraction = seek.shown((position.toFloat() / duration).coerceIn(0f, 1f))
    val shown = if (seek.dragging != null || seek.pending != null) (fraction * duration).toLong() else position

    Column(Modifier.fillMaxWidth().padding(horizontal = 24.dp)) {
        Slider(
            value = fraction,
            onValueChange = { seek.dragging = it },
            onValueChangeFinished = { seek.release()?.let { viewModel.seekTo((it * duration).toLong()) } },
            modifier = Modifier.testTag("seek_bar"),
        )
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(formatDuration(shown), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(formatDuration(state.durationMs), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun Controls(viewModel: PlayerViewModel, state: PlayerUiState) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        ToggleIconButton(
            checked = state.shuffle,
            onClick = viewModel::toggleShuffle,
            icon = R.drawable.ic_shuffle,
            contentDescription = stringResource(R.string.action_shuffle_mode),
            modifier = Modifier.testTag("shuffle"),
        )
        IconButton(onClick = viewModel::previous, modifier = Modifier.size(56.dp)) {
            Icon(painterResource(R.drawable.ic_skip_previous), contentDescription = stringResource(R.string.action_previous), modifier = Modifier.size(32.dp))
        }
        FilledIconButton(onClick = viewModel::togglePlayPause, modifier = Modifier.size(72.dp).testTag("play_pause")) {
            Icon(
                painterResource(if (state.isPlaying) R.drawable.ic_pause else R.drawable.ic_play),
                contentDescription = stringResource(if (state.isPlaying) R.string.action_pause else R.string.action_play),
                modifier = Modifier.size(36.dp),
            )
        }
        IconButton(onClick = viewModel::next, modifier = Modifier.size(56.dp)) {
            Icon(painterResource(R.drawable.ic_skip_next), contentDescription = stringResource(R.string.action_next), modifier = Modifier.size(32.dp))
        }
        ToggleIconButton(
            checked = state.repeat != RepeatMode.OFF,
            onClick = viewModel::cycleRepeat,
            icon = if (state.repeat == RepeatMode.ONE) R.drawable.ic_repeat_one else R.drawable.ic_repeat,
            contentDescription = stringResource(R.string.action_repeat),
            modifier = Modifier.testTag("repeat"),
        )
    }
    Spacer(Modifier.height(4.dp))
}

/** 点一下切换歌词 / 封面：不显示点击水波纹，大面积的歌词、封面闪一下不好看。 */
private fun Modifier.tapToToggle(onTap: () -> Unit): Modifier = pointerInput(onTap) { detectTapGestures { onTap() } }
