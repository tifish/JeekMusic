package com.tinyfish.jeekmusic.ui.nowplaying

import androidx.compose.foundation.clickable
import com.tinyfish.jeekmusic.ui.components.formatDuration
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.runtime.setValue
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.ui.components.Artwork

/**
 * 底部常驻迷你播放条（需求 T-7）：封面、标题、艺术家、上一首 / 播放暂停 / 下一首，点击展开播放页。
 * 左右拖动播放条可以调整进度：拖动时进度条加粗、副标题显示目标时间，松手后才跳转。
 */
@Composable
fun MiniPlayer(viewModel: PlayerViewModel, onExpand: () -> Unit) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val nowPlaying = state.nowPlaying ?: return
    // 细进度条每秒刷新一次；App 在后台时停止收集（docs/design.md §7.7）
    val position by remember { viewModel.position(1_000) }.collectAsStateWithLifecycle(0L)
    // 拖动中的目标进度（0–1）；没在拖动时为 null
    var dragFraction by remember { mutableStateOf<Float?>(null) }
    val duration = state.durationMs
    val progress = dragFraction ?: if (duration > 0) (position.toFloat() / duration).coerceIn(0f, 1f) else 0f

    val currentProgress by rememberUpdatedState(progress)

    Surface(tonalElevation = 3.dp, modifier = Modifier.testTag("mini_player")) {
        // 整个播放条都能左右拖动，按拖动距离相对调整进度（拖满一个屏宽 = 整首歌）；点击仍然打开播放页
        Column(
            Modifier
                .navigationBarsPadding()
                .pointerInput(duration) {
                    if (duration <= 0) return@pointerInput
                    detectHorizontalDragGestures(
                        onDragStart = { dragFraction = currentProgress },
                        onDragEnd = {
                            dragFraction?.let { viewModel.seekTo((it * duration).toLong()) }
                            dragFraction = null
                        },
                        onDragCancel = { dragFraction = null },
                    ) { change, dragAmount ->
                        change.consume()
                        dragFraction = ((dragFraction ?: currentProgress) + dragAmount / size.width).coerceIn(0f, 1f)
                    }
                },
        ) {
            // 固定高度的容器：拖动时进度条加粗，不推动下面的内容
            Box(Modifier.fillMaxWidth().height(6.dp).testTag("mini_seek")) {
                LinearProgressIndicator(
                    progress = { progress },
                    modifier = Modifier.fillMaxWidth().height(if (dragFraction != null) 6.dp else 2.dp),
                    drawStopIndicator = {},
                    gapSize = 0.dp,
                )
            }
            Row(
                Modifier.fillMaxWidth().clickable(onClick = onExpand).padding(start = 12.dp, end = 4.dp, top = 2.dp, bottom = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Artwork(nowPlaying.artworkUri, 44.dp)
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(nowPlaying.title, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
                    Text(
                        dragFraction?.let { "${formatDuration((it * duration).toLong())} / ${formatDuration(duration)}" }
                            ?: nowPlaying.artist ?: stringResource(R.string.unknown_artist),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                IconButton(onClick = viewModel::previous) {
                    Icon(painterResource(R.drawable.ic_skip_previous), contentDescription = stringResource(R.string.action_previous))
                }
                IconButton(onClick = viewModel::togglePlayPause, modifier = Modifier.testTag("mini_play_pause")) {
                    Icon(
                        painterResource(if (state.isPlaying) R.drawable.ic_pause else R.drawable.ic_play),
                        contentDescription = stringResource(if (state.isPlaying) R.string.action_pause else R.string.action_play),
                    )
                }
                IconButton(onClick = viewModel::next) {
                    Icon(painterResource(R.drawable.ic_skip_next), contentDescription = stringResource(R.string.action_next))
                }
            }
        }
    }
}

private val SeekTouchHeight = 8.dp
