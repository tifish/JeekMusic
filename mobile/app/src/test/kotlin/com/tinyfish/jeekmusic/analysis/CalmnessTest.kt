package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.data.settings.TagFilter
import org.junit.Assert.*
import org.junit.Test

class CalmnessTest {
    @Test fun sharedWindowBoundaries() {
        val cases = SharedCases.load("calmness-v6.json").getJSONArray("cases")
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val e = AnalysisEvidence.parse(c.getJSONObject("evidence").toString())!!
            val expected = if (c.isNull("expected")) null else c.getString("expected")
            assertEquals(c.getString("name"), expected, CalmnessEvidence.from(e)?.tag?.name)
            assertNull(CalmnessEvidence.from(e.copy(featureComplete = false)))
        }
        assertNull(CalmnessEvidence.from(null))
        assertEquals(Tag.NON_SPORT, Tag.of(TagDimension.MOOD, "QUIET"))
        assertNull(Tag.of(TagDimension.CALMNESS, "QUIET"))
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
