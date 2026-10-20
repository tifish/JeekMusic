package com.tinyfish.jeekmusic.data.scanner

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class SafPathsTest {

    @Test
    fun `内部存储`() {
        assertEquals(VolumePath("external_primary", "Music/Pop/"), SafPaths.fromTreeDocumentId("primary:Music/Pop"))
        assertEquals(VolumePath("external_primary", "Music/"), SafPaths.fromTreeDocumentId("primary:Music/"))
    }

    @Test
    fun `内部存储根目录`() {
        assertEquals(VolumePath("external_primary", ""), SafPaths.fromTreeDocumentId("primary:"))
    }

    @Test
    fun `SD 卡的卷名转为小写`() {
        assertEquals(VolumePath("1a2b-3c4d", "Music/"), SafPaths.fromTreeDocumentId("1A2B-3C4D:Music"))
    }

    @Test
    fun `Documents 目录`() {
        assertEquals(VolumePath("external_primary", "Documents/Music/"), SafPaths.fromTreeDocumentId("home:Music"))
        assertEquals(VolumePath("external_primary", "Documents/"), SafPaths.fromTreeDocumentId("home:"))
    }

    @Test
    fun `中文路径`() {
        assertEquals(VolumePath("external_primary", "音乐/周杰伦/"), SafPaths.fromTreeDocumentId("primary:音乐/周杰伦"))
    }

    @Test
    fun `无法映射的 provider 返回 null`() {
        assertNull(SafPaths.fromTreeDocumentId("raw:/storage/emulated/0/Download"))
        assertNull(SafPaths.fromTreeDocumentId("msf:1234"))
        assertNull(SafPaths.fromTreeDocumentId("nocolon"))
    }
}
