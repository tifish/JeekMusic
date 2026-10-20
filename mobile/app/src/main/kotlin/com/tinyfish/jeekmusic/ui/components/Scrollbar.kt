package com.tinyfish.jeekmusic.ui.components

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.ScrollState
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.composed
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.math.ceil

/**
 * 列表的滚动条（需求 U-9）：内容一屏放不下时在右边显示，滚动时更明显。
 * 滑块大小和位置按可见项的平均高度估算（长列表的行高基本一致，足够准）。
 *
 * [draggable] 时按住滑块拖动可以快速定位（上千首歌的文件夹很需要）；只有按在滑块上才接管触摸，
 * 其余位置的点击和滑动照常交给列表。队列、标签管理这类右边有拖动手柄的列表只显示不可拖动。
 */
fun Modifier.verticalScrollbar(state: LazyListState, draggable: Boolean = false): Modifier =
    scrollbar(state, draggable) { LazyListAdapter(state) }

/** 用于 verticalScroll 的 Column；要放在 verticalScroll 之前（按视口而不是内容的坐标绘制）。 */
fun Modifier.verticalScrollbar(state: ScrollState, draggable: Boolean = false): Modifier =
    scrollbar(state, draggable) { ScrollStateAdapter(state) }

/** verticalScroll 加上滚动条。 */
fun Modifier.verticalScrollWithScrollbar(state: ScrollState, draggable: Boolean = false): Modifier =
    verticalScrollbar(state, draggable).verticalScroll(state)

fun Modifier.verticalScrollbar(state: LazyGridState): Modifier = scrollbar(state, draggable = false) { LazyGridAdapter(state) }

/** 滑块位置：[offset] 是已滚动的比例（0～1），[visible] 是一屏占全部内容的比例。 */
private data class Metrics(val offset: Float, val visible: Float)

private interface ScrollbarAdapter {
    /** 内容不超过一屏时返回 null。 */
    fun metrics(): Metrics?
    val isScrolling: Boolean
    suspend fun scrollTo(fraction: Float)
}

private class LazyListAdapter(private val state: LazyListState) : ScrollbarAdapter {
    override val isScrolling get() = state.isScrollInProgress

    /** 平均每项的高度（含间距）和全部内容的估算高度。 */
    private fun estimate(): Pair<Float, Float>? {
        val info = state.layoutInfo
        val items = info.visibleItemsInfo
        if (items.isEmpty() || info.totalItemsCount == 0) return null
        val average = items.sumOf { it.size }.toFloat() / items.size + info.mainAxisItemSpacing
        return average to average * info.totalItemsCount + info.beforeContentPadding + info.afterContentPadding
    }

    override fun metrics(): Metrics? {
        if (!state.canScrollForward && !state.canScrollBackward) return null
        val (average, content) = estimate() ?: return null
        val viewport = state.layoutInfo.viewportSize.height.toFloat()
        val scrolled = state.firstVisibleItemIndex * average + state.firstVisibleItemScrollOffset
        val offset = when {
            // 估算有误差：到头、到底时直接贴边
            !state.canScrollBackward -> 0f
            !state.canScrollForward -> 1f
            else -> (scrolled / (content - viewport).coerceAtLeast(1f)).coerceIn(0f, 1f)
        }
        return Metrics(offset, (viewport / content).coerceIn(0f, 1f))
    }

    override suspend fun scrollTo(fraction: Float) {
        val (average, content) = estimate() ?: return
        val total = state.layoutInfo.totalItemsCount
        val target = fraction.coerceIn(0f, 1f) * (content - state.layoutInfo.viewportSize.height).coerceAtLeast(0f)
        val index = (target / average).toInt().coerceIn(0, total - 1)
        state.scrollToItem(index, (target - index * average).toInt().coerceAtLeast(0))
    }
}

private class ScrollStateAdapter(private val state: ScrollState) : ScrollbarAdapter {
    override val isScrolling get() = state.isScrollInProgress

    override fun metrics(): Metrics? {
        val max = state.maxValue
        if (max <= 0 || max == Int.MAX_VALUE) return null
        val viewport = state.viewportSize.toFloat()
        return Metrics(state.value.toFloat() / max, viewport / (viewport + max))
    }

    override suspend fun scrollTo(fraction: Float) {
        state.scrollTo((fraction.coerceIn(0f, 1f) * state.maxValue).toInt())
    }
}

private class LazyGridAdapter(private val state: LazyGridState) : ScrollbarAdapter {
    override val isScrolling get() = state.isScrollInProgress

