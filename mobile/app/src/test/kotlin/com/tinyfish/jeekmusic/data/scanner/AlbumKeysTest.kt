package com.tinyfish.jeekmusic.data.scanner

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Test

class AlbumKeysTest {

    private val volume = "external_primary"

    @Test
    fun `没有专辑名时为空`() {
        assertNull(AlbumKeys.of(null, "A", volume, "Music/"))
        assertNull(AlbumKeys.of("  ", "A", volume, "Music/"))
    }

    @Test
    fun `有专辑艺术家时跨目录合并`() {
        val a = AlbumKeys.of("叶惠美", "周杰伦", volume, "Music/a/")
        val b = AlbumKeys.of("叶惠美 ", "周杰伦", volume, "Music/b/")
        assertEquals(a, b)
    }

    @Test
    fun `没有专辑艺术家时按目录区分同名专辑`() {
        val a = AlbumKeys.of("Greatest Hits", null, volume, "Music/Queen/")
        val b = AlbumKeys.of("Greatest Hits", null, volume, "Music/ABBA/")
        assertNotEquals(a, b)
    }

    @Test
    fun `碟片目录取上一级`() {
        val cd1 = AlbumKeys.of("Live", null, volume, "Music/Live/CD1/")
        val cd2 = AlbumKeys.of("Live", null, volume, "Music/Live/Disc 2/")
        assertEquals(cd1, cd2)
        assertEquals("Music/Live/", AlbumKeys.albumDirectory("Music/Live/CD1/"))
        assertEquals("Music/CDs/", AlbumKeys.albumDirectory("Music/CDs/"))
    }
}
