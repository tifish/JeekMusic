package com.tinyfish.jeekmusic.analysis

/** Same conservative v8 conjunction as Windows. No title/artist, override or calmness inputs. */
object WorkoutEvidence {
    fun strongRun(segment: SegmentEvidence, sensitivity: Sensitivity): Int {
        var current = 0
        var best = 0
        for (f in segment.frames) {
            current = if (TagClassifier.sportScore(f) >= sensitivity.sport) current+1 else 0
            best = maxOf(best,current)
        }
        return best
    }
    fun needsPulse(evidence: AnalysisEvidence) = evidence.complete &&
        (evidence.aggregate()?.sport ?: Double.POSITIVE_INFINITY) < 3.75 &&
        evidence.scoredSegments().count { strongRun(it,Sensitivity.LOOSE) >= 3 } >= 2

    data class Context(val segment: SegmentEvidence, val startMs: Long, val endMs: Long)
    fun plan(evidence: AnalysisEvidence, logicalStartMs: Long, durationMs: Long): List<Context> {
        val result = mutableListOf<Context>()
        if (!needsPulse(evidence)) return result
        val length = minOf(30000L,durationMs)
        if (length < 4000) return result
        for (s in evidence.scoredSegments().filter { strongRun(it,Sensitivity.LOOSE) >= 3 }) {
            val start = ((s.startMs+s.endMs-length)/2).coerceIn(logicalStartMs,logicalStartMs+durationMs-length)
            val end = start+length
            if (result.any { start < it.endMs && end > it.startMs }) continue
            result += Context(s,start,end)
            if (result.size == 3) break
        }
        return if (result.size >= 2) result else emptyList()
    }
    fun supportedSegments(evidence: AnalysisEvidence,sensitivity: Sensitivity): Int {
        var count = 0
        var end = Long.MIN_VALUE
        for (s in evidence.scoredSegments().filter { strongRun(it,sensitivity) >= 3 && it.pulse?.supportsWorkout == true }
            .sortedBy { it.pulse!!.endMs }) {
            if (s.pulse!!.startMs < end) continue
            count++; end = s.pulse.endMs
        }
        return count
    }
    fun classify(evidence: AnalysisEvidence,sensitivity: Sensitivity): Tag? {
        val scores = evidence.aggregate() ?: return null
        if (scores.sport >= sensitivity.sport) return Tag.SPORT
        if (evidence.contract < 6) return Tag.NON_SPORT
        if (supportedSegments(evidence,sensitivity) >= 2) return Tag.SPORT
        if (evidence.rhythmState == "failed") return null
        return Tag.NON_SPORT
    }
}
