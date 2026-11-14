package com.tinyfish.jeekmusic.data.sync

import android.net.Uri
import android.graphics.Bitmap
import android.os.SystemClock
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.MainActivity
import dagger.hilt.android.EntryPointAccessors
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.flow.MutableStateFlow
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.Assume.assumeTrue
import java.io.File

/** Production service regressions; emulator-only, preserving its existing settings. */
class LanMappingSaveTest {
    private fun field(name: String)=LanSyncService::class.java.getDeclaredField(name).apply { isAccessible=true }

    @Test fun actualUiBusyPromptAndLocalSave()=runBlocking {
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        val args=InstrumentationRegistry.getArguments()
        assumeTrue(args.getString("mappingSaveUi")=="true")
        check(android.os.Build.HARDWARE in setOf("ranchu","goldfish"))
        val context=instrumentation.targetContext
        check(context.packageName.endsWith(".debug"))
        val service=EntryPointAccessors.fromApplication(context,LanSyncWorker.Dependencies::class.java).lan()
        val mapping=service.store.mappings().single()
        check(mapping.getString("name")=="Mapping save fixture" && !mapping.getBoolean("enabled"))
        val original=service.store.read()
        service.cancel();service.awaitSync()
        val gate=field("gate").get(service) as Mutex
        var gateOwned=false
        val database=field("db").get(service) as com.tinyfish.jeekmusic.data.db.JeekDatabase
        val originalFolderIds=database.folderDao().all().map { it.id }.toSet()
        @Suppress("UNCHECKED_CAST") val status=field("_status").get(service) as MutableStateFlow<LanStatus>
        val oldStatus=status.value
        val ui=instrumentation.uiAutomation
        fun find(value: String): AccessibilityNodeInfo? {
            fun visit(node: AccessibilityNodeInfo): AccessibilityNodeInfo? {
                if(node.viewIdResourceName==value || node.text?.toString()==value || node.contentDescription?.toString()==value)return node
                for(i in 0 until node.childCount)node.getChild(i)?.let { child -> visit(child)?.let { return it } }
                return null
            }
            return ui.rootInActiveWindow?.let(::visit)
        }
        suspend fun node(value: String): AccessibilityNodeInfo=withTimeout(12_000) {
            var found=find(value)
            while(found==null) { delay(50);found=find(value) }
            found
        }
        suspend fun tap(value: String) {
            withTimeout(12_000) {
                while(true) {
                    var target=find(value)
                    while(target?.isClickable==false)target=target.parent
                    if(target?.isEnabled==true && target.performAction(AccessibilityNodeInfo.ACTION_CLICK))break
                    delay(50)
                }
            }
            delay(150)
        }
        fun shot(name: String) {
            SystemClock.sleep(400) // Capture the settled dialog, after its entrance animation.
            val dir=File(context.cacheDir,"mapping-save-ui").apply { mkdirs() }
            val bitmap=ui.takeScreenshot() ?: error("Screenshot unavailable")
            File(dir,"$name.png").outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG,100,it) };bitmap.recycle()
        }
        ActivityScenario.launch(MainActivity::class.java).use {
            try {
                tap("open_settings");tap("settings_group_devices");tap(mapping.getString("name"))
                withTimeout(12_000) { while(find("lan_save_mapping")?.isEnabled!=true)delay(50) }
                shot("ready")
                // Simulate a round taking the gate just after the dialog observed idle.
                gate.lock();gateOwned=true
                val started=SystemClock.elapsedRealtime()
                tap("lan_save_mapping")
                node(context.getString(com.tinyfish.jeekmusic.R.string.lan_mapping_busy))
                val rejectedMs=SystemClock.elapsedRealtime()-started
                assertTrue("Save waited behind the round: $rejectedMs ms",rejectedMs<3000)
                shot("busy-error")
                tap(context.getString(com.tinyfish.jeekmusic.R.string.confirm))
                status.value=oldStatus.copy(running=true)
                node("lan_mapping_busy")
                assertFalse(node("lan_save_mapping").isEnabled)
                shot("busy-disabled")
                // The close button must remain usable while synchronization owns the gate.
                tap(context.getString(com.tinyfish.jeekmusic.R.string.cancel))
                assertNull(find("lan_save_mapping"))
                status.value=oldStatus.copy(running=false);gate.unlock();gateOwned=false
                tap(mapping.getString("name"))
                withTimeout(12_000) { while(find("lan_save_mapping")?.isEnabled!=true)delay(50) }
                // The fixture uses a pre-existing public SAF grant; the PC is deliberately offline
                // now. Saving must only persist local configuration, never await its network.
                val connection=service.store.read().getJSONObject("connection")
                service.store.update { it.put("connection",JSONObject(connection.toString()).put("port",9)) }
                val saveStarted=SystemClock.elapsedRealtime()
                tap("lan_save_mapping")
                withTimeout(3000) { while(find("lan_save_mapping")!=null)delay(25) }
                val savedMs=SystemClock.elapsedRealtime()-saveStarted
                assertEquals(mapping.getString("key"),service.store.mappings().single().getString("key"))
                assertTrue(service.store.mappings().single().getBoolean("enabled"))
                shot("saved-offline")
                println("MAPPING_UI_EVIDENCE busyRejectedMs=$rejectedMs offlineSavedMs=$savedMs")
            } finally {
                if(gateOwned)gate.unlock()
                status.value=oldStatus
                service.cancel();withTimeout(10_000) { service.awaitSync() }
                database.folderDao().delete(database.folderDao().all().filter { it.id !in originalFolderIds && it.treeUri==mapping.getString("treeUri") }.map { it.id })
                service.store.update { saved -> saved.keys().asSequence().toList().forEach(saved::remove);original.keys().forEach { saved.put(it,original.get(it)) } }
            }
        }
    }

    @Test fun saveDoesNotWaitForBusySyncAndDoesNotChangeSettings()=runBlocking {
        check(android.os.Build.HARDWARE in setOf("ranchu","goldfish"))
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        check(context.packageName.endsWith(".debug"))
        val service=EntryPointAccessors.fromApplication(context,LanSyncWorker.Dependencies::class.java).lan()
        val original=service.store.read()
        service.cancel();service.awaitSync()
        service.store.update { it.put("mappings",JSONArray()) }
        val gate=field("gate").get(service) as Mutex
        val oldJob=field("job").get(service)
        val active=Job()
        suspend fun rejectsPromptly() {
            val before=service.store.read().toString()
            withTimeout(2000) {
                try { service.saveMapping(JSONObject(),setOf(""),Uri.EMPTY);fail("Busy save must be rejected") }
                catch (_: LanMappingBusyException) { }
            }
            assertEquals(before,service.store.read().toString())
        }
        try {
            // Covers both waiting behind a running round and the job-before-gate race.
            gate.lock()
            try { rejectsPromptly();assertTrue(gate.isLocked) } finally { gate.unlock() }
            field("job").set(service,active)
            rejectsPromptly()
            assertFalse(gate.isLocked)
            field("job").set(service,null)
            try { service.saveMapping(JSONObject(),setOf(""),Uri.EMPTY);fail("Invalid SAF grant accepted") }
            catch (_: IllegalArgumentException) { }
            assertFalse("Validation errors must release the gate",gate.isLocked)
        } finally {
            active.cancel();field("job").set(service,oldJob)
            service.store.update { saved -> saved.keys().asSequence().toList().forEach(saved::remove);original.keys().forEach { saved.put(it,original.get(it)) } }
        }
    }

    @Test fun runningStateIsVisibleBeforeGateAndClearsWhenCancelled()=runBlocking {
        check(android.os.Build.HARDWARE in setOf("ranchu","goldfish"))
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        check(context.packageName.endsWith(".debug"))
        val service=EntryPointAccessors.fromApplication(context,LanSyncWorker.Dependencies::class.java).lan()
        val original=service.store.read()
        service.cancel();service.awaitSync()
        service.store.update { it.remove("connection");it.put("mappings",JSONArray()) }
        val gate=field("gate").get(service) as Mutex
        gate.lock()
        try {
            service.startScheduled("mapping-save-test")
            withTimeout(2000) { while(!service.status.value.running)delay(10) }
            assertTrue(service.status.value.running)
            service.cancelScheduled()
            withTimeout(2000) { service.awaitSync() }
            assertFalse(service.status.value.running)
        } finally {
            gate.unlock()
            service.store.update { saved -> saved.keys().asSequence().toList().forEach(saved::remove);original.keys().forEach { saved.put(it,original.get(it)) } }
        }
    }
}
