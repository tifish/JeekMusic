package com.tinyfish.jeekmusic.ui.nowplaying

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.ArtistEntity
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.playback.PlayerUiState
import com.tinyfish.jeekmusic.playback.RepeatMode
import com.tinyfish.jeekmusic.ui.components.Artwork
import com.tinyfish.jeekmusic.ui.components.LocalTrackMenuById
import com.tinyfish.jeekmusic.ui.components.LyricsSearchDialog
import com.tinyfish.jeekmusic.ui.components.ToggleIconButton
import com.tinyfish.jeekmusic.ui.components.formatDuration
import com.tinyfish.jeekmusic.ui.navigation.LocalNavigator
import kotlinx.coroutines.launch

/**
 * 播放页（需求 NP-1 ~ NP-3）：顶部支持下拉收起与封面/歌词切换，中间显示歌词或大封面，
 * 底部是精致进度条、播放控制与队列 / 睡眠定时入口。
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
    val lyricsState by viewModel.lyrics.collectAsStateWithLifecycle()
    val loaded = lyricsState as? LyricsState.Loaded
    val lyricsPosition = remember { viewModel.position(200) }
    val online by viewModel.onlineLyricsEnabled.collectAsStateWithLifecycle()
    var manualLyrics by remember(nowPlaying?.trackId) { mutableStateOf<Boolean?>(null) }
    val showLyrics = manualLyrics ?: (loaded != null)

    val backdrop = Brush.verticalGradient(
        colors = listOf(
            MaterialTheme.colorScheme.surfaceContainerHigh,
            MaterialTheme.colorScheme.surface,
            MaterialTheme.colorScheme.surface,
        ),
    )

    var dragAccumulated by remember { mutableFloatStateOf(0f) }

    Column(
        Modifier
            .fillMaxSize()
            .background(backdrop)
            .systemBarsPadding()
            .testTag("now_playing"),
    ) {
        // 顶部拖拽把手 + 收起按钮 + 封面/歌词快捷切换
        Column(
            Modifier
                .fillMaxWidth()
                .pointerInput(onCollapse) {
                    detectVerticalDragGestures(
                        onDragStart = { dragAccumulated = 0f },
                        onDragEnd = {
                            if (dragAccumulated > 90f) onCollapse()
                            dragAccumulated = 0f
                        },
                        onDragCancel = { dragAccumulated = 0f },
                    ) { _, dragAmount ->
                        if (dragAmount > 0f) dragAccumulated += dragAmount
                    }
                },
        ) {
            Box(Modifier.fillMaxWidth().padding(top = 8.dp, bottom = 2.dp), contentAlignment = Alignment.Center) {
                Box(
                    Modifier
                        .width(36.dp)
                        .height(4.dp)
                        .clip(CircleShape)
                        .background(MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.3f)),
                )
            }
            Box(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 2.dp)) {
                IconButton(onClick = onCollapse, modifier = Modifier.align(Alignment.CenterStart)) {
                    Icon(painterResource(R.drawable.ic_expand_more), contentDescription = stringResource(R.string.action_collapse))
                }
                ViewModeSwitch(
                    showLyrics = showLyrics,
                    onSelect = { manualLyrics = it },
                    modifier = Modifier.align(Alignment.Center),
                )
                IconButton(
                    onClick = { nowPlaying?.let { openMenu(it.trackId) } },
                    modifier = Modifier.align(Alignment.CenterEnd).testTag("now_playing_more"),
                ) {
                    Icon(painterResource(R.drawable.ic_more_vert), contentDescription = stringResource(R.string.action_more))
                }
            }
        }

        val searchButton: @Composable () -> Unit = {
            // 开启在线歌词后，没有歌词时提供"搜索歌词"
            if (online && lyricsState == LyricsState.None) {
                TextButton(
                    onClick = { scope.launch { lyricsSearchTrack = viewModel.currentTrack() } },
                    modifier = Modifier.padding(top = 8.dp).testTag("search_lyrics"),
                ) {
                    Text(stringResource(R.string.lyrics_search))
                }
            }
        }

        if (showLyrics) {
            // 歌词模式：顶部紧凑曲目信息 + 充足呼吸感
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Artwork(nowPlaying?.artworkUri, 54.dp, cornerRadius = 10.dp)
                Spacer(Modifier.width(14.dp))
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(
                        nowPlaying?.title.orEmpty(),
                        style = MaterialTheme.typography.titleMedium,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            nowPlaying?.artist ?: stringResource(R.string.unknown_artist),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.primary,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.clickable(onClick = openArtist).testTag("now_playing_artist"),
                        )
                        nowPlaying?.album?.let { album ->
                            Text(" · ", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            Text(
                                album,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier
                                    .weight(1f, fill = false)
                                    .clickable(enabled = nowPlaying.albumKey != null) { nowPlaying.albumKey?.let(navigator::openAlbum) },
                            )
                        }
                    }
                }
            }
            if (loaded != null) {
                LyricsView(
                    lyrics = loaded.lyrics,
                    offsetMs = loaded.offsetMs,
                    position = lyricsPosition,
                    onSeek = viewModel::seekTo,
                    onTap = { manualLyrics = false },
                    modifier = Modifier.weight(1f),
                )
                if (loaded.lyrics.synced) {
                    LyricsOffsetBar(
                        loaded.offsetMs,
                        onChange = { viewModel.setLyricsOffset(loaded.trackKey, it) },
                        modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
                    )
                }
            } else {
                Column(
                    Modifier.weight(1f).fillMaxWidth().tapToToggle { manualLyrics = false }.testTag("no_lyrics"),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    if (lyricsState == LyricsState.None) {
                        Text(stringResource(R.string.lyrics_none), style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    searchButton()
                }
            }
        } else {
            // 封面模式：大圆角封面居中 + 下方清晰曲目信息
            Column(
                Modifier.weight(1f).fillMaxWidth().padding(horizontal = 28.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center,
            ) {
                Artwork(
                    nowPlaying?.artworkUri,
                    size = 320.dp,
                    modifier = Modifier
                        .weight(1f, fill = false)
                        .padding(vertical = 12.dp)
                        .aspectRatio(1f)
                        .shadow(14.dp, RoundedCornerShape(20.dp), clip = false)
                        .tapToToggle { manualLyrics = true }
                        .testTag("now_playing_cover"),
                    cornerRadius = 20.dp,
                )
                Spacer(Modifier.height(16.dp))
                Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(
                        nowPlaying?.title.orEmpty(),
                        style = MaterialTheme.typography.headlineSmall,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        nowPlaying?.artist ?: stringResource(R.string.unknown_artist),
                        style = MaterialTheme.typography.bodyLarge,
                        color = MaterialTheme.colorScheme.primary,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.clickable(onClick = openArtist).testTag("now_playing_artist"),
                    )
                    nowPlaying?.album?.let { album ->
                        Text(
                            album,
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
                searchButton()
            }
        }

        SeekBar(viewModel, state)
        Controls(viewModel, state)
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ToggleIconButton(
                checked = state.sleepTimer.active,
                onClick = { showSleepTimer = true },
                icon = R.drawable.ic_bedtime,
                contentDescription = stringResource(R.string.menu_sleep_timer),
                modifier = Modifier.testTag("sleep_timer"),
            )
            SleepTimerLabel(state.sleepTimer, Modifier.weight(1f).padding(start = 6.dp))
            IconButton(onClick = { showQueue = true }, modifier = Modifier.testTag("open_queue")) {
                Icon(painterResource(R.drawable.ic_queue), contentDescription = stringResource(R.string.action_queue))
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
private fun ViewModeSwitch(showLyrics: Boolean, onSelect: (Boolean) -> Unit, modifier: Modifier = Modifier) {
    Surface(
        modifier = modifier,
        shape = CircleShape,
        color = MaterialTheme.colorScheme.surfaceContainerHighest.copy(alpha = 0.7f),
        border = BorderStroke(0.5.dp, MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.4f)),
    ) {
        Row(Modifier.padding(3.dp), verticalAlignment = Alignment.CenterVertically) {
            ModePill(
                label = stringResource(R.string.now_playing_cover),
                selected = !showLyrics,
                onClick = { onSelect(false) },
            )
            ModePill(
                label = stringResource(R.string.now_playing_lyrics),
                selected = showLyrics,
                onClick = { onSelect(true) },
            )
        }
    }
}

@Composable
private fun ModePill(label: String, selected: Boolean, onClick: () -> Unit) {
    Box(
        Modifier
            .clip(CircleShape)
            .background(if (selected) MaterialTheme.colorScheme.primary else Color.Transparent)
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 5.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Medium,
            color = if (selected) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant,
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
    val dragging = seek.dragging != null
    val shown = if (dragging || seek.pending != null) (fraction * duration).toLong() else position

    val activeColor = MaterialTheme.colorScheme.primary
    val inactiveColor = activeColor.copy(alpha = 0.2f)
    val onDrag = rememberUpdatedState<(Float) -> Unit> { seek.dragging = it }
    val onRelease = rememberUpdatedState { seek.release()?.let { viewModel.seekTo((it * duration).toLong()) } }
    val onCancel = rememberUpdatedState { seek.cancel() }

    Column(Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 4.dp)) {
        Box(
            Modifier
                .fillMaxWidth()
                .height(34.dp)
                .testTag("seek_bar")
                .pointerInput(duration) {
                    val pad = 8.dp.toPx()
                    fun frac(x: Float) = ((x - pad) / (size.width - 2 * pad).coerceAtLeast(1f)).coerceIn(0f, 1f)
                    awaitEachGesture {
                        val down = awaitFirstDown()
                        down.consume()
                        onDrag.value(frac(down.position.x))
                        while (true) {
                            val change = awaitPointerEvent().changes.firstOrNull { it.id == down.id }
                            if (change == null) {
                                onCancel.value()
                                break
                            }
                            if (!change.pressed) {
                                change.consume()
                                onRelease.value()
                                break
                            }
                            change.consume()
                            onDrag.value(frac(change.position.x))
                        }
                    }
                }
                .drawBehind {
                    val pad = 8.dp.toPx()
                    val y = size.height / 2f
                    val trackWidth = (if (dragging) 6.dp else 4.dp).toPx()
                    val x = pad + (size.width - 2 * pad) * fraction
                    drawLine(inactiveColor, Offset(pad, y), Offset(size.width - pad, y), strokeWidth = trackWidth, cap = StrokeCap.Round)
                    drawLine(activeColor, Offset(pad, y), Offset(x, y), strokeWidth = trackWidth, cap = StrokeCap.Round)
                    drawCircle(activeColor, radius = (if (dragging) 9.dp else 6.5.dp).toPx(), center = Offset(x, y))
                },
        )
        Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(
                formatDuration(shown),
                style = MaterialTheme.typography.labelSmall,
                color = if (dragging) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(formatDuration(state.durationMs), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun Controls(viewModel: PlayerViewModel, state: PlayerUiState) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 10.dp),
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
        IconButton(onClick = viewModel::previous, modifier = Modifier.size(54.dp)) {
            Icon(painterResource(R.drawable.ic_skip_previous), contentDescription = stringResource(R.string.action_previous), modifier = Modifier.size(32.dp))
        }
        FilledIconButton(
            onClick = viewModel::togglePlayPause,
            modifier = Modifier
                .size(72.dp)
                .shadow(6.dp, CircleShape, clip = false)
                .testTag("play_pause"),
        ) {
            Icon(
                painterResource(if (state.isPlaying) R.drawable.ic_pause else R.drawable.ic_play),
                contentDescription = stringResource(if (state.isPlaying) R.string.action_pause else R.string.action_play),
                modifier = Modifier.size(36.dp),
            )
        }
        IconButton(onClick = viewModel::next, modifier = Modifier.size(54.dp)) {
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
}

/** 点一下切换歌词 / 封面：不显示点击水波纹，大面积的歌词、封面闪一下不好看。 */
private fun Modifier.tapToToggle(onTap: () -> Unit): Modifier = pointerInput(onTap) { detectTapGestures { onTap() } }
