package com.tinyfish.jeekmusic.data.filter

import com.tinyfish.jeekmusic.data.scanner.SortKeys
import com.ibm.icu.text.Transliterator
import org.junit.Assert.*
import org.junit.Test

class InitialsMatcherTest {
    private val matcher = InitialsMatcher(SortKeys { Transliterator.getInstance("Han-Latin; Latin-ASCII").transliterate(it) })
    @Test fun initialsMatchHanAcrossAllFieldsAndKeepLiteralSearch() {
        assertEquals("zjl", matcher.initials("周杰伦 Jay"))
        assertTrue(matcher.matches("ZJL", "晴天", "周杰伦", "叶惠美"))
        assertTrue(matcher.matches("qtx", "晴天雪"))
        assertTrue(matcher.matches("yhm", "晴天", "周杰伦", "叶惠美"))
        assertTrue(matcher.matches("Jay", "周杰伦 Jay"))
        assertTrue(matcher.matches("100%_", "track 100%_final.flac"))
        assertFalse(matcher.matches("jay", "Just A Year"))
        assertFalse(matcher.matches(" ", "anything"))
    }
}
