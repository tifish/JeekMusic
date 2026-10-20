package com.tinyfish.jeekmusic.util

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.data.scanner.SortKeys
import com.tinyfish.jeekmusic.description
import com.tinyfish.jeekmusic.str
import com.tinyfish.jeekmusic.strings
import com.ibm.icu.text.Transliterator
import org.junit.Assert.assertEquals
import org.junit.Test

/** 用例在 shared/testcases/natural-order.json、sort-keys.json（两端共用）。 */
class NaturalOrderComparatorTest {

    @Test
    fun naturalOrder() {
        for (c in SharedCases.load("natural-order.json").cases()) {
            val sorted = c.getJSONArray("input").strings().sortedWith(NaturalOrderComparator)
            assertEquals(c.description(), c.getJSONArray("expected").strings(), sorted)
        }
    }

    @Test
    fun indexLetter() {
        val keys = SortKeys { it }
        for (c in SharedCases.load("sort-keys.json").cases("indexLetter")) {
            assertEquals(c.description(), c.str("expected"), keys.indexLetter(keys.sortKey(c.getString("input"))))
        }
    }

    /** 手机上用 android.icu；JVM 上用同源的 ICU4J，转写结果一样。 */
    @Test
    fun sortKey() {
        val transliterator = Transliterator.getInstance("Han-Latin; Latin-ASCII")
        val keys = SortKeys { transliterator.transliterate(it) }
        for (c in SharedCases.load("sort-keys.json").cases("sortKey")) {
            assertEquals(c.description(), c.str("expected"), keys.sortKey(c.getString("input")))
        }
    }

    /** 拼音、繁简的期望值由 shared/pinyin/build-testcases.py 用 ICU4J 生成；这里核对 ICU4J 本身（升级 ICU 时会发现结果变了）。 */
    @Test
    fun pinyinAndChineseText() {
        val pinyin = Transliterator.getInstance("Han-Latin; Latin-ASCII")
        for (c in SharedCases.load("pinyin.json").cases()) {
            assertEquals(c.getString("input"), c.getString("expected"), pinyin.transliterate(c.getString("input")))
        }
        val simplified = Transliterator.getInstance("Traditional-Simplified")
        val traditional = Transliterator.getInstance("Simplified-Traditional")
        for (c in SharedCases.load("chinese-text.json").cases()) {
            assertEquals(c.getString("input"), c.getString("simplified"), simplified.transliterate(c.getString("input")))
            assertEquals(c.getString("input"), c.getString("traditional"), traditional.transliterate(c.getString("input")))
        }
    }

    /** 补充：数字段的比较不会溢出，前导零不影响数值大小。 */
    @Test
    fun leadingZeros() {
        assertEquals(listOf("a1", "a01", "a001", "a2"), listOf("a001", "a2", "a01", "a1").sortedWith(NaturalOrderComparator))
    }
}
