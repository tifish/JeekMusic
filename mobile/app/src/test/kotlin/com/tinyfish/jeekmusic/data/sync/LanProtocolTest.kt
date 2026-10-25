package com.tinyfish.jeekmusic.data.sync

import com.tinyfish.jeekmusic.data.backup.BackupRefs
import org.junit.Assert.*
import org.junit.Test
import org.json.JSONObject
import org.json.JSONArray

class LanProtocolTest {
    @Test fun replacingCueBindingsRefreshesOldAndNewAudioAndRetainsOtherCueOwners() {
        val bindings = JSONObject().put("Shared/A.wav", JSONArray(listOf("Selected/Album.cue", "Other/Keep.cue")))
        val affected = LanBindings.replace(bindings, "Selected/Album.cue", listOf("Shared/B.wav"))
        assertEquals(setOf("Shared/A.wav", "Shared/B.wav"), affected)
        assertEquals("[\"Other/Keep.cue\"]", bindings.getJSONArray("Shared/A.wav").toString())
        assertEquals("[\"Selected/Album.cue\"]", bindings.getJSONArray("Shared/B.wav").toString())
        LanBindings.replace(bindings, "Selected/Album.cue", listOf("Shared/B.wav"))
        assertEquals(1, bindings.getJSONArray("Shared/B.wav").length())
        LanBindings.replace(bindings, "Other/Keep.cue", emptyList())
        assertFalse(bindings.has("Shared/A.wav"))
    }
    @Test fun rejectsUnsafePortablePathsBeforeAnyDocumentAccess() {
        for (path in listOf("../secret", "/absolute", "C:/disk", "a\\b", "a//b", "a/../b", "NUL.wav", "CON.x", "x./a", ".jeekmusic/sync", "x /a")) {
            assertTrue(path, runCatching { LanProtocol.relative(path) }.isFailure)
        }
        assertEquals("艺术家/Album/song.flac", LanProtocol.relative("艺术家/Album/song.flac"))
        assertEquals("", LanProtocol.relative("", root = true))
    }
    @Test fun rootIdentityNeverFallsBackToUnrelatedPartialDownload() {
        val matcher = BackupRefs.Matcher(mapOf("root:one/Karajan/song.wav" to "one", "ordinary/song.wav" to "ordinary"))
        assertNull(matcher.match("root:two/Karajan/song.wav"))
        assertEquals("one", matcher.match("root:one/Karajan/song.wav"))
        assertEquals("ordinary", matcher.match("song.wav"))
        assertNull(BackupRefs.Matcher(mapOf("root:one/A/song.wav" to "one")).match("song.wav"))
    }
}
