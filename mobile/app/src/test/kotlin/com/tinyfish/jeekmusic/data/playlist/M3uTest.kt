package com.tinyfish.jeekmusic.data.playlist

import org.junit.Assert.assertEquals
import org.junit.Test

class M3uTest {

    @Test
    fun `导出扩展 m3u`() {
        val text = M3u.format(listOf(M3u.Entry("/storage/emulated/0/Music/a.mp3", 215, "周杰伦 - 晴天")))
        assertEquals("#EXTM3U\n#EXTINF:215,周杰伦 - 晴天\n/storage/emulated/0/Music/a.mp3\n", text)
    }

    @Test
    fun `解析各种路径写法`() {
        val text = """
            #EXTM3U
            #EXTINF:215,周杰伦 - 晴天
            /storage/emulated/0/Music/周杰伦/晴天.mp3
            ..\Music\许巍\曾经的你.flac
            D:\Music\Queen\Bohemian Rhapsody.mp3
            file:///sdcard/Music/a%20b.mp3

            relative/song.ogg
        """.trimIndent()
        assertEquals(
            listOf(
                listOf("storage", "emulated", "0", "Music", "周杰伦", "晴天.mp3"),
                listOf("Music", "许巍", "曾经的你.flac"),
                listOf("Music", "Queen", "Bohemian Rhapsody.mp3"),
                listOf("sdcard", "Music", "a b.mp3"),
                listOf("relative", "song.ogg"),
            ),
            M3u.parse(text),
        )
    }

    @Test
    fun `存储路径`() {
        assertEquals("/storage/emulated/0/Music/a.mp3", M3u.storagePath("external_primary", "Music/", "a.mp3"))
        assertEquals("/storage/1A2B-3C4D/Music/a.mp3", M3u.storagePath("1a2b-3c4d", "Music/", "a.mp3"))
    }
}
