package com.tinyfish.jeekmusic.data.library

import com.tinyfish.jeekmusic.data.db.DirectoryCount
import org.junit.Assert.assertEquals
import org.junit.Test

class FolderTreeTest {

    private val counts = listOf(
        DirectoryCount("Music/", 3),
        DirectoryCount("Music/Pop/", 5),
        DirectoryCount("Music/Pop/2004/", 2),
        DirectoryCount("Music/Classical/Bach/", 4),
        DirectoryCount("Music/10 Live/", 1),
        DirectoryCount("Music/9 Live/", 1),
    )

    @Test
    fun `直接子文件夹，歌曲数包含更深的层级`() {
        assertEquals(
            listOf(
                FolderNode("9 Live", "Music/9 Live/", 1),
                FolderNode("10 Live", "Music/10 Live/", 1),
                FolderNode("Classical", "Music/Classical/", 4),
                FolderNode("Pop", "Music/Pop/", 7),
            ),
            FolderTree.children("Music/", counts),
        )
    }

    @Test
    fun `只有更深层有歌曲的中间目录也会出现`() {
        assertEquals(listOf(FolderNode("Bach", "Music/Classical/Bach/", 4)), FolderTree.children("Music/Classical/", counts))
    }

    @Test
    fun `没有子文件夹`() {
        assertEquals(emptyList<FolderNode>(), FolderTree.children("Music/Pop/2004/", counts))
    }
}
