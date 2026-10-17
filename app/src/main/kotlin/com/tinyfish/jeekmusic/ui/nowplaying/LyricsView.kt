package com.tinyfish.jeekmusic.ui.nowplaying

import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.gestures.detectTapGestures
import com.tinyfish.jeekmusic.ui.components.verticalScrollbar
import kotlin.math.abs
import kotlinx.coroutines.delay
import com.tinyfish.jeekmusic.ui.components.formatDuration
import com.tinyfish.jeekmusic.R
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.res.painterResource
import androidx.compose.runtime.mutableStateOf
import androidx.compose.material3.IconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.HorizontalDivider
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.gestures.animateScrollBy
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.collectIsDraggedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.data.lyrics.LrcParser
import com.tinyfish.jeekmusic.data.lyrics.Lyrics
import kotlinx.coroutines.flow.Flow

/**
 * 歌词（需求 LY-3 ~ LY-5、LY-12，docs/design.md §8.6）：
 * - 当前行通过 derivedStateOf 计算，只有行号变化时才重组，200ms 的进度轮询不会让整个列表重组
 * - 自动滚动让当前行居中；点一下歌词（[onTap]）切换到封面
 * - 拖动歌词时暂停跟随，中间出现一条定位线，显示线上那一行的时间和播放按钮，点播放按钮跳到那里；
 *   松手后 3 秒没有操作就收起定位线、回到当前行。只拖不点不会跳转，避免误操作
 * - 当前行只改颜色和字重，不做缩放或模糊动画（省电）
 *
 * [offsetMs] 为正时歌词提前显示。
 */
@Composable
fun LyricsView(
    lyrics: Lyrics,
    offsetMs: Long,
    position: Flow<Long>,
    onSeek: (Long) -> Unit,
    onTap: () -> Unit,
    modifier: Modifier = Modifier,
) {
    if (!lyrics.synced) {
        PlainLyrics(lyrics, onTap, modifier)
        return
    }
    val positionMs by position.collectAsStateWithLifecycle(0L)
    val current by remember(lyrics, offsetMs) {
        derivedStateOf { LrcParser.currentIndex(lyrics.lines, positionMs + offsetMs) }
    }
    val listState = rememberLazyListState()
    val dragged by listState.interactionSource.collectIsDraggedAsState()
    // 用户在拖动浏览歌词：暂停跟随，显示定位线
    var browsing by remember { mutableStateOf(false) }
    LaunchedEffect(dragged, listState.isScrollInProgress) {
        if (dragged) {
            browsing = true
        } else if (browsing && !listState.isScrollInProgress) {
            delay(FOLLOW_PAUSE_MS)
            browsing = false
        }
    }
    // 点了播放按钮后要跟随的行：播放位置是轮询的，跳转后当前行要过一会儿才更新，这期间先停在这一行，避免先滚回旧位置
    var seekTarget by remember { mutableStateOf<Int?>(null) }
    LaunchedEffect(seekTarget) {
        if (seekTarget != null) {
            delay(SEEK_HOLD_MS)
            seekTarget = null
        }
    }

    // 点按交给外层：行上没有点击处理，滚动只消费拖动，定位线的播放按钮自己消费点击
    BoxWithConstraints(modifier.fillMaxSize().pointerInput(onTap) { detectTapGestures { onTap() } }.testTag("lyrics")) {
        val viewport = with(LocalDensity.current) { maxHeight.roundToPx() }
        // 定位线和跟随的位置：列表的垂直中间
        val anchor = viewport / 2
        // 定位线上的那一行（中心离定位线最近的）；只在浏览时计算
        val selected by remember(listState, anchor) {
            derivedStateOf {
                if (!browsing) return@derivedStateOf -1
                val info = listState.layoutInfo
                info.visibleItemsInfo.minByOrNull { abs(it.offset - info.viewportStartOffset + it.size / 2 - anchor) }?.index ?: -1
            }
        }
        LaunchedEffect(current, browsing, seekTarget) {
            if (browsing) return@LaunchedEffect
            val target = seekTarget
            when {
                target != null && current != target -> listState.centerOn(target, anchor)
                current >= 0 -> {
                    seekTarget = null
                    listState.centerOn(current, anchor)
                }
            }
        }
        LazyColumn(
            state = listState,
            modifier = Modifier.fillMaxSize(),
            // 上下各留半屏，第一行和最后一行也能滚到中间
            contentPadding = PaddingValues(vertical = maxHeight / 2),
        ) {
            itemsIndexed(lyrics.lines, key = { i, _ -> i }) { index, line ->
                val active = index == current
                val color = when {
                    active -> MaterialTheme.colorScheme.primary
                    index == selected -> MaterialTheme.colorScheme.onSurface
                    else -> MaterialTheme.colorScheme.onSurfaceVariant
                }
                Column(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 24.dp, vertical = 8.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Text(
                        line.text.ifEmpty { "♪" },
                        textAlign = TextAlign.Center,
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = if (active) FontWeight.Bold else FontWeight.Normal,
                        color = color,
                    )
                    line.translation?.let {
                        Text(it, textAlign = TextAlign.Center, style = MaterialTheme.typography.bodyMedium, color = color)
                    }
                }
            }
        }
        lyrics.lines.getOrNull(selected)?.let { line ->
            val seekTo = (line.timeMs - offsetMs).coerceAtLeast(0)
            SeekGuide(
                timeMs = seekTo,
                onPlay = {
                    onSeek(seekTo)
                    seekTarget = selected
                    browsing = false
                },
                modifier = Modifier.align(Alignment.CenterStart),
            )
        }
    }
}

