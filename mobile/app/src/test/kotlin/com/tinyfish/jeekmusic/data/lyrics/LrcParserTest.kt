package com.tinyfish.jeekmusic.data.lyrics

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.description
import com.tinyfish.jeekmusic.objects
import com.tinyfish.jeekmusic.str
import com.tinyfish.jeekmusic.text
import org.junit.Assert.assertEquals
import org.junit.Test

/** 用例在 shared/testcases/lrc-parser.json（两端共用）。 */
class LrcParserTest {

    @Test
    fun parse() {
        for (c in SharedCases.load("lrc-parser.json").cases()) {
            val lyrics = LrcParser.parse(c.text())
            val e = c.getJSONObject("expected")
            assertEquals(c.description(), e.getBoolean("synced"), lyrics.synced)
            val expected = e.getJSONArray("lines").objects().map { LyricLine(it.getLong("timeMs"), it.getString("text"), it.str("translation")) }
            assertEquals(c.description(), expected, lyrics.lines)
        }
    }

    @Test
    fun currentIndex() {
        for (c in SharedCases.load("lrc-parser.json").cases("currentIndex")) {
            val lines = c.getJSONArray("lines").let { a -> (0 until a.length()).map { LyricLine(a.getLong(it), "") } }
            assertEquals(c.description(), c.getInt("expected"), LrcParser.currentIndex(lines, c.getLong("positionMs")))
        }
    }
}
