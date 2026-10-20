package com.tinyfish.jeekmusic.data.scanner

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.description
import com.tinyfish.jeekmusic.flag
import com.tinyfish.jeekmusic.str
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

/** 用例在 shared/testcases/album-keys.json（两端共用；desktopOnly 的用例只在桌面端跑）。 */
class AlbumKeysTest {

    private fun key(o: JSONObject) = AlbumKeys.of(o.str("album"), o.str("albumArtist"), o.getString("volume"), o.getString("dir"))

    @Test
    fun albumKeys() {
        val file = SharedCases.load("album-keys.json")
        for (c in file.cases("nullKeys")) assertNull(c.description(), key(c))
        for (c in file.cases("pairs").filterNot { it.flag("desktopOnly") }) {
            val a = key(c.getJSONObject("a"))
            val b = key(c.getJSONObject("b"))
            assertNotNull(c.description(), a)
            assertEquals(c.description(), c.getBoolean("same"), a == b)
        }
        for (c in file.cases("albumDirectory").filterNot { it.flag("desktopOnly") }) {
            assertEquals(c.description(), c.getString("expected"), AlbumKeys.albumDirectory(c.getString("input")))
        }
    }
}
