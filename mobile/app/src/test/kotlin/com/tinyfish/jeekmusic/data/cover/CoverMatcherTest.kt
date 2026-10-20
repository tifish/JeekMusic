package com.tinyfish.jeekmusic.data.cover

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.description
import org.junit.Assert.assertEquals
import org.junit.Test

/** 用例在 shared/testcases/cover-matcher.json（两端共用）。单元测试里没有 android.icu，用 JSON 里的小表模拟繁简转换。 */
class CoverMatcherTest {

    private val file = SharedCases.load("cover-matcher.json")
    private val fold = file.getJSONObject("fold").let { o -> o.keys().asSequence().associate { it[0] to o.getString(it)[0] } }
    private val matcher = CoverMatcher { text -> text.map { fold[it] ?: it }.joinToString("") }

    @Test
    fun titles() {
        for (c in file.cases("titleMatches")) {
            assertEquals(c.description(), c.getBoolean("result"), matcher.titleMatches(c.getString("expected"), c.getString("actual")))
        }
    }

    @Test
    fun artists() {
        for (c in file.cases("artistMatches")) {
            assertEquals(c.description(), c.getBoolean("result"), matcher.artistMatches(c.getString("expected"), c.getString("actual")))
        }
        for (c in file.cases("primaryArtist")) {
            assertEquals(c.description(), c.getString("expected"), CoverMatcher.primaryArtist(c.getString("input")))
        }
    }

    @Test
    fun detectsChinese() {
        for (c in file.cases("hasCjk")) {
            assertEquals(c.description(), c.getBoolean("expected"), CoverMatcher.hasCjk(c.getString("input")))
        }
    }
}
