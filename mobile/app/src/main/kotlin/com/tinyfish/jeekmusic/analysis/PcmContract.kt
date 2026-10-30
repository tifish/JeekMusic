package com.tinyfish.jeekmusic.analysis

/** v5 half-open source intervals and deterministic resampling, shared with desktop PcmMath. */
object PcmContract {
    fun windows(start: Long, duration: Long): List<LongRange> {
        if (duration <= 0) return emptyList()
        if (duration < 40_000) return listOf(start until start + duration)
        val result = mutableListOf<LongRange>()
        for (p in listOf(.3, .5, .7)) {
            var from = minOf(start + (duration * p).toLong(), start + duration - 10_000)
            result.lastOrNull()?.let { from = maxOf(from, it.last + 1) }
            if (from + 10_000 <= start + duration) result += from until from + 10_000
        }
        return result
    }
    fun additional(start: Long, duration: Long, used: List<LongRange>): List<LongRange> {
        if (duration < 40_000) return emptyList()
        val result = mutableListOf<LongRange>()
        for (p in listOf(.1, .85, 0.0, .9)) {
            val from = minOf(start + (duration * p).toLong(), start + duration - 10_000)
            val end = from + 10_000
            if ((used + result).any { from <= it.last && end > it.first }) continue
            result += from until end
            if (result.size == 2) break
        }
        return result
    }
    fun resample(input: FloatArray, source: Int, target: Int): FloatArray {
        require(source > 0 && target > 0)
        if (source == target) return input
        val ratio = source.toDouble() / target
        return FloatArray((input.size / ratio).toInt()) { i ->
            val position = i * ratio
            val from = position.toInt()
            if (source < target) {
                (input[from] + (input[minOf(from + 1, input.lastIndex)] - input[from]) * (position - from)).toFloat()
            } else {
                val to = minOf(input.size, ((i + 1) * ratio).toInt().coerceAtLeast(from + 1))
                var sum = 0f
                for (j in from until to) sum += input[j]
                sum / (to - from)
            }
        }
    }
    fun sample(value: Float) = if (value.isFinite()) value.coerceIn(-1f, 1f) else 0f
}
