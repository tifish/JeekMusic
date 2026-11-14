package com.tinyfish.jeekmusic.data.sync

import android.graphics.Bitmap
import android.os.SystemClock
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.MainActivity
import com.tinyfish.jeekmusic.R
import dagger.hilt.android.EntryPointAccessors
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import java.util.UUID

/** Emulator-only regressions: no production conflicts, credentials or music are changed. */
class LanConflictPendingTest {
    private val instrumentation=InstrumentationRegistry.getInstrumentation()
    private val context=instrumentation.targetContext
    private fun service(): LanSyncService {
        check(android.os.Build.HARDWARE in setOf("ranchu","goldfish"))
        check(context.packageName.endsWith(".debug"))
        return EntryPointAccessors.fromApplication(context,LanSyncWorker.Dependencies::class.java).lan()
    }
    private fun database(service: LanSyncService)=LanSyncService::class.java.getDeclaredField("fileDatabase").apply { isAccessible=true }.get(service) as LanFileDatabase
    private fun row(mapping: String,index: Int)=JSONObject().put("id",UUID.randomUUID().toString()).put("mapping",mapping).put("path","Pending demo $index.mp3")
        .put("localVersion","m2:65:1793799853318915800").put("remoteVersion","m2:64:1793799853188764700")
    private fun restore(service: LanSyncService,original: JSONObject) {
        service.store.update { saved -> saved.keys().asSequence().toList().forEach(saved::remove);original.keys().forEach { saved.put(it,original.get(it)) } }
    }

    @Test fun unfinishedChoicesRetainDirtyRetryWithBackoff()=runBlocking {
        val service=service();val db=database(service);val original=service.store.read()
        service.cancel();service.awaitSync()
        val key="pending-retry-${UUID.randomUUID()}";val row=row(key,1)
        try {
            // A current mapping whose PC is offline. No local directory is opened.
            val mapping=JSONObject().put("layoutVersion",2).put("key",key).put("rootId","0".repeat(32)).put("enabled",true)
            service.store.update { it.put("connection",JSONObject().put("host","127.0.0.1").put("port",9)).put("mappings",JSONArray(listOf(mapping))).put("autoCancelled",false) }
            db.issue(row);db.queueChoice(row.getString("id"),"remote")
            service.startScheduled("pending-choice-test")
            withTimeout(10_000) { service.awaitSync() }
            assertEquals(1,service.pendingConflictCount.value)
            assertTrue(service.schedule.value.dirty)
            assertFalse(service.schedule.value.running)
            assertTrue("Unfinished choice must retain bounded retry",service.schedule.value.backoffUntil>System.currentTimeMillis())
            assertEquals("remote",db.issue(row.getString("id"))!!.getString("queuedChoice"))
        } finally {
            service.cancel();service.awaitSync();db.resolved(key,row.getString("path"));restore(service,original)
        }
    }

    @Test fun actualUiThreeChoicesPartialCompletionAndReopen()=runBlocking {
        assumeTrue(InstrumentationRegistry.getArguments().getString("conflictPendingUi")=="true")
        val service=service();val db=database(service);val original=service.store.read()
        service.cancel();service.awaitSync()
        check(db.issueCount()==0) { "Visual fixture requires an empty debug conflict list" }
        service.store.update { it.put("mappings",JSONArray());it.remove("connection") }
        val key="pending-ui-${UUID.randomUUID()}";val rows=(1..3).map { row(key,it) }
        val ui=instrumentation.uiAutomation
        fun find(predicate: (AccessibilityNodeInfo)->Boolean): AccessibilityNodeInfo? {
            fun visit(node: AccessibilityNodeInfo): AccessibilityNodeInfo? {
                if(predicate(node))return node
                for(i in 0 until node.childCount)node.getChild(i)?.let { child -> visit(child)?.let { return it } }
                return null
            }
            return ui.rootInActiveWindow?.let(::visit)
        }
        fun find(value: String)=find { it.viewIdResourceName==value || it.text?.toString()==value || it.contentDescription?.toString()==value }
        suspend fun node(value: String): AccessibilityNodeInfo=withTimeout(12_000) {
            var result=find(value)
            while(result==null) { delay(50);result=find(value) }
            result
        }
        suspend fun tap(value: String) {
            withTimeout(12_000) {
                while(true) {
                    var target=find(value)
                    while(target?.isClickable==false)target=target.parent
                    if(target?.isEnabled==true && target.performAction(AccessibilityNodeInfo.ACTION_CLICK))break
                    if(value==context.getString(R.string.lan_use_pc))find { it.isScrollable }?.performAction(AccessibilityNodeInfo.ACTION_SCROLL_FORWARD)
                    delay(100)
                }
            }
            delay(250)
        }
        fun shot(name: String) {
            SystemClock.sleep(400)
            val dir=File(context.cacheDir,"conflict-pending-ui").apply { mkdirs() }
            val bitmap=ui.takeScreenshot() ?: error("Screenshot unavailable")
            File(dir,"$name.png").outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG,100,it) };bitmap.recycle()
        }
        suspend fun top() {
            repeat(8) { find { it.isScrollable }?.performAction(AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD);delay(100) }
        }
        rows.forEach(db::issue)
        ActivityScenario.launch(MainActivity::class.java).use {
            try {
                node(context.getString(R.string.sync_conflict_notice,3));shot("three-unselected")
                tap("sync_conflict_notice")
                repeat(3) { index ->
                    withTimeout(12_000) {
                        while(find(context.getString(R.string.lan_use_pc))==null) {
                            find { it.isScrollable }?.performAction(AccessibilityNodeInfo.ACTION_SCROLL_FORWARD);delay(100)
                        }
                    }
                    tap(context.getString(R.string.lan_use_pc))
                    withTimeout(5000) { while(service.pendingConflictCount.value!=index+1)delay(25) }
                    if(index==0) { top();node(context.getString(R.string.sync_conflict_mixed_notice,2,1));shot("mixed") }
                }
                top();node(context.getString(R.string.sync_conflict_waiting_notice,3));shot("three-selected")
                assertNull(find(context.getString(R.string.lan_use_pc)))
                assertTrue(node("sync_conflict_resume").isEnabled)
                tap("sync_conflict_resume") // Remains safe and responsive with no configured PC.
                db.resolved(key,rows.first().getString("path"))
                db.updateIssue(JSONObject(rows[1].toString()).put("choice","remote"))
                node(context.getString(R.string.sync_conflict_waiting_notice,2));shot("two-waiting")
                tap(context.getString(R.string.sync_conflict_back))
                node(context.getString(R.string.sync_conflict_waiting_notice,2));shot("main-two-waiting")
                tap("sync_conflict_notice")
                node(context.getString(R.string.sync_conflict_waiting_notice,2))
                node(context.getString(R.string.sync_conflict_pending,context.getString(R.string.lan_use_pc)))
                assertTrue(node("sync_conflict_resume").isEnabled);shot("reopened-two-waiting")
                rows.drop(1).forEach { db.resolved(key,it.getString("path")) }
                node(context.getString(R.string.sync_conflict_empty));shot("completed-open-page")
                tap(context.getString(R.string.sync_conflict_back));delay(300)
                assertNull(find("sync_conflict_notice"));shot("main-completed")
            } finally {
                service.cancel();service.awaitSync();rows.forEach { db.resolved(key,it.getString("path")) };restore(service,original)
            }
        }
    }
}
