package com.tinyfish.jeekmusic.data.scanner

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.description
import com.tinyfish.jeekmusic.strings
import org.junit.Assert.assertEquals
import org.junit.Test

/** 用例在 shared/testcases/artist-splitter.json（两端共用）。 */
class ArtistSplitterTest {

    @Test
    fun split() {
        for (c in SharedCases.load("artist-splitter.json").cases("split")) {
            var separators = c.optJSONArray("separators")?.strings()?.toSet() ?: ArtistSplitConfig.DEFAULT_SEPARATORS
            c.optJSONArray("extraSeparators")?.let { separators = separators + it.strings() }
            val exceptions = c.optJSONArray("exceptions")?.strings() ?: ArtistSplitConfig.DEFAULT_EXCEPTIONS
            val result = ArtistSplitter.split(c.getJSONArray("values").strings(), ArtistSplitConfig(separators, exceptions))
            assertEquals(c.description(), c.getJSONArray("expected").strings(), result)
        }
    }

    @Test
    fun nameKey() {
        for (c in SharedCases.load("artist-splitter.json").cases("nameKey")) {
            assertEquals(c.description(), c.getString("expected"), ArtistSplitter.nameKey(c.getString("input")))
        }
    }
}
