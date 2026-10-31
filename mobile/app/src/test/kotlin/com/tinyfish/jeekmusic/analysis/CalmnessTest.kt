package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.data.settings.TagFilter
import org.junit.Assert.*
import org.junit.Test

class CalmnessTest {
    @Test fun sharedWindowBoundaries() {
        for (file in listOf("calmness-v6.json", "calmness-v7.json")) {
            val cases = SharedCases.load(file).getJSONArray("cases")
            for (i in 0 until cases.length()) {
                val c = cases.getJSONObject(i)
                val e = AnalysisEvidence.parse(c.getJSONObject("evidence").toString())!!
                val expected = if (c.isNull("expected")) null else c.getString("expected")
                val calm = CalmnessEvidence.from(e)
                assertEquals(c.getString("name"), expected, calm?.tag?.name)
                if (c.has("expected_support")) assertEquals(c.getDouble("expected_support"), calm!!.support, 1e-12)
                if (c.has("expected_minimum_segment_support")) assertEquals(c.getDouble("expected_minimum_segment_support"), calm!!.minimumSegmentSupport, 1e-12)
                if (c.has("expected_strict_support")) assertEquals(c.getDouble("expected_strict_support"), calm!!.strictSupport, 1e-12)
                if (calm != null) {
                    assertEquals(calm.windows, calm.segments.sumOf { it.windows })
                    assertEquals("strict_or_relative_per_window", calm.toJson().getString("supportDefinition"))
                    assertEquals(.5, calm.toJson().getJSONObject("thresholds").getDouble("relativeEnergyToCalmCeiling"), 0.0)
                    assertTrue(calm.support >= maxOf(calm.strictSupport, calm.relativeSupport) && calm.support <= 1)
                }
                assertNull(CalmnessEvidence.from(e.copy(featureComplete = false)))
            }
        }
        assertNull(CalmnessEvidence.from(null))
        assertEquals(Tag.NON_SPORT, Tag.of(TagDimension.MOOD, "QUIET"))
        assertNull(Tag.of(TagDimension.CALMNESS, "QUIET"))
    }
    @Test fun invalidWindowCannotSupplyEvidence() {
        for (value in listOf(Double.NaN, Double.POSITIVE_INFINITY, -.01)) {
            val f = ModelScores(-3.0, -1.0, value, .01, 0.0)
            val s = SegmentEvidence(0, 3000, false, 48000, 1, 1, f, List(3) { f }, null)
            assertNull(CalmnessEvidence.from(AnalysisEvidence(listOf(s), emptyList(), true, null, 0)))
            assertNull(CalmnessEvidence.from(AnalysisEvidence(listOf(s.copy(frames = List(3) { f.copy(calm = .04, energy = value) })), emptyList(), true, null, 0)))
        }
    }
    @Test fun filtersCombineReplaceAndClearIndependently() {
        val f = TagFilter(mood = Tag.NON_SPORT).toggle(Tag.CALM).toggle(Tag.VOCAL)
        assertEquals(listOf(Tag.NON_SPORT, Tag.VOCAL, Tag.CALM), f.active)
        assertEquals(Tag.NON_CALM, f.toggle(Tag.NON_CALM).calmness)
        assertEquals(Tag.NON_SPORT, f.toggle(Tag.NON_CALM).mood)
        assertNull(f.toggle(Tag.CALM).calmness)
        assertFalse(TagFilter().isActive)
    }
}
