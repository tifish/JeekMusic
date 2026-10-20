package com.tinyfish.jeekmusic.data.backup

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.data.backup.BackupRefs.Root
import com.tinyfish.jeekmusic.description
import com.tinyfish.jeekmusic.str
import org.junit.Assert.assertEquals
import org.junit.Test

/** 用例在 shared/testcases/backup-refs.json（两端共用）。 */
class BackupRefsTest {

    @Test
    fun refOf() {
        for (c in SharedCases.load("backup-refs.json").cases("refOf")) {
            val roots = c.getJSONArray("roots").let { a -> (0 until a.length()).map { Root(a.getJSONArray(it).getString(0), a.getJSONArray(it).getString(1)) } }
            assertEquals(c.description(), c.str("expected"), BackupRefs.refOf(c.getString("trackKey"), roots))
        }
    }

    @Test
    fun match() {
        for (c in SharedCases.load("backup-refs.json").cases("match")) {
            val refs = c.getJSONObject("refs").let { o -> o.keys().asSequence().associateWith { o.getString(it) } }
            assertEquals(c.description(), c.str("expected"), BackupRefs.Matcher(refs).match(c.getString("ref")))
        }
    }
}
