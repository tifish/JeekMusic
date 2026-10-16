package com.tinyfish.jeekmusic.util

import com.tinyfish.jeekmusic.data.scanner.SortKeys
import org.junit.Assert.assertEquals
import org.junit.Test

class NaturalOrderComparatorTest {

    @Test
    fun `数字按数值排序`() {
        val sorted = listOf("10.mp3", "2.mp3", "1.mp3", "02.mp3").sortedWith(NaturalOrderComparator)
        assertEquals(listOf("1.mp3", "2.mp3", "02.mp3", "10.mp3"), sorted)
    }

    @Test
    fun `忽略大小写`() {
        val sorted = listOf("b.mp3", "A.mp3", "c.mp3").sortedWith(NaturalOrderComparator)
        assertEquals(listOf("A.mp3", "b.mp3", "c.mp3"), sorted)
    }

    @Test
    fun `混合文本和数字`() {
        val sorted = listOf("Track 10", "Track 9", "Track 1a", "Track 1").sortedWith(NaturalOrderComparator)
        assertEquals(listOf("Track 1", "Track 1a", "Track 9", "Track 10"), sorted)
    }

    @Test
    fun `字母索引`() {
        val keys = SortKeys { it }
        assertEquals("Z", keys.indexLetter(keys.sortKey("zhou jie lun")))
        assertEquals("#", keys.indexLetter(keys.sortKey("1999")))
        assertEquals("#", keys.indexLetter(""))
    }
}
