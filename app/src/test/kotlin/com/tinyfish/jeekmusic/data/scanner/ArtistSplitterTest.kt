package com.tinyfish.jeekmusic.data.scanner

import org.junit.Assert.assertEquals
import org.junit.Test

class ArtistSplitterTest {

    private fun split(vararg values: String, config: ArtistSplitConfig = ArtistSplitConfig()) =
        ArtistSplitter.split(values.toList(), config)

    @Test
    fun `默认按斜杠拆分（网易云的写法）`() {
        assertEquals(listOf("薛之谦", "张靓颖"), split("薛之谦/张靓颖"))
        assertEquals(listOf("张惠妹", "艾怡良", "徐佳莹"), split("张惠妹/艾怡良/徐佳莹"))
        assertEquals(listOf("Herbert von Karajan", "Wiener Philharmoniker"), split("Herbert von Karajan/Wiener Philharmoniker"))
    }

    @Test
    fun `分号和顿号`() {
        assertEquals(listOf("A", "B"), split("A; B"))
        assertEquals(listOf("周杰伦", "费玉清"), split("周杰伦、费玉清"))
    }

    @Test
    fun `feat 和 ft`() {
        assertEquals(listOf("Rihanna", "Eminem"), split("Rihanna feat. Eminem"))
        assertEquals(listOf("Rihanna", "Eminem"), split("Rihanna ft Eminem"))
        assertEquals(listOf("Rihanna", "Eminem"), split("Rihanna FEAT. Eminem"))
        assertEquals(listOf("A", "B"), split("A (feat. B)"))
        assertEquals(listOf("A", "B"), split("A featuring B"))
    }

    @Test
    fun `词语分隔符不会拆开单词内部`() {
        assertEquals(listOf("Fitz and the Tantrums"), split("Fitz and the Tantrums"))
        assertEquals(listOf("Lefty"), split("Lefty"))
    }

    @Test
    fun `默认不按 & 和逗号拆分`() {
        assertEquals(listOf("小娟&山谷里的居民"), split("小娟&山谷里的居民"))
        assertEquals(listOf("Crosby, Stills"), split("Crosby, Stills"))
    }

    @Test
    fun `启用 & 后拆分`() {
        val config = ArtistSplitConfig(separators = ArtistSplitConfig.DEFAULT_SEPARATORS + "&")
        assertEquals(listOf("杨宗纬", "李泉"), split("杨宗纬&李泉", config = config))
    }

    @Test
    fun `例外名单不拆`() {
        assertEquals(listOf("AC/DC"), split("AC/DC"))
        val config = ArtistSplitConfig(separators = ArtistSplitConfig.DEFAULT_SEPARATORS + "&")
        assertEquals(listOf("Simon & Garfunkel"), split("Simon & Garfunkel", config = config))
        assertEquals(listOf("AC/DC", "Queen"), split("AC/DC/Queen"))
    }

    @Test
    fun `多值标签逐个拆分并去重`() {
        assertEquals(listOf("A", "B", "C"), split("A/B", "b", "C"))
    }

    @Test
    fun `忽略空白和空段`() {
        assertEquals(listOf("A", "B"), split(" A // B "))
        assertEquals(emptyList<String>(), split("  "))
    }

    @Test
    fun `nameKey 忽略大小写和多余空白`() {
        assertEquals("the beatles", ArtistSplitter.nameKey("  The   Beatles "))
    }
}
