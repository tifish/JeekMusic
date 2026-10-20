package com.tinyfish.jeekmusic.data.scanner

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.charset.Charset

class CueParserTest {

    private val sample = """
        REM GENRE Pop
        REM DATE 2003
        PERFORMER "周杰伦"
        TITLE "叶惠美"
        FILE "叶惠美.wav" WAVE
          TRACK 01 AUDIO
            TITLE "以父之名"
            INDEX 01 00:00:00
          TRACK 02 AUDIO
            TITLE "懦夫"
            PERFORMER "周杰伦 feat. 费玉清"
            INDEX 00 05:41:20
            INDEX 01 05:42:37
          TRACK 03 AUDIO
            TITLE "晴天"
            INDEX 01 09:58:00
    """.trimIndent()

    @Test
    fun `解析专辑信息和音轨`() {
        val sheet = CueParser.parse(sample)
        assertEquals("叶惠美", sheet.title)
        assertEquals("周杰伦", sheet.performer)
        assertEquals(2003, sheet.year)
        assertEquals("Pop", sheet.genre)
        val file = sheet.files.single()
        assertEquals("叶惠美.wav", file.name)
        assertEquals(listOf(1, 2, 3), file.tracks.map { it.number })
        assertEquals(listOf("以父之名", "懦夫", "晴天"), file.tracks.map { it.title })
        // 缺省继承顶层 PERFORMER
        assertEquals(listOf("周杰伦", "周杰伦 feat. 费玉清", "周杰伦"), file.tracks.map { it.performer })
    }

    @Test
    fun `使用 INDEX 01 作为起点，帧按每秒 75 帧换算`() {
        val tracks = CueParser.parse(sample).files.single().tracks
        assertEquals(0L, tracks[0].startMs)
        assertEquals(5 * 60_000L + 42_000 + 37 * 1000 / 75, tracks[1].startMs)
        assertEquals(9 * 60_000L + 58_000, tracks[2].startMs)
    }

    @Test
    fun `分轨 CUE`() {
        val cue = """
            FILE "01.flac" WAVE
              TRACK 01 AUDIO
                INDEX 01 00:00:00
            FILE "02.flac" WAVE
              TRACK 02 AUDIO
                INDEX 01 00:00:00
        """.trimIndent()
        assertTrue(CueParser.parse(cue).isPerTrack)
        assertFalse(CueParser.parse(sample).isPerTrack)
    }

    @Test
    fun `文件名不带引号`() {
        assertEquals("album.flac", CueParser.parse("FILE album.flac WAVE").files.single().name)
    }

    @Test
    fun `GBK 编码的 CUE 自动识别`() {
        val bytes = sample.toByteArray(Charset.forName("GBK"))
        assertEquals("叶惠美", CueParser.parse(CharsetDetector.decode(bytes)).title)
    }

    @Test
    fun `编码识别：BOM、UTF-8、Big5`() {
        assertEquals("晴天", CharsetDetector.decode(byteArrayOf(0xEF.toByte(), 0xBB.toByte(), 0xBF.toByte()) + "晴天".toByteArray()))
        assertEquals("晴天", CharsetDetector.decode("晴天".toByteArray()))
        assertEquals("張學友", CharsetDetector.decode("張學友".toByteArray(Charset.forName("Big5"))))
    }
}
