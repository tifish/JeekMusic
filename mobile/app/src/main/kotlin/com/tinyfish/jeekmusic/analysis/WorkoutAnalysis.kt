package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.db.TrackFeaturesEntity
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlin.coroutines.coroutineContext

/** v6 production pulse stage. A valid v5 base is reused, never represented as fresh model inference. */
object WorkoutAnalysis {
    fun upgradeWithoutAudio(features: TrackFeaturesEntity): TrackFeaturesEntity? {
        if (features.analyzerVersion != 5 || !features.complete()) return null
        val evidence = AnalysisEvidence.parse(features.analysisEvidence) ?: return null
        if (WorkoutEvidence.needsPulse(evidence)) return null
        return features.copy(analyzerVersion=6,analysisEvidence=evidence.copy(contract=6,rhythmContract=1,
            rhythmState="not_needed",baseEvidenceReused=true).toJson())
    }

    suspend fun complete(track: TrackEntity,data: TrackFeaturesEntity,
        decode: (LongRange,() -> Unit) -> FloatArray?,reuseBase: Boolean = false): TrackFeaturesEntity {
        val context = coroutineContext
        val check = { context.ensureActive() }
        val started = System.nanoTime()
        val evidence = AnalysisEvidence.parse(data.analysisEvidence) ?: error("base_evidence")
        val start = track.startMs ?: 0
        val duration = track.endMs?.let { minOf(track.durationMs,it-start) } ?: track.durationMs
        val plan = WorkoutEvidence.plan(evidence,start,duration)
        val segments = evidence.segments.toMutableList()
        var failed = false
        for (w in plan) {
            var pcm: FloatArray? = null
            var pulse: PercussivePulse? = null
            var error: String? = null
            var attempts = 0
            val stage = System.nanoTime()
            for (attempt in 0..2) {
                check(); attempts++
                if (attempt > 0) delay(if (attempt == 1) 200L else 800L)
                try {
                    if (pcm == null) pcm = decode(w.startMs until w.endMs,check) ?: error("decode_empty")
                    if (pcm.size < (w.endMs-w.startMs)*16-1600) { pcm=null; error("truncated_pulse_context") }
                    pulse = PercussivePulseExtractor.fromModelPcm(pcm,check)
                    break
                } catch (e: java.util.concurrent.CancellationException) { throw e }
                catch (e: Exception) { error=e.javaClass.simpleName }
            }
            failed = failed || pulse == null
            val result = (pulse ?: PercussivePulse(emptyList(),emptyList(),error)).copy(
                startMs=w.startMs,endMs=w.endMs,attempts=attempts,elapsedMs=(System.nanoTime()-stage)/1000000)
            segments[evidence.segments.indexOf(w.segment)] = w.segment.copy(pulse=result)
        }
        check()
        val updated = evidence.copy(contract=6,segments=segments,rhythmContract=1,
            rhythmState=if (failed) "failed" else if (plan.isEmpty()) "not_needed" else "complete",baseEvidenceReused=reuseBase,
            elapsedMs=(if (reuseBase) 0 else evidence.elapsedMs)+(System.nanoTime()-started)/1000000)
        return data.copy(analyzerVersion=6,analysisEvidence=updated.toJson(),analysisAttempts=if (failed) 3 else data.analysisAttempts)
    }
}
