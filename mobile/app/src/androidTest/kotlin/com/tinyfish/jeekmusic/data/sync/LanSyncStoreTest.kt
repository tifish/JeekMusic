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
