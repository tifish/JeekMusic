package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.str
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** 用例在 shared/testcases/classical-titles.json（两端共用）；曲名补救的完整分类见 tag-classifier.json 的 classify。 */
class ClassicalTitlesTest {

    @Test
    fun titles() {
        val file = SharedCases.load("classical-titles.json")
        for (c in file.cases("matches")) assertTrue(c.str("title"), ClassicalTitles.matches(c.str("title"), c.str("album")))
        for (c in file.cases("notMatches")) assertFalse(c.str("title"), ClassicalTitles.matches(c.str("title"), c.str("album")))
    }
}
