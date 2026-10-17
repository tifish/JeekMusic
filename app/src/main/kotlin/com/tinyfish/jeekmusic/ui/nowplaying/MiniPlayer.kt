package com.tinyfish.jeekmusic.ui.nowplaying

import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.draw.drawBehind
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.awaitEachGesture
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
 * 调整进度（需求 T-7）：顶部的进度条有圆点标出当前位置，进度条所在的 28dp 高的区域内按下即跳到手指位置、
 * 拖动时圆点跟着手指；在播放条其他地方左右拖动则按距离相对调整。拖动时副标题显示目标时间，松手后才跳转。
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
            SeekStrip(
                progress = progress,
                dragging = dragFraction != null,
                enabled = duration > 0,
                onDrag = { dragFraction = it },
                onRelease = {
                    dragFraction?.let { viewModel.seekTo((it * duration).toLong()) }
                    dragFraction = null
                },
                onCancel = { dragFraction = null },
            )
            Row(
                Modifier.fillMaxWidth().clickable(onClick = onExpand).padding(start = 12.dp, end = 4.dp, bottom = 6.dp),
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

/**
 * 迷你播放条顶部的进度条：圆点标出当前位置；整条 [StripHeight] 高的区域都是热区，
 * 按下立即跳到手指所在的位置（拖动时圆点跟着手指走），松手后由 [onRelease] 真正跳转。
 * 按下和移动事件都在这里消费掉，外层"整条相对拖动"的手势不会同时触发。
 */
@Composable
private fun SeekStrip(
    progress: Float,
    dragging: Boolean,
    enabled: Boolean,
    onDrag: (Float) -> Unit,
    onRelease: () -> Unit,
    onCancel: () -> Unit,
) {
    val active = MaterialTheme.colorScheme.primary
    val inactive = active.copy(alpha = 0.25f)
    val currentOnDrag by rememberUpdatedState(onDrag)
    val currentOnRelease by rememberUpdatedState(onRelease)
    val currentOnCancel by rememberUpdatedState(onCancel)
    Box(
        Modifier
            .fillMaxWidth()
            .height(StripHeight)
            .testTag("mini_seek")
            .pointerInput(enabled) {
                if (!enabled) return@pointerInput
                val inset = StripInset.toPx()
                fun fraction(x: Float) = ((x - inset) / (size.width - 2 * inset)).coerceIn(0f, 1f)
                awaitEachGesture {
                    val down = awaitFirstDown()
                    down.consume()
                    currentOnDrag(fraction(down.position.x))
                    while (true) {
                        val change = awaitPointerEvent().changes.firstOrNull { it.id == down.id }
                        if (change == null) {
                            currentOnCancel()
                            break
                        }
                        if (!change.pressed) {
                            change.consume()
                            currentOnRelease()
                            break
                        }
                        change.consume()
                        currentOnDrag(fraction(change.position.x))
                    }
                }
            }
            .drawBehind {
                val inset = StripInset.toPx()
                val y = size.height / 2
                val track = (if (dragging) 6.dp else 4.dp).toPx()
                val x = inset + (size.width - 2 * inset) * progress
                drawLine(inactive, Offset(inset, y), Offset(size.width - inset, y), strokeWidth = track, cap = StrokeCap.Round)
                drawLine(active, Offset(inset, y), Offset(x, y), strokeWidth = track, cap = StrokeCap.Round)
                // 当前位置：明显的圆点，拖动时放大
                drawCircle(active, radius = (if (dragging) 9.dp else 6.dp).toPx(), center = Offset(x, y))
            },
    )
}

private val StripHeight = 28.dp
private val StripInset = 12.dp