    override fun metrics(): Metrics? {
        if (!state.canScrollForward && !state.canScrollBackward) return null
        val info = state.layoutInfo
        val items = info.visibleItemsInfo
        if (items.isEmpty()) return null
        // 按行估算：第一行有几列、平均行高多少
        val columns = items.count { it.row == items.first().row }.coerceAtLeast(1)
        val rows = ceil(info.totalItemsCount / columns.toFloat())
        val rowHeight = items.sumOf { it.size.height }.toFloat() / items.size + info.mainAxisItemSpacing
        val content = rows * rowHeight + info.beforeContentPadding + info.afterContentPadding
        val viewport = info.viewportSize.height.toFloat()
        val scrolled = (state.firstVisibleItemIndex / columns) * rowHeight + state.firstVisibleItemScrollOffset
        val offset = if (!state.canScrollForward) 1f else (scrolled / (content - viewport).coerceAtLeast(1f)).coerceIn(0f, 1f)
        return Metrics(offset, (viewport / content).coerceIn(0f, 1f))
    }

    override suspend fun scrollTo(fraction: Float) = Unit
}

private fun Modifier.scrollbar(key: Any, draggable: Boolean, create: () -> ScrollbarAdapter): Modifier = composed {
    // 按滚动状态记住：每次重组都新建的话，拖动手势会被重启
    val adapter = remember(key) { create() }
    val color = MaterialTheme.colorScheme.onSurfaceVariant
    val activeColor = MaterialTheme.colorScheme.primary
    var dragging by remember { mutableStateOf(false) }
    // 平时淡淡地显示，告诉用户还有内容；滚动、拖动时更明显
    val alpha by animateFloatAsState(if (dragging || adapter.isScrolling) 0.9f else 0.5f, label = "scrollbar")
    val scope = rememberCoroutineScope()

    this
        .drawWithContent {
            drawContent()
            val m = adapter.metrics() ?: return@drawWithContent
            val thumb = thumbBounds(m, size.height, MinThumb.toPx(), TrackPadding.toPx())
            val width = (if (dragging) DraggingWidth else ThumbWidth).toPx()
            drawRoundRect(
                color = if (dragging) activeColor else color,
                alpha = if (dragging) 1f else alpha,
                topLeft = Offset(size.width - EdgeMargin.toPx() - width, thumb.first),
                size = Size(width, thumb.second),
                cornerRadius = CornerRadius(width / 2),
            )
        }
        .then(
            if (!draggable) {
                Modifier
            } else {
                Modifier.pointerInput(adapter) {
                    awaitEachGesture {
                        // 在 Initial 阶段先看是不是按在滑块上：是的话由这里处理，列表不会同时滚动
                        val down = awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
                        val m = adapter.metrics() ?: return@awaitEachGesture
                        val (top, height) = thumbBounds(m, size.height.toFloat(), MinThumb.toPx(), TrackPadding.toPx())
                        val slop = TouchSlop.toPx()
                        val onThumb = down.position.x >= size.width - TouchWidth.toPx() &&
                            down.position.y in (top - slop)..(top + height + slop)
                        if (!onThumb) return@awaitEachGesture
                        down.consume()
                        dragging = true
                        val grab = down.position.y - top
                        var job: Job? = null
                        try {
                            while (true) {
                                val change = awaitPointerEvent(PointerEventPass.Initial).changes.firstOrNull { it.id == down.id } ?: break
                                change.consume()
                                if (!change.pressed) break
                                val current = adapter.metrics() ?: break
                                val thumbHeight = thumbBounds(current, size.height.toFloat(), MinThumb.toPx(), TrackPadding.toPx()).second
                                val track = size.height - 2 * TrackPadding.toPx() - thumbHeight
                                val fraction = ((change.position.y - grab - TrackPadding.toPx()) / track.coerceAtLeast(1f))
                                job?.cancel()
                                job = scope.launch { adapter.scrollTo(fraction) }
                            }
                        } finally {
                            dragging = false
                        }
                    }
                }
            },
        )
}

/** 滑块的顶端和高度（像素）。 */
private fun thumbBounds(m: Metrics, height: Float, minThumb: Float, padding: Float): Pair<Float, Float> {
    val track = height - 2 * padding
    val thumb = (track * m.visible).coerceIn(minOf(minThumb, track), track)
    return padding + (track - thumb) * m.offset to thumb
}

private val ThumbWidth = 4.dp
private val DraggingWidth = 8.dp
private val EdgeMargin = 2.dp
private val TrackPadding = 4.dp
private val MinThumb = 40.dp

/** 滑块的触摸范围：右边这么宽、滑块上下再各放宽一些。 */
private val TouchWidth = 32.dp
private val TouchSlop = 12.dp