/** 拖动歌词时中间的定位线：左边是线上那一行的时间，右边是播放按钮。 */
@Composable
private fun SeekGuide(timeMs: Long, onPlay: () -> Unit, modifier: Modifier = Modifier) {
    Row(modifier.fillMaxWidth().padding(start = 8.dp).testTag("lyrics_seek_guide"), verticalAlignment = Alignment.CenterVertically) {
        Text(formatDuration(timeMs), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
        HorizontalDivider(Modifier.weight(1f).padding(horizontal = 8.dp), color = MaterialTheme.colorScheme.primary.copy(alpha = 0.5f))
        IconButton(onClick = onPlay, modifier = Modifier.testTag("lyrics_seek_play")) {
            Icon(painterResource(R.drawable.ic_play), contentDescription = stringResource(R.string.lyrics_seek_here), tint = MaterialTheme.colorScheme.primary)
        }
    }
}

/** 把第 [index] 行的中心滚到 [anchor]（相对列表顶部的像素）。不在屏幕上时先跳过去再微调。 */
private suspend fun LazyListState.centerOn(index: Int, anchor: Int) {
    fun find() = layoutInfo.visibleItemsInfo.firstOrNull { it.index == index }
    val info = find() ?: run {
        scrollToItem(index)
        find()
    } ?: return
    val center = info.offset - layoutInfo.viewportStartOffset + info.size / 2
    animateScrollBy((center - anchor).toFloat())
}

@Composable
private fun PlainLyrics(lyrics: Lyrics, onTap: () -> Unit, modifier: Modifier) {
    val listState = rememberLazyListState()
    LazyColumn(modifier.fillMaxSize().pointerInput(onTap) { detectTapGestures { onTap() } }.verticalScrollbar(listState).testTag("lyrics"), state = listState, contentPadding = PaddingValues(24.dp)) {
        itemsIndexed(lyrics.lines) { _, line ->
            Text(
                line.text,
                Modifier.fillMaxWidth().padding(vertical = 4.dp),
                textAlign = TextAlign.Center,
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** 歌词时间偏移调整（需求 LY-6）：±0.5 秒，按首保存。 */
@Composable
fun LyricsOffsetBar(offsetMs: Long, onChange: (Long) -> Unit, modifier: Modifier = Modifier) {
    Row(modifier, horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
        TextButton(onClick = { onChange(offsetMs - OFFSET_STEP_MS) }, modifier = Modifier.testTag("lyrics_later")) { Text("−0.5s") }
        TextButton(onClick = { onChange(0) }, enabled = offsetMs != 0L) {
            Text("%+.1fs".format(offsetMs / 1000.0), style = MaterialTheme.typography.labelMedium)
        }
        TextButton(onClick = { onChange(offsetMs + OFFSET_STEP_MS) }, modifier = Modifier.testTag("lyrics_earlier")) { Text("+0.5s") }
    }
}

private const val FOLLOW_PAUSE_MS = 3_000L
private const val OFFSET_STEP_MS = 500L
private const val SEEK_HOLD_MS = 2_000L
