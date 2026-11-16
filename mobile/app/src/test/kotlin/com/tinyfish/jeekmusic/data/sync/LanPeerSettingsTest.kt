package com.tinyfish.jeekmusic.data.sync

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class LanPeerSettingsTest {
    private fun connection(pin: Char, port: Int = 39843) = JSONObject().put("host", "pc-$pin.local").put("port", port)
        .put("pin", pin.toString().repeat(64)).put("deviceId", "phone-a").put("token", "synthetic-$pin").put("name", "PC $pin")
    private fun migrated() = LanPeerSettings.migrate(JSONObject().put("connection", connection('a')).put("autoCancelled", true)
        .put("mappings", JSONArray().put(JSONObject().put("rootId", "root-a").put("key", "original-key").put("scope", "original-scope")
            .put("directoryIdentity", "original-directory").put("deltaCursor", "original-cursor").put("treeUri", "content://original"))))

    @Test fun migrationKeepsCredentialMappingTransactionReferencesAndCancellation() {
        val state = migrated()
        val peer = LanPeerSettings.rows(state).single()
        assertEquals("synthetic-a", peer.getString("token"))
        assertEquals("a".repeat(64), state.getString("primaryPeerId"))
        assertEquals(state.getString("primaryPeerId"), state.getString("selectedPeerId"))
        val mapping = state.getJSONArray("mappings").getJSONObject(0)
        assertEquals(peer.getString("peerId"), mapping.getString("peerId"))
        for ((field, expected) in mapOf("key" to "original-key", "scope" to "original-scope", "directoryIdentity" to "original-directory", "deltaCursor" to "original-cursor", "treeUri" to "content://original"))
            assertEquals(expected, mapping.getString(field))
        assertTrue(state.getBoolean("autoCancelled"))
        assertEquals(state.toString(), LanPeerSettings.migrate(state).toString())
    }

    @Test fun addingSecondComputerDoesNotReplacePrimaryOrMutateFirstPeer() {
        val state = migrated()
        val primaryBefore = state.getJSONObject("connection").toString()
        val second = LanPeerSettings.add(state, connection('b'))
        assertEquals(2, LanPeerSettings.rows(state).size)
        assertEquals(second, state.getString("selectedPeerId"))
        assertEquals(primaryBefore, state.getJSONObject("connection").toString())
        assertEquals("a".repeat(64), state.getJSONArray("mappings").getJSONObject(0).getString("peerId"))
    }

    @Test fun repairingSameFingerprintChangesEndpointWithoutDuplicatingPeerOrLosingSchedule() {
        val state = migrated()
        LanPeerSettings.rows(state).single().put("schedule", JSONObject().put("cancelled", true).put("backoffUntil", 123))
        LanPeerSettings.add(state, connection('a', 40000).put("token", "new-credential"))
        val peer = LanPeerSettings.rows(state).single()
        assertEquals(40000, peer.getInt("port"))
        assertEquals("new-credential", peer.getString("token"))
        assertTrue(peer.getJSONObject("schedule").getBoolean("cancelled"))
        assertEquals("original-key", state.getJSONArray("mappings").getJSONObject(0).getString("key"))
    }

    @Test fun sameRootIdOnDifferentComputersIsDifferentMapping() {
        val a = JSONObject().put("peerId", "peer-a").put("rootId", "same-root")
        val b = JSONObject().put("peerId", "peer-b").put("rootId", "same-root")
        assertFalse(LanPeerSettings.sameMapping(a, b))
        assertTrue(LanPeerSettings.sameMapping(a, JSONObject(a.toString())))
        val managed = JSONObject(a.toString()).put("layoutVersion", 2).put("volume", "external_primary").put("relative", "Music/A/")
        assertThrows(IllegalArgumentException::class.java) { LanMappingTargets.requireSeparate(listOf(managed), "same-root", "external_primary", "Music/A/", "peer-b") }
        LanMappingTargets.requireSeparate(listOf(managed), "same-root", "external_primary", "Music/B/", "peer-b")
    }

    @Test fun compatibilityAliasChangesOnlyThePrimaryComputer() {
        val state = migrated()
        LanPeerSettings.add(state, connection('b'))
        state.getJSONObject("connection").put("port", 9)
        LanPeerSettings.importLegacyConnection(state); LanPeerSettings.normalize(state)
        assertEquals(9, LanPeerSettings.rows(state).single { it.getString("peerId") == "a".repeat(64) }.getInt("port"))
        assertEquals(39843, LanPeerSettings.rows(state).single { it.getString("peerId") == "b".repeat(64) }.getInt("port"))
    }

    @Test fun removingPrimaryNeverElectsAnotherPersonalDataDestination() {
        val state = migrated()
        val second = LanPeerSettings.add(state, connection('b'))
        state.put("peers", JSONArray(LanPeerSettings.rows(state).filter { it.getString("peerId") == second })).put("primaryPeerId", "")
        LanPeerSettings.normalize(state)
        assertFalse(state.has("connection"))
        assertEquals(second, state.getString("selectedPeerId"))
    }

    @Test fun invalidOrFutureSchemasFailClosed() {
        assertThrows(IllegalArgumentException::class.java) { LanPeerSettings.migrate(JSONObject().put("schemaVersion", 3)) }
        assertThrows(IllegalArgumentException::class.java) { LanPeerSettings.migrate(JSONObject().put("schemaVersion", 2)) }
        val state = migrated()
        state.getJSONArray("peers").put(JSONObject(state.getJSONArray("peers").getJSONObject(0).toString()))
        assertThrows(IllegalArgumentException::class.java) { LanPeerSettings.migrate(state) }
    }
    @Test fun missingLegacyCredentialNeverAttachesOldMappingsToTheNextComputer() {
        val state=LanPeerSettings.migrate(JSONObject().put("mappings",JSONArray().put(JSONObject().put("rootId","old-root"))))
        LanPeerSettings.add(state,connection('b'))
        assertEquals("unbound",state.getJSONArray("mappings").getJSONObject(0).getString("peerId"))
    }
}
