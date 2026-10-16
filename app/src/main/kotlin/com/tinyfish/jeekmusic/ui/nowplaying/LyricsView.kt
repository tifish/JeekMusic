package com.tinyfish.jeekmusic.ui.nowplaying

import android.os.SystemClock
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
import androidx.compose.runtime.mutableLongStateOf
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
 * 歌词（需求 LY-3 ~ LY-5，docs/design.md §8.6）：
 * - 当前行通过 derivedStateOf 计算，只有行号变化时才重组，200ms 的进度轮询不会让整个列表重组
 * - 自动滚动让当前行居中；用户拖动后 3 秒内暂停跟随；点击某一行跳到对应时间
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
    modifier: Modifier = Modifier,
) {
    if (!lyrics.synced) {
        PlainLyrics(lyrics, modifier)
        return
    }
    val positionMs by position.collectAsStateWithLifecycle(0L)
    val current by remember(lyrics, offsetMs) {
        derivedStateOf { LrcParser.currentIndex(lyrics.lines, positionMs + offsetMs) }
    }
    val listState = rememberLazyListState()
    val dragged by listState.interactionSource.collectIsDraggedAsState()
    var lastUserScroll by remember { mutableLongStateOf(0L) }
    LaunchedEffect(dragged) { if (dragged) lastUserScroll = SystemClock.elapsedRealtime() }

    BoxWithConstraints(modifier.fillMaxSize().testTag("lyrics")) {
        val viewport = with(LocalDensity.current) { maxHeight.roundToPx() }
        LaunchedEffect(current) {
            val idle = SystemClock.elapsedRealtime() - lastUserScroll > FOLLOW_PAUSE_MS
            if (current >= 0 && idle && !dragged) listState.animateScrollToItem(current, -viewport / 3)
        }
        LazyColumn(
            state = listState,
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(vertical = maxHeight / 3),
        ) {
            itemsIndexed(lyrics.lines, key = { i, _ -> i }) { index, line ->
                val active = index == current
                Column(
                    Modifier
                        .fillMaxWidth()
                        .clickable { onSeek((line.timeMs - offsetMs).coerceAtLeast(0)) }
                        .padding(horizontal = 24.dp, vertical = 8.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Text(
                        line.text.ifEmpty { "♪" },
                        textAlign = TextAlign.Center,
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = if (active) FontWeight.Bold else FontWeight.Normal,
                        color = if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    line.translation?.let {
                        Text(
                            it,
                            textAlign = TextAlign.Center,
                            style = MaterialTheme.typography.bodyMedium,
                            color = if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun PlainLyrics(lyrics: Lyrics, modifier: Modifier) {
    LazyColumn(modifier.fillMaxSize().testTag("lyrics"), contentPadding = PaddingValues(24.dp)) {
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
