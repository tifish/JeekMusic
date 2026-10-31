package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.db.TrackFeaturesEntity
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlin.coroutines.coroutineContext

object TrackAnalyzer {
    suspend fun analyze(track: TrackEntity, decode: (LongRange, () -> Unit) -> FloatArray?,
        infer: (FloatArray, () -> Unit) -> Pair<ModelScores?, List<ModelScores>>): TrackFeaturesEntity {
        val started = System.nanoTime()
        val context = coroutineContext
        val checkCancelled = { context.ensureActive() }
        val segments = mutableListOf<SegmentEvidence>()
        var longest = floatArrayOf()
        suspend fun sample(w: LongRange, additional: Boolean) {
            var pcm: FloatArray? = null
            var error: String? = null
            var decodes = 0
            var models = 0
            var mean: ModelScores? = null
            var frames = emptyList<ModelScores>()
            for (attempt in 0..2) {
                checkCancelled()
                if (attempt > 0) delay(if (attempt == 1) 200L else 800L)
                try {
                    if (pcm == null) { decodes++; pcm = decode(w, checkCancelled) ?: error("decode_empty") }
                    if (pcm.size < (w.last + 1 - w.first) * 16 - 1600) {
                        pcm = null; error("truncated_window")
                    }
                    val input = pcm
                    if (input.size < 15600) { error = "decode:insufficient_samples"; break }
                    if (input.size > longest.size) longest = input
                    models++
                    val result = infer(input, checkCancelled)
                    mean = result.first
                    frames = result.second
                    check(mean != null && frames.isNotEmpty()) { "empty_model_output" }
                    error = null
                    break
                } catch (e: java.util.concurrent.CancellationException) { throw e }
                catch (e: Exception) { error = (if (pcm == null) "decode:" else "model:") + e.javaClass.simpleName }
            }
            segments += SegmentEvidence(w.first, w.last + 1, additional, pcm?.size ?: 0, decodes, models, mean, frames, error)
        }
        val start = track.startMs ?: 0
        val duration = track.endMs?.let { minOf(track.durationMs, it - start) } ?: track.durationMs
        val initial = PcmContract.windows(start, duration)
        for (w in initial) sample(w, false)
        val reasons = AnalysisEvidence(segments.toList(), emptyList(), false, null, 0).reasons()
        if (reasons.isNotEmpty()) for (w in PcmContract.additional(start, duration, initial)) sample(w, true)
        var f: AudioFeatures? = null
        var featureError: String? = null
        for (attempt in 0..2) {
            checkCancelled()
            if (longest.isEmpty()) break
            if (attempt > 0) delay(if (attempt == 1) 200L else 800L)
            try { f = FeatureExtractor.extract(PcmContract.resample(longest, 16000, FeatureExtractor.SAMPLE_RATE)); break }
            catch (e: java.util.concurrent.CancellationException) { throw e }
            catch (e: Exception) { featureError = "features:" + e.javaClass.simpleName }
        }
        val good = AnalysisEvidence(segments, emptyList(), false, null, 0).scoredSegments()
        val count = good.sumOf { it.frames.size }
        fun mean(select: (ModelScores) -> Double) = good.sumOf { select(it.mean!!) * it.frames.size } / count
        val m = if (count == 0) null else ModelScores(mean { it.vocal }, mean { it.classical }, mean { it.calm }, mean { it.energy }, mean { it.orchestra })
        val finalReasons = if (segments.any { it.additional && !it.complete }) reasons + "additional_failure_first_pass_retained" else reasons
        val evidence = AnalysisEvidence(segments, finalReasons, f != null, if (f == null) featureError ?: "features:no_pcm" else null,
            (System.nanoTime() - started) / 1_000_000)
        val base = TrackFeaturesEntity(track.trackKey, "${track.dateModified}:${track.size}", 5,
            f?.bpm, f?.bpmConfidence ?: 0.0, f?.loudness ?: -70.0, f?.loudnessRange ?: 0.0, f?.onsetRate ?: 0.0,
            f?.centroidHz ?: 0.0, f?.lowRatio ?: 0.0, System.currentTimeMillis(), !evidence.complete,
            m?.vocal, m?.classical, m?.calm, m?.energy, m?.orchestra, evidence.toJson(), if (evidence.complete) 1 else 3)
        return WorkoutAnalysis.complete(track,base,decode)
    }
}
