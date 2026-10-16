package com.tinyfish.jeekmusic.data.backup

import com.tinyfish.jeekmusic.data.backup.BackupRefs.Root
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class BackupRefsTest {

    private val roots = listOf(Root("external_primary", "Music/"), Root("1a2b-3c4d", "Songs/Classic/"))

    @Test
    fun `相对于所属音乐文件夹的路径`() {
        assertEquals("周杰伦/晴天.mp3", BackupRefs.refOf("external_primary:Music/周杰伦/晴天.mp3", roots))
        assertEquals("Live.flac#03", BackupRefs.refOf("1a2b-3c4d:Songs/Classic/Live.flac#03", roots))
    }

    @Test
    fun `不属于任何音乐文件夹`() {
        assertNull(BackupRefs.refOf("external_primary:Download/a.mp3", roots))
        assertNull(BackupRefs.refOf("no-colon", roots))
    }

    @Test
    fun `嵌套时取最深的根`() {
        val nested = listOf(Root("v", "Music/"), Root("v", "Music/Pop/"))
        assertEquals("a.mp3", BackupRefs.refOf("v:Music/Pop/a.mp3", nested))
    }

    @Test
    fun `完全匹配优先`() {
        val matcher = BackupRefs.Matcher(mapOf("周杰伦/晴天.mp3" to "k1", "Other/晴天.mp3" to "k2"))
        assertEquals("k1", matcher.match("周杰伦/晴天.mp3"))
    }

    @Test
    fun `层级不同时按后缀匹配`() {
        // 新设备上把整个 Music 拷进了 Backup/Music
        val matcher = BackupRefs.Matcher(mapOf("Music/周杰伦/晴天.mp3" to "k1", "Music/许巍/曾经的你.mp3" to "k2"))
        assertEquals("k1", matcher.match("周杰伦/晴天.mp3"))
        assertEquals("k2", matcher.match("曾经的你.mp3"))
    }

    @Test
    fun `同名文件有歧义时取后缀最长的唯一一个，否则放弃`() {
        val matcher = BackupRefs.Matcher(mapOf("A/Live/01.mp3" to "k1", "B/Studio/01.mp3" to "k2"))
        assertEquals("k1", matcher.match("Live/01.mp3"))
        assertNull(matcher.match("C/01.mp3"))
        assertNull(matcher.match("没有这个.mp3"))
    }
}
