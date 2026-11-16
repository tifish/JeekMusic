package com.tinyfish.jeekmusic.data.sync

import android.graphics.Bitmap
import android.util.Base64
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.MainActivity
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.PlaylistEntity
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Host harness snapshots and restores private emulator data while the client is stopped. */
@RunWith(AndroidJUnit4::class)
class LanGroupTest {
    @Test fun isolatedGroupPhase()=runBlocking {
        check(android.os.Build.HARDWARE in setOf("ranchu","goldfish"))
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        val raw=InstrumentationRegistry.getArguments().getString("jeekGroupFixture") ?: error("Explicit isolated fixture required")
        val config=JSONObject(String(Base64.decode(raw,Base64.NO_WRAP),Charsets.UTF_8));check(config.getBoolean("isolated"))
        val lan=EntryPointAccessors.fromApplication(instrumentation.targetContext,LanSyncWorker.Dependencies::class.java).lan()
        val db=lan.db;val sync=lan.sync
        val ui=instrumentation.uiAutomation
        fun find(tag: String): AccessibilityNodeInfo? {
            fun visit(node: AccessibilityNodeInfo): AccessibilityNodeInfo? {
                if(node.viewIdResourceName==tag || node.viewIdResourceName?.endsWith("/$tag")==true)return node
                repeat(node.childCount) { node.getChild(it)?.let { child -> visit(child)?.let { found -> return found } } }
                return null
            }
            return ui.rootInActiveWindow?.let(::visit)
        }
        suspend fun tap(tag: String) {
            try { withTimeout(20_000) {
                while(true) {
                    fun dismissUpdate(node: AccessibilityNodeInfo): Boolean {
                        if(node.text?.toString()==instrumentation.targetContext.getString(com.tinyfish.jeekmusic.R.string.update_later)) {
                            var target: AccessibilityNodeInfo?=node
                            while(target?.isClickable==false)target=target.parent
                            return target?.performAction(AccessibilityNodeInfo.ACTION_CLICK)==true
                        }
                        repeat(node.childCount) { node.getChild(it)?.let { child -> if(dismissUpdate(child))return true } };return false
                    }
                    ui.rootInActiveWindow?.let(::dismissUpdate)
                    var node=find(tag)
                    while(node?.isClickable==false)node=node.parent
                    if(node?.isEnabled==true && node.isVisibleToUser && node.performAction(AccessibilityNodeInfo.ACTION_CLICK))break
                    val metrics=instrumentation.targetContext.resources.displayMetrics
                    val x=metrics.widthPixels/2;val from=(metrics.heightPixels*0.8).toInt();val to=(metrics.heightPixels*0.65).toInt()
                    android.os.ParcelFileDescriptor.AutoCloseInputStream(ui.executeShellCommand("input swipe $x $from $x $to 600")).use { it.readBytes() }
                    delay(200)
                }
            } } catch(error: TimeoutCancellationException) { throw IllegalStateException("UI target timed out: $tag",error) }
            delay(300)
        }
        fun screenshot(name: String) {
            val output=File(instrumentation.targetContext.cacheDir,"group-ui").apply { mkdirs() }
            val bitmap=ui.takeScreenshot() ?: error("Screenshot unavailable")
            File(output,"$name.png").outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG,100,it) };bitmap.recycle()
        }
        val scenario=ActivityScenario.launch(MainActivity::class.java)
        try {
            lan.cancel();lan.awaitSync()
            try { withTimeout(20_000) { while(lan.networkPolicy.waitingForWifi)delay(100) } }
            catch(error: TimeoutCancellationException) { throw IllegalStateException("Emulator Wi-Fi policy did not become ready",error) }
            if(config.has("invitation")) {
                assertFalse(sync.groupStatus().optBoolean("joined"))
                val invitation=config.getString("invitation");assertEquals("127.0.0.1",LanInvitation.parse(invitation).host)
                lan.store.update(false) { state ->
                    state.put("peers",JSONArray()).put("primaryPeerId","").put("selectedPeerId","").put("mappings",JSONArray()).put("autoCancelled",true)
                    state.remove("connection");state.remove("manualPending");state.remove("manualPeerId")
                }
                lan.pair(invitation)
                run {
                    tap("open_settings");tap("settings_group_devices");tap("sync_group_join")
                    withTimeout(30_000) { while(!sync.groupStatus().optBoolean("joined"))delay(100) }
                    withTimeout(15_000) { while(find("sync_group_status")==null)delay(100) }
                    find("sync_group_status")?.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_SHOW_ON_SCREEN.id);delay(300)
                    screenshot("joined")
                }
            }
            config.optString("createName").takeIf { it.isNotEmpty() }?.let { name ->
                val now=System.currentTimeMillis();db.playlistDao().insert(PlaylistEntity(name=name,createdAt=now,updatedAt=now))
            }
            if(config.optBoolean("clearLearning"))lan.preferences.delete()
            config.optString("renameFrom").takeIf { it.isNotEmpty() }?.let { old ->
                val playlist=db.playlistDao().all().single { it.name==old };db.playlistDao().rename(playlist.id,config.getString("renameTo"),System.currentTimeMillis())
            }
            sync.syncNow(directoryRequested=false)
            if(config.optBoolean("offline")) {
                assertTrue(sync.groupStatus().getLong("pendingUploads")>0)
                assertTrue(runCatching { lan.synchronizeGroup() }.isFailure)
            } else {
                lan.synchronizeGroup()
                val state=sync.groupStatus();assertEquals(0L,state.getLong("pendingUploads"))
                assertTrue(lan.groups.status.value.optString("lastError").isEmpty())
            }
            val names=db.playlistDao().all().map { it.name }.toSet()
            config.optString("expectedLearningGeneration").takeIf { it.isNotEmpty() }?.let { expected ->
                assertEquals(expected,JSONObject(lan.preferences.export()).getString("generation"))
            }
            for(field in listOf("expectedNames","absentNames"))config.optJSONArray(field)?.let { array ->
                repeat(array.length()) { index -> assertEquals(array.getString(index),field=="expectedNames",array.getString(index) in names) }
            }
            println("GROUP_EVIDENCE phasePassed=true noFileMapping=${lan.store.mappings().isEmpty()}")
        } catch(error: Throwable) { screenshot("failure");throw error }
        finally { scenario.close() }
    }
}
