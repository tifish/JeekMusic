package com.tinyfish.jeekmusic.data.playlist

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.description
import com.tinyfish.jeekmusic.objects
import com.tinyfish.jeekmusic.strings
import com.tinyfish.jeekmusic.text
import org.junit.Assert.assertEquals
import org.junit.Test

/** 用例在 shared/testcases/m3u.json（两端共用）。 */
class M3uTest {

    @Test
    fun m3u() {
        val file = SharedCases.load("m3u.json")
        for (c in file.cases("format")) {
            val entries = c.getJSONArray("entries").objects().map { M3u.Entry(it.getString("path"), it.getLong("durationSec"), it.getString("title")) }
            assertEquals(c.description(), c.getString("expected"), M3u.format(entries))
        }
        for (c in file.cases("parse")) {
            val expected = c.getJSONArray("expected").let { a -> (0 until a.length()).map { a.getJSONArray(it).strings() } }
            assertEquals(c.description(), expected, M3u.parse(c.text()))
        }
        for (c in file.cases("storagePath")) {
            assertEquals(c.description(), c.getString("expected"), M3u.storagePath(c.getString("volume"), c.getString("relativePath"), c.getString("fileName")))
        }
    }
}
