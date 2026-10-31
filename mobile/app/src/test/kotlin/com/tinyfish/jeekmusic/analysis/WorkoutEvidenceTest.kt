package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.SharedCases
import org.junit.Assert.*
import org.junit.Test

class WorkoutEvidenceTest {
    @Test fun sharedConjunctionFailuresAndSensitivity() {
        val cases = SharedCases.load("workout-v8.json").getJSONArray("cases")
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val e = AnalysisEvidence.parse(c.getJSONObject("evidence").toString())!!
            assertTrue(e.complete)
            for (sensitivity in Sensitivity.entries) {
                val expected = c.getJSONObject("expected").let { if (it.isNull(sensitivity.name)) null else it.getString(sensitivity.name) }
                assertEquals(c.getString("name"),expected,WorkoutEvidence.classify(e,sensitivity)?.name)
            }
            assertEquals(e.aggregate(),AnalysisEvidence.parse(e.toJson())!!.aggregate())
            assertEquals(CalmnessEvidence.from(e)?.tag,CalmnessEvidence.from(e.copy(contract=5,rhythmContract=0,rhythmState=null))?.tag)
        }
    }
    @Test fun contextPlanKeepsLogicalBoundariesAndVersionIsRequired() {
        val e = AnalysisEvidence.parse(SharedCases.load("workout-v8.json").getJSONArray("cases").getJSONObject(0).getJSONObject("evidence").toString())!!
        val plan = WorkoutEvidence.plan(e,20000,80000)
        assertEquals(2,plan.size)
        assertTrue(plan.all { it.startMs >= 20000 && it.endMs <= 100000 && it.endMs-it.startMs == 30000L })
        assertFalse(plan[0].startMs < plan[1].endMs && plan[0].endMs > plan[1].startMs)
        assertNull(AnalysisEvidence.parse(e.copy(rhythmContract=0).toJson()))
        assertNull(AnalysisEvidence.parse(e.copy(contract=5).toJson()))
    }
}
