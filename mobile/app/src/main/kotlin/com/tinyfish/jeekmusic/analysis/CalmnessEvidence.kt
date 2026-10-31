package com.tinyfish.jeekmusic.analysis

import org.json.JSONArray
import org.json.JSONObject

/** Rules v7 heuristic over existing v5 windows. Independent of workout preference and volume.
 * Support describes sampled evidence, not calibrated probability. */
data class CalmnessEvidence(val windows: Int, val support: Double, val minimumSegmentSupport: Double, val energeticShare: Double,
    val strictSupport: Double = 0.0, val relativeSupport: Double = 0.0, val segments: List<CalmnessSegmentEvidence> = emptyList()) {
    val tag get() = if (support >= SUPPORT_THRESHOLD && minimumSegmentSupport >= SEGMENT_SUPPORT_THRESHOLD &&
        energeticShare <= ENERGETIC_SHARE_CEILING) Tag.CALM else Tag.NON_CALM

    /** Same diagnostic field names as Windows review/MCP; supports overlap, never sum them. */
    fun toJson(): JSONObject = JSONObject().put("heuristicNotCalibrated", true)
        .put("supportDefinition", "strict_or_relative_per_window").put("windows", windows)
        .put("support", support).put("minimumSegmentSupport", minimumSegmentSupport).put("energeticShare", energeticShare)
        .put("strictSupport", strictSupport).put("relativeSupport", relativeSupport)
        .put("segments", JSONArray(segments.map { s -> JSONObject().put("startMs", s.startMs).put("endMs", s.endMs)
            .put("windows", s.windows).put("support", s.support).put("strictSupport", s.strictSupport).put("relativeSupport", s.relativeSupport) }))
        .put("thresholds", JSONObject().put("calmFloor", CALM_FLOOR).put("energyCeiling", ENERGY_CEILING)
            .put("energyToCalmCeiling", ENERGY_TO_CALM_CEILING).put("relativeCalmFloor", RELATIVE_CALM_FLOOR)
            .put("relativeEnergyCeiling", RELATIVE_ENERGY_CEILING).put("relativeEnergyToCalmCeiling", RELATIVE_ENERGY_TO_CALM_CEILING)
            .put("supportAtLeast", SUPPORT_THRESHOLD).put("eachSegmentSupportAtLeast", SEGMENT_SUPPORT_THRESHOLD)
            .put("energeticAbove", ENERGETIC_THRESHOLD).put("energeticShareAtMost", ENERGETIC_SHARE_CEILING))

    companion object {
        const val CALM_FLOOR = .02
        const val ENERGY_CEILING = .08
        const val ENERGY_TO_CALM_CEILING = 1.5
        const val SUPPORT_THRESHOLD = .7
        const val SEGMENT_SUPPORT_THRESHOLD = .5
        const val ENERGETIC_THRESHOLD = .15
        const val ENERGETIC_SHARE_CEILING = .2
        // Lower proxy scores require stronger relative dominance AND lower energy.
        // Tiny/zero calm never supplies positive evidence merely because energy is zero.
        const val RELATIVE_CALM_FLOOR = .01
        const val RELATIVE_ENERGY_CEILING = .02
        const val RELATIVE_ENERGY_TO_CALM_CEILING = .5
        fun from(evidence: AnalysisEvidence?): CalmnessEvidence? {
            if (evidence?.complete != true) return null
            val segments = evidence.scoredSegments()
            val frames = segments.flatMap { it.frames }
            if (frames.size < 3 || frames.any { !it.calm.isFinite() || !it.energy.isFinite() || it.calm < 0 || it.energy < 0 } ||
                frames.none { it.calm + it.energy >= .01 }) return null
            fun strict(f: ModelScores) = f.calm >= CALM_FLOOR && f.energy <= ENERGY_CEILING && f.energy <= f.calm * ENERGY_TO_CALM_CEILING
            fun relative(f: ModelScores) = f.calm >= RELATIVE_CALM_FLOOR && f.energy <= RELATIVE_ENERGY_CEILING && f.energy <= f.calm * RELATIVE_ENERGY_TO_CALM_CEILING
            fun quiet(f: ModelScores) = strict(f) || relative(f)
            val details = segments.map { s -> CalmnessSegmentEvidence(s.startMs, s.endMs, s.frames.size,
                s.frames.count(::quiet).toDouble() / s.frames.size, s.frames.count(::strict).toDouble() / s.frames.size,
                s.frames.count(::relative).toDouble() / s.frames.size) }
            return CalmnessEvidence(frames.size, frames.count(::quiet).toDouble() / frames.size,
                details.minOf { it.support }, frames.count { it.energy > ENERGETIC_THRESHOLD }.toDouble() / frames.size,
                frames.count(::strict).toDouble() / frames.size, frames.count(::relative).toDouble() / frames.size, details)
        }
    }
}

data class CalmnessSegmentEvidence(val startMs: Long, val endMs: Long, val windows: Int,
    val support: Double, val strictSupport: Double, val relativeSupport: Double)
