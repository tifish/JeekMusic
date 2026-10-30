package com.tinyfish.jeekmusic.analysis

/** Rules v6 heuristic over existing v5 windows. Independent of workout preference and volume.
 * Support describes sampled evidence, not calibrated probability. */
data class CalmnessEvidence(val windows: Int, val support: Double, val minimumSegmentSupport: Double, val energeticShare: Double) {
    val tag get() = if (support >= SUPPORT_THRESHOLD && minimumSegmentSupport >= SEGMENT_SUPPORT_THRESHOLD &&
        energeticShare <= ENERGETIC_SHARE_CEILING) Tag.CALM else Tag.NON_CALM

    companion object {
        const val CALM_FLOOR = .02
        const val ENERGY_CEILING = .08
        const val ENERGY_TO_CALM_CEILING = 1.5
        const val SUPPORT_THRESHOLD = .7
        const val SEGMENT_SUPPORT_THRESHOLD = .5
        const val ENERGETIC_THRESHOLD = .15
        const val ENERGETIC_SHARE_CEILING = .2
        fun from(evidence: AnalysisEvidence?): CalmnessEvidence? {
            if (evidence?.complete != true) return null
            val segments = evidence.scoredSegments()
            val frames = segments.flatMap { it.frames }
            if (frames.size < 3 || frames.any { !it.calm.isFinite() || !it.energy.isFinite() || it.calm < 0 || it.energy < 0 } ||
                frames.none { it.calm + it.energy >= .01 }) return null
            fun quiet(f: ModelScores) = f.calm >= CALM_FLOOR && f.energy <= ENERGY_CEILING && f.energy <= f.calm * ENERGY_TO_CALM_CEILING
            return CalmnessEvidence(frames.size, frames.count(::quiet).toDouble() / frames.size,
                segments.minOf { s -> s.frames.count(::quiet).toDouble() / s.frames.size },
                frames.count { it.energy > ENERGETIC_THRESHOLD }.toDouble() / frames.size)
        }
    }
}
