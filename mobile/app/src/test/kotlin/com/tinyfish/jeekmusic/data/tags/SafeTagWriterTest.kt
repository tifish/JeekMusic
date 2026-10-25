package com.tinyfish.jeekmusic.data.tags

import org.junit.Assert.*
import org.junit.Test
import java.io.File

/** Structural regression checks supplement (not replace) the actual Android/PCM suite. */
class SafeTagWriterTest {
    private val assets = File("src/androidTest/assets/corpus")
    private val lyric = File(assets, "lyrics-after.lrc").readText()
    private val cover = File(assets, "front.jpg").readBytes()

    @Test fun sharedNativeCorpus() {
        for (name in listOf("id3v23.mp3", "id3v24.mp3", "untagged.mp3", "native.flac", "native.ogg", "native.opus", "native.m4a", "untagged.m4a") +
            listOf("private-v23.mp3", "private-v24.mp3", "private.flac", "private.ogg", "private.opus", "faststart-padded.m4a", "large.ogg", "large.opus").map { "../boundaries/$it" }) {
            for (change in listOf(TagEdit(cover = cover), TagEdit(lyrics = lyric), TagEdit(cover, lyric))) {
                withFile(name) { file ->
                    val before = file.readBytes()
                    SafeTagWriter.write(file, change)
                    val once = file.readBytes()
                    SafeTagWriter.write(file, change)
                    assertArrayEquals("Idempotent write: $name", once, file.readBytes())
                    if (name.endsWith("mp3")) {
                        assertEquals(if (name == "id3v23.mp3" || "private-v23" in name) 3 else 4, once[3].toInt())
                        val hadV1 = hasV1(before)
                        assertEquals(hadV1, hasV1(once))
                        if (hadV1) assertArrayEquals(before.takeLast(128).toByteArray(), once.takeLast(128).toByteArray())
                    }
                }
            }
        }
    }
    @Test fun nullTargetsDoNothingIncludingMalformedFiles() = withFile("native.wav") { file ->
        val before = file.readBytes()
        SafeTagWriter.write(file, TagEdit())
        assertArrayEquals(before, file.readBytes())
    }
    @Test fun unsupportedAndAmbiguousInputsAreUnchanged() {
        for (name in listOf("multiple-lyrics.mp3", "native.wav", "native.aiff", "native.wma", "native.wv", "faststart.m4a", "../boundaries/unsafe-faststart.m4a", "../boundaries/unsafe-chained.ogg", "../boundaries/ambiguous-sylt.mp3", "../boundaries/unsafe-chapters.mp3", "../boundaries/unsafe-cenc.m4a", "../boundaries/unsafe-saio.m4a", "../boundaries/private.m4a")) {
            withFile(name) { file ->
                val before = file.readBytes()
                assertTrue("Must refuse $name", runCatching { SafeTagWriter.write(file, TagEdit(cover, lyric)) }.isFailure)
                assertArrayEquals(name, before, file.readBytes())
            }
        }
    }
    @Test fun coverPreservesMultipleLyricSlots() = withFile("multiple-lyrics.mp3") { file ->
        SafeTagWriter.write(file, TagEdit(cover = cover))
    }
    @Test fun exactTextAndVersionEncoding() {
        val text = "[offset:-123]\r\n\r\n[00:01.234]中文? Café Ελληνικά العربية 𠮷😀\r\n[00:01.234]Translation\r\n"
        for (name in listOf("id3v23.mp3", "id3v24.mp3", "untagged.mp3")) withFile(name) { file ->
            SafeTagWriter.write(file, TagEdit(lyrics = text))
            val bytes = file.readBytes()
            val body = id3Frames(bytes).single { it.first == "USLT" }.second
            assertEquals(if (name == "id3v23.mp3") 1 else 3, body[0].toInt())
        }
    }
    @Test fun allPrivateMultivalueAndDateFramesRemainByteExact() = withFile("id3v23.mp3") { file ->
        val before = id3Frames(file.readBytes()).filter { it.first !in setOf("APIC", "USLT") }
        SafeTagWriter.write(file, TagEdit(cover, lyric))
        val after = id3Frames(file.readBytes()).filter { it.first !in setOf("APIC", "USLT") }
        assertEquals(before.map { it.first }, after.map { it.first })
        before.zip(after).forEach { (a, b) -> assertArrayEquals(a.first, a.second, b.second) }
    }
    @Test fun invalidUnicodeIsRejectedBeforeWriting() = withFile("id3v24.mp3") { file ->
        val before = file.readBytes()
        assertTrue(runCatching { SafeTagWriter.write(file, TagEdit(lyrics = "broken\uD800")) }.isFailure)
        assertArrayEquals(before, file.readBytes())
    }
    @Test fun unsafeId3HeadersAndStackedTagsAreRefused() = withFile("id3v24.mp3") { file ->
        val original = file.readBytes()
        for (flag in listOf(0x80, 0x40, 0x10)) {
            val bytes = original.clone().also { it[5] = flag.toByte() }
            file.writeBytes(bytes)
            assertTrue(runCatching { SafeTagWriter.write(file, TagEdit(cover, lyric)) }.isFailure)
            assertArrayEquals(bytes, file.readBytes())
        }
        val end = 10 + readSync(original, 6)
        val stacked = original.copyOfRange(0, end) + original
        file.writeBytes(stacked)
        assertTrue(runCatching { SafeTagWriter.write(file, TagEdit(cover, lyric)) }.isFailure)
        assertArrayEquals(stacked, file.readBytes())
    }
    @Test fun structuralLocationsAndInvalidatedUnknownFramesAreRefused() {
        val names = listOf("unsafe-alter-v23.mp3", "unsafe-alter-v24.mp3") +
            listOf("udta", "movie", "top").flatMap { listOf("unsafe-iloc-$it.m4a", "unsafe-uuid-$it.m4a") }
        for (name in names) for (change in listOf(TagEdit(cover = cover), TagEdit(lyrics = lyric), TagEdit(cover, lyric))) {
            withFile("../review-boundaries/$name") { file ->
                val before = file.readBytes()
                assertTrue("Must refuse $name", runCatching { SafeTagWriter.write(file, change) }.isFailure)
                assertArrayEquals(name, before, file.readBytes())
            }
        }
    }
    @Test fun opaqueMp4PayloadIsNotSearchedForBoxNames() = withFile("../review-boundaries/safe-opaque.m4a") { file ->
        SafeTagWriter.write(file, TagEdit(cover, lyric))
    }
    private fun hasV1(bytes: ByteArray) = bytes.size >= 128 && bytes.copyOfRange(bytes.size - 128, bytes.size - 125).contentEquals(ascii("TAG"))
    private fun id3Frames(bytes: ByteArray): List<Pair<String, ByteArray>> {
        var p = 10
        val end = 10 + readSync(bytes, 6)
        val output = ArrayList<Pair<String, ByteArray>>()
        while (p < end && bytes[p] != 0.toByte()) {
            val size = if (bytes[3] == 4.toByte()) readSync(bytes, p + 4) else u32(bytes, p + 4).toInt()
            output += String(bytes, p, 4, Charsets.US_ASCII) to bytes.copyOfRange(p + 10, p + 10 + size)
            p += 10 + size
        }
        return output
    }
    private fun withFile(name: String, action: (File) -> Unit) {
        val file = File.createTempFile("jeek-tags-", ".${name.substringAfterLast('.')}")
        try { File(assets, name).copyTo(file, overwrite = true); action(file) } finally { file.delete() }
    }
}
