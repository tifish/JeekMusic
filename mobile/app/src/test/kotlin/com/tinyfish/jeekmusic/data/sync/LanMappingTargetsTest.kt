package com.tinyfish.jeekmusic.data.sync

import org.json.JSONObject
import org.junit.Assert.assertThrows
import org.junit.Test

class LanMappingTargetsTest {
    private fun mapping(root: String, relative: String, volume: String = "external_primary") =
        JSONObject().put("rootId", root).put("volume", volume).put("relative", relative)

    @Test fun validSiblingDestinationsNeedNoManualDeclaration() {
        val current = listOf(mapping("a", "Music/JeekMusic-a/"))
        LanMappingTargets.requireSeparate(current, "b", "external_primary", "Music/")
        LanMappingTargets.requireSeparate(current, "b", "sd_card", "Music/JeekMusic-a/")
        // Existing task edits retain their own namespace; no consent flag is stored or supplied.
        LanMappingTargets.requireSeparate(current, "a", "external_primary", "Music/")
    }

    @Test fun nestedDestinationStillRejectedWithoutAnyDeclarationFlag() {
        val current = listOf(mapping("a", "Music/JeekMusic-a/"))
        assertThrows(IllegalArgumentException::class.java) {
            LanMappingTargets.requireSeparate(current, "b", "external_primary", "Music/JeekMusic-a/Child/")
        }
    }

    @Test fun enclosingDestinationAndCaseVariantStillRejected() {
        val current = listOf(mapping("a", "MUSIC/JeekMusic-b/Child/JeekMusic-a/"))
        assertThrows(IllegalArgumentException::class.java) {
            LanMappingTargets.requireSeparate(current, "b", "external_primary", "Music/")
        }
    }
}
