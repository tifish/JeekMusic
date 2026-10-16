package com.tinyfish.jeekmusic.data.scanner

import org.junit.Assert.assertEquals
import org.junit.Test
import java.nio.charset.Charset

class CharsetFixerTest {

    /** 模拟 TagLib 的行为：把 [charset] 编码的字节按 Latin-1 解码。 */
    private fun mojibake(text: String, charset: String) =
        text.toByteArray(Charset.forName(charset)).toString(Charsets.ISO_8859_1)

    @Test
    fun `GBK 乱码被修复`() {
        assertEquals("荣耀", CharsetFixer.fix(mojibake("荣耀", "GB2312")))
        assertEquals("周杰伦", CharsetFixer.fix(mojibake("周杰伦", "GBK")))
    }

    @Test
    fun `中英混排的 GBK 乱码被修复`() {
        assertEquals("G.E.M. 邓紫棋", CharsetFixer.fix(mojibake("G.E.M. 邓紫棋", "GBK")))
    }

    @Test
    fun `正确的 Unicode 原样保留`() {
        assertEquals("周杰伦", CharsetFixer.fix("周杰伦"))
        assertEquals("Ünïcödé 中文", CharsetFixer.fix("Ünïcödé 中文"))
    }

    @Test
    fun `纯 ASCII 原样保留`() {
        assertEquals("Queen", CharsetFixer.fix("Queen"))
        assertEquals("", CharsetFixer.fix(""))
    }

    @Test
    fun `带重音符号的西文不被误判`() {
        listOf("Motörhead", "Beyoncé", "Café del Mar", "Björk", "Mötley Crüe", "Sigur Rós", "Déjà vu").forEach {
            assertEquals(it, CharsetFixer.fix(it))
        }
    }

    @Test
    fun `强制指定 Big5`() {
        val big5 = Charset.forName("Big5")
        assertEquals("張學友", CharsetFixer.fix(mojibake("張學友", "Big5"), forced = big5))
    }

    @Test
    fun `强制解码失败时原样保留`() {
        // GBK 字节不是合法的 UTF-8，严格解码会失败
        val value = mojibake("荣耀", "GB2312")
        assertEquals(value, CharsetFixer.fix(value, forced = Charsets.UTF_8))
    }

    @Test
    fun `多值逐个修复`() {
        val values = listOf(mojibake("周杰伦", "GBK"), "Queen")
        assertEquals(listOf("周杰伦", "Queen"), CharsetFixer.fixAll(values))
    }
}
