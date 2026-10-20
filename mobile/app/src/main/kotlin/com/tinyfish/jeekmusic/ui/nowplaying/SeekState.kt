package com.tinyfish.jeekmusic.ui.nowplaying

import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import kotlinx.coroutines.delay
import kotlin.math.abs

/**
 * 进度条显示的位置（0–1）：拖动中跟着手指；松手后保持在目标位置，直到播放位置追上来。
 *
 * 播放位置是定时轮询的（迷你播放条每秒一次），松手后如果马上改回显示播放位置，
 * 会先跳回旧位置、下一次轮询时再跳到新位置（需求 T-7）。
 */
@Stable
class SeekState {
    /** 拖动中的位置；没在拖动时为 null。 */
    var dragging by mutableStateOf<Float?>(null)

    /** 已经松手、等待播放位置追上的目标位置。 */
    var pending by mutableStateOf<Float?>(null)
        internal set

    fun shown(actual: Float): Float = dragging ?: pending ?: actual

    /** 松手：返回要跳转到的位置（没在拖动时为 null），在播放位置追上之前继续显示它。 */
    fun release(): Float? {
        val target = dragging ?: return null
        dragging = null
        pending = target
        return target
    }

    fun cancel() {
        dragging = null
    }
}

/**
 * [positionMs] / [durationMs] 是实际的播放位置；与目标相差不到 1.5 秒就认为已经追上，
 * 最多等 3 秒（跳转失败时也会回到实际位置）。
 */
@Composable
fun rememberSeekState(positionMs: Long, durationMs: Long): SeekState {
    val state = remember { SeekState() }
    val pending = state.pending
    LaunchedEffect(positionMs, pending, durationMs) {
        if (pending != null && durationMs > 0 && abs(positionMs - pending * durationMs) < 1_500) state.pending = null
    }
    LaunchedEffect(pending) {
        if (pending != null) {
            delay(3_000)
            state.pending = null
        }
    }
    return state
}
