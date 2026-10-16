package com.tinyfish.jeekmusic.data.lyrics

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LrcParserTest {

    @Test
    fun `基本格式和元信息`() {
        val lyrics = LrcParser.parse(
            """
            [ti:晴天]
            [ar:周杰伦]
            [00:01.50]故事的小黄花
            [00:05.123]从出生那年就飘着
            [00:09]童年的荡秋千
            """.trimIndent(),
        )
        assertTrue(lyrics.synced)
        assertEquals(listOf(1_500L, 5_123L, 9_000L), lyrics.lines.map { it.timeMs })
        assertEquals("故事的小黄花", lyrics.lines.first().text)
    }

    @Test
    fun `一行多个时间戳`() {
        val lyrics = LrcParser.parse("[00:10.00][01:30.00]副歌\n[00:20.00]主歌")
        assertEquals(listOf(10_000L to "副歌", 20_000L to "主歌", 90_000L to "副歌"), lyrics.lines.map { it.timeMs to it.text })
    }

    @Test
    fun `同一时间戳的两行合并为原文和翻译`() {
        val lyrics = LrcParser.parse("[00:01.00]Hello\n[00:01.00]你好\n[00:02.00]World")
        assertEquals("Hello", lyrics.lines[0].text)
        assertEquals("你好", lyrics.lines[0].translation)
        assertNull(lyrics.lines[1].translation)
    }

    @Test
    fun `offset 为正时歌词提前`() {
        val lyrics = LrcParser.parse("[offset:+500]\n[00:02.00]a\n[00:00.20]b")
        assertEquals(listOf(0L, 1_500L), lyrics.lines.map { it.timeMs })
    }

    @Test
    fun `逐字歌词去掉逐字时间`() {
        val lyrics = LrcParser.parse("[00:01.00]<00:01.00>故<00:01.50>事<00:02.00>的")
        assertEquals("故事的", lyrics.lines.single().text)
    }

    @Test
    fun `没有时间戳时是纯文本歌词`() {
        val lyrics = LrcParser.parse("\n第一行\n\n第二行\n")
        assertFalse(lyrics.synced)
        assertEquals(listOf("第一行", "", "第二行"), lyrics.lines.map { it.text })
    }

    @Test
    fun `当前行二分查找`() {
        val lines = listOf(LyricLine(1_000, "a"), LyricLine(2_000, "b"), LyricLine(3_000, "c"))
        assertEquals(-1, LrcParser.currentIndex(lines, 500))
        assertEquals(0, LrcParser.currentIndex(lines, 1_000))
        assertEquals(1, LrcParser.currentIndex(lines, 2_999))
        assertEquals(2, LrcParser.currentIndex(lines, 99_000))
        assertEquals(-1, LrcParser.currentIndex(emptyList(), 0))
    }
}
