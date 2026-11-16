package com.tinyfish.jeekmusic.data.sync

import android.content.ContextWrapper
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class LanSyncStoreTest {
    private fun context() = object : ContextWrapper(InstrumentationRegistry.getInstrumentation().targetContext) {
        private val isolated = File(super.getFilesDir(), "lan-store-tests/${UUID.randomUUID()}").apply { mkdirs() }
        override fun getFilesDir(): File = isolated
    }

    @Test fun persistedLegacyMigrationKeepsOriginalBackupAndTwoPeersAcrossRestart() {
        val context = context()
        val pin = "a".repeat(64)
        val original = JSONObject().put("connection", JSONObject().put("pin", pin).put("token", "synthetic-original").put("host", "localhost").put("port", 10))
            .put("mappings", JSONArray().put(JSONObject().put("rootId", "same").put("key", "original-key").put("scope", "original-scope")))
        File(context.filesDir, "lan-sync.json").writeText(original.toString())
        val store = LanSyncStore(context, LanSyncLedger(context))
        assertEquals(original.toString(), File(context.filesDir, "lan-sync-v1.json").readText())
        store.addPeer(JSONObject().put("pin", "b".repeat(64)).put("token", "synthetic-second").put("host", "localhost").put("port", 20))
        store.saveMapping(JSONObject().put("peerId", "b".repeat(64)).put("rootId", "same").put("key", "second-key"))
        val restarted = LanSyncStore(context, LanSyncLedger(context))
        assertEquals(2, restarted.peers().size)
        assertEquals(2, restarted.mappings().size)
        assertEquals("original-key", restarted.mappings()[0].getString("key"))
        assertEquals("original-scope", restarted.mappings()[0].getString("scope"))
        assertEquals("synthetic-original", restarted.read().getJSONObject("connection").getString("token"))
        restarted.forgetPeer(pin)
        assertFalse(restarted.read().has("connection"))
        assertEquals("second-key", restarted.mappings().single().getString("key"))
        assertEquals(1, LanSyncStore(context, LanSyncLedger(context)).peers().size)
    }

    @Test fun malformedSettingsAreNotSilentlyReplacedWithEmptyConfiguration() {
        val context = context()
        val file = File(context.filesDir, "lan-sync.json")
        file.writeText("{broken")
        assertThrows(org.json.JSONException::class.java) { LanSyncStore(context, LanSyncLedger(context)) }
        assertEquals("{broken", file.readText())
    }

    @Test fun togglingMappingsKeepsOrderAndOtherFoldersUnchangedAcrossRestart() {
        val context = context()
        val store = LanSyncStore(context, LanSyncLedger(context))
        val roots = listOf("A", "B", "C")
        for (root in roots) store.saveMapping(JSONObject().put("rootId", root).put("enabled", false))
        val enabled = roots.associateWith { false }.toMutableMap()
        for (root in listOf("A", "A", "B", "C", "B", "C")) {
            enabled[root] = !enabled.getValue(root)
            store.saveMapping(store.mappings().single { it.getString("rootId") == root }.put("enabled", enabled[root]))
            for (snapshot in listOf(store, LanSyncStore(context, LanSyncLedger(context)))) {
                assertEquals("Updating $root must not move its row", roots, snapshot.mappings().map { it.getString("rootId") })
                assertEquals(enabled, snapshot.mappings().associate { it.getString("rootId") to it.getBoolean("enabled") })
            }
        }
    }

    @Test fun editingAnExistingMappingKeepsItsPositionAndNewMappingsAppend() {
        val context = context()
        val store = LanSyncStore(context, LanSyncLedger(context))
        for (root in listOf("A", "B")) store.saveMapping(JSONObject().put("rootId", root).put("name", root))
        store.saveMapping(JSONObject().put("rootId", "A").put("name", "Renamed A").put("selection", JSONArray(listOf("Music"))))
        store.saveMapping(JSONObject().put("rootId", "C").put("name", "C"))
        val mappings = LanSyncStore(context, LanSyncLedger(context)).mappings()
        assertEquals(listOf("A", "B", "C"), mappings.map { it.getString("rootId") })
        assertEquals("Renamed A", mappings[0].getString("name"))
        assertEquals("Music", mappings[0].getJSONArray("selection").getString(0))
        assertEquals("B", mappings[1].getString("name"))
    }
}
