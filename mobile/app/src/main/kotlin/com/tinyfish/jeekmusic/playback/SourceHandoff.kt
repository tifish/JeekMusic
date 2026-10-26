package com.tinyfish.jeekmusic.playback

import kotlin.random.Random

/** Indices, rather than IDs, preserve repeated playlist entries and their order. */
internal object SourceHandoff {
    data class Plan(val before: List<Int>, val after: List<Int>, val anchor: Boolean)

    fun plan(ids: List<Long>, current: Long, manual: Boolean, shuffle: Boolean, random: Random = Random.Default): Plan {
        val here = if (manual) -1 else ids.indexOf(current)
        if (shuffle) return Plan(emptyList(), ids.indices.filter { it != here }.shuffled(random), here < 0)
        return if (here >= 0) Plan((0 until here).toList(), (here + 1 until ids.size).toList(), false)
        else Plan(emptyList(), ids.indices.toList(), true)
    }
}
