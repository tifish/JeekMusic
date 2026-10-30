package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.SharedCases
import org.junit.Assert.*
import org.junit.Test

class AnalysisEvidenceTest {
    @Test fun sharedAggregationAndSerialization() {
        val cases = SharedCases.load("analysis-v5.json").getJSONArray("cases")
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val e = AnalysisEvidence.parse(c.getJSONObject("evidence").toString())!!
            assertTrue(e.complete)
            val a = e.aggregate()!!
            assertEquals(c.getBoolean("vocal"), a.vocal >= TagClassifier.VOCAL_THRESHOLD)
            assertEquals(c.getBoolean("sport"), a.sport >= 3.25)
            assertEquals(a, AnalysisEvidence.parse(e.toJson())!!.aggregate())
        }
    }
    @Test fun lowRatesAndSamplingBoundaries() {
        assertArrayEquals(floatArrayOf(0f, .5f, 1f, 1f), PcmContract.resample(floatArrayOf(0f, 1f), 8000, 16000), 0f)
        assertEquals(16000, PcmContract.resample(FloatArray(11025), 11025, 16000).size)
        for (duration in listOf(40000L, 45000L, 100000L, 300000L)) {
            val initial = PcmContract.windows(90000, duration)
            val extra = PcmContract.additional(90000, duration, initial)
            assertTrue(extra.size <= 2 && extra.sumOf { it.last + 1 - it.first } <= 20000)
            val all = (initial + extra).sortedBy { it.first }
            assertTrue(all.all { it.first >= 90000 && it.last < 90000 + duration })
            assertTrue(all.zipWithNext().all { it.first.last < it.second.first })
        }
        assertTrue(PcmContract.additional(90000, 30000, PcmContract.windows(90000, 30000)).isEmpty())
    }
}
