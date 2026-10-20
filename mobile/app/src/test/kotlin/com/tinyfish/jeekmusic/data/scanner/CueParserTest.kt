package com.tinyfish.jeekmusic.data.scanner

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.description
import com.tinyfish.jeekmusic.objects
import com.tinyfish.jeekmusic.str
import com.tinyfish.jeekmusic.text
import org.junit.Assert.assertEquals
import org.junit.Test
import java.nio.charset.Charset

/** 用例在 shared/testcases/cue-parser.json（两端共用）。 */
class CueParserTest {

    @Test
    fun parse() {
        for (c in SharedCases.load("cue-parser.json").cases()) {
            val text = c.str("encoding")?.let { CharsetDetector.decode(c.text().toByteArray(Charset.forName(it))) } ?: c.text()
            val sheet = CueParser.parse(text)
            val e = c.getJSONObject("expected")
            val d = c.description()
            if (e.has("title")) assertEquals(d, e.str("title"), sheet.title)
            if (e.has("performer")) assertEquals(d, e.str("performer"), sheet.performer)
            if (e.has("year")) assertEquals(d, if (e.isNull("year")) null else e.getInt("year"), sheet.year)
            if (e.has("genre")) assertEquals(d, e.str("genre"), sheet.genre)
            if (e.has("isPerTrack")) assertEquals(d, e.getBoolean("isPerTrack"), sheet.isPerTrack)
            if (e.has("files")) {
                val expected = e.getJSONArray("files").objects().map { f ->
                    CueFile(f.getString("name"), f.getJSONArray("tracks").objects().map {
                        CueTrack(it.getInt("number"), it.str("title"), it.str("performer"), it.getLong("startMs"))
                    })
                }
                assertEquals(d, expected, sheet.files)
            }
        }
    }

    @Test
    fun parseTime() {
        for (c in SharedCases.load("cue-parser.json").cases("parseTime")) {
            assertEquals(c.description(), if (c.isNull("expected")) null else c.getLong("expected"), CueParser.parseTime(c.getString("input")))
        }
    }
}
