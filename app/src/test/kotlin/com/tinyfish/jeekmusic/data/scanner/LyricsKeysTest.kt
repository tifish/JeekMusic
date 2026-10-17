package com.tinyfish.jeekmusic.data.scanner

import org.junit.Assert.assertEquals
import org.junit.Test

class LyricsKeysTest {

    @Test
    fun standardKeysComeFirstInOrder() {
        assertEquals(listOf("LYRICS", "USLT"), TagReader.lyricsKeys(listOf("TITLE", "USLT", "LYRICS")))
    }

    /** TagLib 写入的 USLT 帧带描述 "LYRICS"，读回来是 "LYRICS:LYRICS"。 */
    @Test
    fun describedUsltFramesAreIncluded() {
        assertEquals(listOf("LYRICS:LYRICS"), TagReader.lyricsKeys(listOf("ALBUM", "LYRICS:LYRICS")))
        assertEquals(listOf("LYRICS", "LYRICS:a", "LYRICS:b"), TagReader.lyricsKeys(listOf("LYRICS:b", "LYRICS", "LYRICS:a")))
    }

    @Test
    fun noLyrics() {
        assertEquals(emptyList<String>(), TagReader.lyricsKeys(listOf("TITLE", "ARTIST")))
    }
}
