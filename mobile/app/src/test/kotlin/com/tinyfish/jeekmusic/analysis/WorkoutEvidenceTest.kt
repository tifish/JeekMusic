package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.SharedCases
import org.junit.Assert.*
import org.junit.Test

class WorkoutEvidenceTest {
    @Test fun normalLegacyWeightedBoundary() {
        for(offset in listOf(-.000001,.000001)) {
            val f=AudioFeatures(130.0,2.0,-7.0,0.0,2.5,1000.0,.2+(.05+offset)/.30*.4)
            for(s in Sensitivity.entries) assertEquals(if(offset<0) Tag.NON_SPORT else Tag.SPORT,LegacyMoodClassifier.classify(f,s))
        }
    }
    @Test fun oldSensitivityInputsAndSamplingAlwaysUseNormal() {
        for(s in Sensitivity.entries) { assertEquals(3.25,s.sport,0.0); assertEquals(.60,s.legacy,0.0) }
        val m=ModelScores(-3.0,-2.0,0.0,.003*(kotlin.math.exp(3.3)-1),0.0)
        val e=AnalysisEvidence((0..1).map { i -> SegmentEvidence(i*40000L,i*40000L+10000,false,160000,1,1,m,List(12){m},null) },emptyList(),true,null,0,contract=6,rhythmContract=1,rhythmState="not_needed")
        assertTrue(e.reasons().contains("mood_near_threshold")); assertFalse(e.reasons().contains("mood_segment_conflict"))
        assertFalse(WorkoutEvidence.needsPulse(e)); assertTrue(WorkoutEvidence.plan(e,0,100000).isEmpty())
        for(s in Sensitivity.entries) assertEquals(Tag.SPORT,WorkoutEvidence.classify(e,s))
    }
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
        val e = AnalysisEvidence.parse(SharedCases.load("workout-v8.json").getJSONArray("cases").let { it.getJSONObject(it.length()-1) }.getJSONObject("evidence").toString())!!
        val plan = WorkoutEvidence.plan(e,20000,80000)
        assertEquals(2,plan.size)
        assertTrue(plan.all { it.startMs >= 20000 && it.endMs <= 100000 && it.endMs-it.startMs == 30000L })
        assertFalse(plan[0].startMs < plan[1].endMs && plan[0].endMs > plan[1].startMs)
        assertNull(AnalysisEvidence.parse(e.copy(rhythmContract=0).toJson()))
        assertNull(AnalysisEvidence.parse(e.copy(contract=5).toJson()))
    }
}
