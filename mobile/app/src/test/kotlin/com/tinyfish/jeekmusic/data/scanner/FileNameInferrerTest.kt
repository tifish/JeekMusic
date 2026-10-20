package com.tinyfish.jeekmusic.data.scanner

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class FileNameInferrerTest {

    private fun infer(name: String, format: FileNameFormat = FileNameFormat.ARTIST_TITLE) =
        FileNameInferrer.infer(name, format)

    @Test
    fun `艺术家 - 标题`() {
        assertEquals(InferredName("周杰伦", "晴天"), infer("周杰伦 - 晴天.mp3"))
        assertEquals(InferredName("Panta.Q,周深", "对不起"), infer("Panta.Q,周深 - 对不起.lrc"))
    }

    @Test
    fun `去掉开头的音轨号`() {
        assertEquals(InferredName("Queen", "Bohemian Rhapsody"), infer("01. Queen - Bohemian Rhapsody.flac"))
        assertEquals(InferredName("Queen", "Bohemian Rhapsody"), infer("01 - Queen - Bohemian Rhapsody.flac"))
    }

    @Test
    fun `只有音轨号和标题时推断失败`() {
        assertNull(infer("03 - 晴天.mp3"))
    }

    @Test
    fun `四位数字不当作音轨号`() {
        assertEquals(InferredName("1999", "Prince"), infer("1999 - Prince.mp3"))
    }

    @Test
    fun `连字符两侧没有空格时不拆`() {
        assertNull(infer("Jay-Z.mp3"))
        assertEquals(InferredName("Jay-Z", "Empire State of Mind"), infer("Jay-Z - Empire State of Mind.mp3"))
    }

    @Test
    fun `破折号和全角连字符`() {
        assertEquals(InferredName("许巍", "曾经的你"), infer("许巍—曾经的你.mp3"))
        assertEquals(InferredName("许巍", "曾经的你"), infer("许巍－曾经的你.mp3"))
    }

    @Test
    fun `标题中还有分隔符时只按第一个拆`() {
        assertEquals(
            InferredName("Accardo", "Vivaldi - The Four Seasons"),
            infer("Accardo - Vivaldi - The Four Seasons.flac"),
        )
    }

    @Test
    fun `标题 - 艺术家格式`() {
        assertEquals(InferredName("周杰伦", "晴天"), infer("晴天 - 周杰伦.mp3", FileNameFormat.TITLE_ARTIST))
    }

    @Test
    fun `没有分隔符时推断失败`() {
        assertNull(infer("50c01294df9d48902639978235.mp3"))
        assertNull(infer(" - 晴天.mp3"))
    }
}
