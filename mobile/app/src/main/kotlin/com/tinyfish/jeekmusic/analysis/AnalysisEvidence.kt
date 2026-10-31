package com.tinyfish.jeekmusic.analysis

import org.json.JSONArray
import org.json.JSONObject
import kotlin.math.abs

data class SegmentEvidence(val startMs: Long, val endMs: Long, val additional: Boolean, val sampleCount: Int,
    val decodeAttempts: Int, val modelAttempts: Int, val mean: ModelScores?, val frames: List<ModelScores>, val error: String?,
    val pulse: PercussivePulse? = null) {
    val complete get() = mean != null && frames.isNotEmpty() && error == null
}
data class DimensionEvidence(val vocal: Double, val sport: Double, val classical: Double,
    val vocalSupport: Double, val sportSupport: Double, val classicalSupport: Double)

data class AnalysisEvidence(val segments: List<SegmentEvidence>, val additionalReasons: List<String>,
    val featureComplete: Boolean, val featureError: String?, val elapsedMs: Long, val contract: Int = 5,
    val rhythmContract: Int = 0, val rhythmState: String? = null, val baseEvidenceReused: Boolean = false) {
    val complete get() = featureComplete && segments.any { !it.additional } && segments.filter { !it.additional }.all { it.complete }
    fun scoredSegments() = segments.filter { s -> s.complete && (!s.additional || segments.none { it.additional && !it.complete }) }
    fun aggregate(): DimensionEvidence? {
        val good = scoredSegments()
        val frames = good.flatMap { it.frames }
        if (frames.isEmpty()) return null
        var vocal = frames.map { it.vocal }.average()
        for (s in good) for (run in s.frames.windowed(3)) vocal = maxOf(vocal, run.minOf { it.vocal } - .5)
        val sports = frames.map(TagClassifier::sportScore).sorted()
        val sport = (sports[(sports.size - 1) / 2] + sports[sports.size / 2]) / 2
        val classical = good.sumOf { it.mean!!.classical * it.frames.size } / frames.size
        return DimensionEvidence(vocal, sport, classical,
            frames.count { it.vocal >= TagClassifier.VOCAL_THRESHOLD }.toDouble() / frames.size,
            frames.count { TagClassifier.sportScore(it) >= Sensitivity.NORMAL.sport }.toDouble() / frames.size,
            frames.count { it.classical >= TagClassifier.CLASSICAL_THRESHOLD }.toDouble() / frames.size)
    }
    fun reasons(): List<String> = buildList {
        val a = aggregate()
        val good = scoredSegments()
        if (a == null || good.sumOf { it.frames.size } < 10 || good.size != segments.size) add("insufficient_evidence")
        if (a != null) {
            if (abs(a.vocal - TagClassifier.VOCAL_THRESHOLD) <= .5) add("voice_near_threshold")
            if (listOf(2.75, 3.25, 3.75).any { abs(a.sport - it) <= .35 }) add("mood_near_threshold")
            if (abs(a.classical - TagClassifier.CLASSICAL_THRESHOLD) <= .35) add("genre_near_threshold")
            fun conflict(positive: (SegmentEvidence) -> Boolean) = good.any(positive) && good.any { !positive(it) }
            if (conflict { it.mean!!.vocal >= TagClassifier.VOCAL_THRESHOLD }) add("voice_segment_conflict")
            if (conflict { TagClassifier.sportScore(it.mean!!) >= 3.25 }) add("mood_segment_conflict")
            if (conflict { it.mean!!.classical >= TagClassifier.CLASSICAL_THRESHOLD }) add("genre_segment_conflict")
        }
    }
    fun toJson(): String = JSONObject().put("contract", contract).put("feature_complete", featureComplete)
        .put("rhythm_contract",rhythmContract).put("rhythm_state",rhythmState).put("base_evidence_reused",baseEvidenceReused)
        .put("feature_error", featureError).put("elapsed_ms", elapsedMs).put("additional_reasons", JSONArray(additionalReasons))
        .put("segments", JSONArray(segments.map { s -> JSONObject().put("start_ms", s.startMs).put("end_ms", s.endMs)
            .put("additional", s.additional).put("sample_count", s.sampleCount).put("decode_attempts", s.decodeAttempts)
            .put("model_attempts", s.modelAttempts).put("mean", s.mean?.json()).put("frames", JSONArray(s.frames.map { it.json() }))
            .put("error", s.error).put("pulse", s.pulse?.json()) })).toString()
    companion object {
        private fun ModelScores.json() = JSONObject().put("vocal", vocal).put("classical", classical)
            .put("calm", calm).put("energy", energy).put("orchestra", orchestra)
        private fun score(o: JSONObject) = ModelScores(o.getDouble("vocal"), o.getDouble("classical"),
            o.getDouble("calm"), o.getDouble("energy"), o.getDouble("orchestra")).also {
                require(listOf(it.vocal, it.classical, it.calm, it.energy, it.orchestra).all(Double::isFinite))
            }
        private fun PercussivePulse.json() = JSONObject().put("start_ms",startMs).put("end_ms",endMs)
            .put("attempts",attempts).put("elapsed_ms",elapsedMs).put("error",error)
            .put("bass",JSONArray(bass.map { it.json() })).put("broadband",JSONArray(broadband.map { it.json() }))
        private fun PulsePeak.json() = JSONObject().put("bpm",bpm).put("correlation",correlation).put("coherence",coherence)
        private fun pulse(o: JSONObject,s: JSONObject): PercussivePulse {
            fun peaks(name: String): List<PulsePeak> {
                val a = o.getJSONArray(name)
                require(a.length() <= 8)
                return (0 until a.length()).map { i -> val p = a.getJSONObject(i)
                    PulsePeak(p.getInt("bpm"),p.getDouble("correlation"),p.getDouble("coherence")).also {
                        require(it.bpm in 40..240 && it.correlation.isFinite() && it.correlation in -1.0..1.0 &&
                            it.coherence.isFinite() && it.coherence in 0.0..1.0)
                    }
                }
            }
            val result = PercussivePulse(peaks("bass"),peaks("broadband"),o.optString("error").takeIf { it.isNotEmpty() && it != "null" },
                o.getLong("start_ms"),o.getLong("end_ms"),o.getInt("attempts"),o.getLong("elapsed_ms"))
            require(result.startMs >= 0 && result.startMs <= s.getLong("start_ms") && result.endMs >= s.getLong("end_ms") &&
                result.endMs-result.startMs <= 30000 && result.attempts in 1..3)
            return result
        }
        fun parse(json: String?): AnalysisEvidence? = try {
            val o = JSONObject(json ?: "")
            val contract = o.getInt("contract")
            require(contract == 5 || contract == 6)
            val rhythmContract = o.optInt("rhythm_contract")
            val rhythmState = o.optString("rhythm_state").takeIf { it.isNotEmpty() && it != "null" }
            require(contract != 6 || rhythmContract == 1 && rhythmState in listOf("not_needed","complete","failed"))
            val array = o.getJSONArray("segments")
            require(array.length() <= 5)
            val segments = (0 until array.length()).map { i ->
                val s = array.getJSONObject(i)
                val f = s.getJSONArray("frames")
                require(f.length() <= 62 && s.getLong("end_ms") > s.getLong("start_ms"))
                SegmentEvidence(s.getLong("start_ms"), s.getLong("end_ms"), s.getBoolean("additional"), s.getInt("sample_count"),
                    s.getInt("decode_attempts"), s.getInt("model_attempts"), s.optJSONObject("mean")?.let(::score),
                    (0 until f.length()).map { score(f.getJSONObject(it)) }, s.optString("error").takeIf { it.isNotEmpty() && it != "null" },
                    s.optJSONObject("pulse")?.let { pulse(it,s) })
            }
            require(contract != 5 || rhythmContract == 0 && rhythmState == null && !o.optBoolean("base_evidence_reused") && segments.none { it.pulse != null })
            val r = o.getJSONArray("additional_reasons")
            AnalysisEvidence(segments, (0 until r.length()).map(r::getString), o.getBoolean("feature_complete"),
                o.optString("feature_error").takeIf { it.isNotEmpty() && it != "null" }, o.getLong("elapsed_ms"),contract,rhythmContract,rhythmState,o.optBoolean("base_evidence_reused"))
        } catch (_: Exception) { null }
    }
}
