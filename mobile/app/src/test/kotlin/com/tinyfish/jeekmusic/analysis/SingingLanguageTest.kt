package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.data.settings.TagFilter
import org.junit.Assert.*
import org.junit.Test

class SingingLanguageTest {
    @Test fun unknownInstrumentalAndMixedLanguagesFollowDesktopPolicy() {
        for (value in listOf("UNKNOWN", "INSTRUMENTAL")) for (filter in LanguageFilter.entries)
            assertFalse(SingingLanguage.manual(value).matches(filter))
        val mixed = SingingLanguage.manual("CHINESE_ENGLISH")
        assertTrue(mixed.matches(LanguageFilter.CHINESE)); assertTrue(mixed.matches(LanguageFilter.ENGLISH))
        assertFalse(mixed.matches(LanguageFilter.NON_CHINESE)); assertFalse(mixed.matches(LanguageFilter.NON_ENGLISH))
        assertTrue(SingingLanguage("known", listOf("yue")).matches(LanguageFilter.CHINESE))
        assertTrue(SingingLanguage.manual("OTHER").matches(LanguageFilter.NON_ENGLISH))
    }
    @Test fun filterGroupsAreExclusiveAndIntersectOtherDimensions() {
        val filter = TagFilter(voice = Tag.VOCAL, onlyUnannotated = true).toggleLanguage(LanguageFilter.CHINESE).toggleLanguage(LanguageFilter.NON_ENGLISH)
        assertEquals(2, filter.languages.size); assertTrue(filter.unannotated)
        assertEquals(LanguageFilter.NON_CHINESE, filter.toggleLanguage(LanguageFilter.NON_CHINESE).chinese)
        assertNull(filter.toggleLanguage(LanguageFilter.CHINESE).chinese)
        assertEquals(Tag.VOCAL, filter.toggleLanguage(LanguageFilter.CHINESE).voice)
        assertFalse(TagFilter().isActive)
    }
    @Test fun evidenceNeedsConfirmedSpeechAndStrongOrRepeatedSupport() {
        fun sample(lang: String?, probability: Double, speech: Boolean = true, vocal: Double = 0.0) = LanguageSample(vocal, lang, probability, speech)
        assertEquals("unknown", SingingLanguage.aggregate(listOf(sample("zh", .75))).state)
        assertEquals("known", SingingLanguage.aggregate(listOf(sample("zh", .75), sample("zh", .7))).state)
        assertEquals(listOf("en", "zh"), SingingLanguage.aggregate(listOf(sample("zh", .95), sample("en", .97))).languages)
        assertEquals("unknown", SingingLanguage.aggregate(listOf(sample("zh", .99, false), sample("en", .99, false))).state)
        assertEquals("instrumental", SingingLanguage.aggregate(listOf(sample(null, 0.0, false, -4.0), sample(null, 0.0, false, -4.0))).state)
    }
    @Test fun windowsStayWithinCueAndNeverOverlapOrExceedSixtySeconds() {
        for (duration in listOf(1000L, 3000L, 5000L, 25000L, 50000L, 180000L)) {
            val windows = SingingLanguage.windows(90000, duration)
            assertTrue(windows.size <= 3); assertTrue(windows.sumOf { it.last + 1 - it.first } <= 60000)
            windows.forEach { assertTrue(it.first >= 90000 && it.last < 90000 + duration) }
            windows.zipWithNext().forEach { (a,b) -> assertTrue(a.last < b.first) }
        }
    }
}
