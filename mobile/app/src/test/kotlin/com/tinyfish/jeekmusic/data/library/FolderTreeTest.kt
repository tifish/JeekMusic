package com.tinyfish.jeekmusic.data.library

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.data.db.DirectoryCount
import com.tinyfish.jeekmusic.description
import com.tinyfish.jeekmusic.objects
import org.junit.Assert.assertEquals
import org.junit.Test

/** 用例在 shared/testcases/folder-tree.json（两端共用）。 */
class FolderTreeTest {

    @Test
    fun children() {
        val file = SharedCases.load("folder-tree.json")
        val counts = file.getJSONArray("counts").let { a ->
            (0 until a.length()).map { DirectoryCount(a.getJSONArray(it).getString(0), a.getJSONArray(it).getInt(1)) }
        }
        for (c in file.cases()) {
            val expected = c.getJSONArray("expected").objects().map {
                FolderNode(it.getString("name"), it.getString("relativePath"), it.getInt("trackCount"))
            }
            assertEquals(c.description(), expected, FolderTree.children(c.getString("path"), counts))
        }
    }
}
