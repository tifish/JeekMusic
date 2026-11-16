package com.tinyfish.jeekmusic.data.sync

import android.graphics.Bitmap
import android.util.Base64
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.MainActivity
import com.tinyfish.jeekmusic.analysis.complete
import com.tinyfish.jeekmusic.data.library.*
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
        suspend fun metadataSync() {
            for(attempt in 0..2) {
                try { lan.synchronizeGroup();return }
                catch(error: java.io.IOException) {
                    if(error !is javax.net.ssl.SSLException && error !is java.net.SocketException && error !is java.net.SocketTimeoutException && error !is java.io.EOFException || attempt==2)throw error
                    // Multiple real devices share one loopback source through adb reverse.
                    // Keep production admission limits; retry the idempotent metadata round.
                    delay(1000L*(attempt+1))
                }
            }
        }
        val ui=instrumentation.uiAutomation
        fun find(tag: String): AccessibilityNodeInfo? {
            fun visit(node: AccessibilityNodeInfo): AccessibilityNodeInfo? {
                if(node.isVisibleToUser && (node.viewIdResourceName==tag || node.viewIdResourceName?.endsWith("/$tag")==true))return node
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
                    if(ui.rootInActiveWindow?.let(::dismissUpdate)==true) { delay(500);continue }
                    val node=find(tag)
                    if(node?.isEnabled==true && node.refresh()) {
                        val bounds=android.graphics.Rect();node.getBoundsInScreen(bounds)
                        // Compose may replace its virtual nodes while a page/scroll settles.
                        // Tap only a fresh target whose visible bounds remained stable.
                        delay(250)
                        val current=find(tag);val stable=android.graphics.Rect()
                        current?.getBoundsInScreen(stable)
                        if(current?.isEnabled==true && bounds==stable && !stable.isEmpty) {
                            android.os.ParcelFileDescriptor.AutoCloseInputStream(ui.executeShellCommand("input tap ${stable.centerX()} ${stable.centerY()}")).use { it.readBytes() }
                            break
                        }
                        continue
                    }
                    if(node!=null) { delay(200);continue }
                    val metrics=instrumentation.targetContext.resources.displayMetrics
                    val x=metrics.widthPixels/2;val from=(metrics.heightPixels*0.8).toInt();val to=(metrics.heightPixels*0.65).toInt()
                    android.os.ParcelFileDescriptor.AutoCloseInputStream(ui.executeShellCommand("input swipe $x $from $x $to 600")).use { it.readBytes() }
                    delay(200)
                }
            } } catch(error: TimeoutCancellationException) { throw IllegalStateException("UI target timed out: $tag",error) }
            delay(800)
        }
        suspend fun textClick(text: String) {
            var attempts=0
            try { withTimeout(20_000) {
                while(true) {
                    fun visit(node: AccessibilityNodeInfo): AccessibilityNodeInfo? {
                        if(node.text?.toString().equals(text,true) || node.contentDescription?.toString().equals(text,true))return node
                        repeat(node.childCount) { node.getChild(it)?.let { child -> visit(child)?.let { return it } } };return null
                    }
                    var node=ui.rootInActiveWindow?.let(::visit)
                    val original=node
                    while(node?.isClickable==false)node=node.parent
                    if(node?.isEnabled==true && node.isVisibleToUser && node.performAction(AccessibilityNodeInfo.ACTION_CLICK))break
                    if(original?.isVisibleToUser==true) {
                        val bounds=android.graphics.Rect();original.getBoundsInScreen(bounds)
                        if(!bounds.isEmpty) { android.os.ParcelFileDescriptor.AutoCloseInputStream(ui.executeShellCommand("input tap ${bounds.centerX()} ${bounds.centerY()}")).use { it.readBytes() };break }
                    }
                    if(++attempts%5==0) {
                        val metrics=instrumentation.targetContext.resources.displayMetrics
                        android.os.ParcelFileDescriptor.AutoCloseInputStream(ui.executeShellCommand("input swipe ${metrics.widthPixels/2} ${(metrics.heightPixels*.8).toInt()} ${metrics.widthPixels/2} ${(metrics.heightPixels*.65).toInt()} 400")).use { it.readBytes() }
                    }
                    delay(100)
                }
            } }catch(error: TimeoutCancellationException) { throw IllegalStateException("UI text target timed out: $text",error) }
            delay(400)
        }
        suspend fun scrollToTop() {
            // Settings keeps each destination's scroll position across Activity launches.
            // The join action is near the top; searching only downward can never find it
            // when a previous phase left the same page at the file mapping section.
            val metrics=instrumentation.targetContext.resources.displayMetrics
            repeat(5) {
                android.os.ParcelFileDescriptor.AutoCloseInputStream(ui.executeShellCommand("input swipe ${metrics.widthPixels/2} ${(metrics.heightPixels*.35).toInt()} ${metrics.widthPixels/2} ${(metrics.heightPixels*.85).toInt()} 300")).use { it.readBytes() }
                delay(100)
            }
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
                    tap("open_settings")
                    // Wait for settings to settle before inspecting navigation state. A lazy
                    // overview row being off screen is not evidence of a retained subpage.
                    withTimeout(15_000) { while(find("settings")==null)delay(100) }
                    delay(600)
                    scrollToTop();tap("settings_group_devices");scrollToTop();tap("sync_group_join")
                    withTimeout(30_000) { while(!sync.groupStatus().optBoolean("joined"))delay(100) }
                    withTimeout(15_000) { while(find("sync_group_status")==null)delay(100) }
                    find("sync_group_status")?.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_SHOW_ON_SCREEN.id);delay(300)
                    screenshot("joined")
                }
            }
            config.optString("bindDirectory").takeIf { it.isNotEmpty() }?.let { directory ->
                check(directory.matches(Regex("JeekGroupAcceptance_[0-9a-f]{8}")))
                val root=File(android.os.Environment.getExternalStorageDirectory(),"Documents/$directory");check(root.isDirectory)
                tap("lan_choose_roots");textClick(config.getString("collectionName"))
                textClick(instrumentation.targetContext.getString(com.tinyfish.jeekmusic.R.string.lan_whole_root))
                tap("lan_pick_target")
                withTimeout(15_000) { while(ui.rootInActiveWindow?.packageName?.toString()?.contains("documentsui")!=true || ui.rootInActiveWindow?.findAccessibilityNodeInfosByText("USE THIS FOLDER").isNullOrEmpty())delay(100) }
                delay(600)
                if(ui.rootInActiveWindow?.findAccessibilityNodeInfosByText("Documents").isNullOrEmpty()) {
                    if(ui.rootInActiveWindow?.findAccessibilityNodeInfosByText(android.os.Build.MODEL).isNullOrEmpty())textClick("Show roots")
                    textClick(android.os.Build.MODEL)
                }
                textClick("Documents");textClick(directory)
                textClick("USE THIS FOLDER");textClick("ALLOW")
                tap("lan_save_mapping")
                withTimeout(20_000) { while(lan.store.mappings().isEmpty() || find("lan_save_mapping")!=null)delay(100) }
                assertEquals(config.getString("collectionId"),lan.store.mappings().single().getString("collectionId"))
                screenshot("collection-bound")
                lan.cancel();lan.awaitSync()
            }
            val mapping=lan.store.mappings().singleOrNull()
            fun local(path: String): File {
                check(mapping!=null);LanProtocol.relative(path)
                val directory=mapping.getString("relative");check(directory.matches(Regex("Documents/JeekGroupAcceptance_[0-9a-f]{8}/")))
                return File(android.os.Environment.getExternalStorageDirectory(),directory+path)
            }
            if(config.has("writeFiles") || config.has("deleteFiles")) {
                check(mapping!=null)
                val folder=db.folderDao().all().single { it.treeUri==mapping.getString("treeUri") }
                val tree=DocumentTree(instrumentation.targetContext,lan.localFileIndex)
                config.optJSONObject("writeFiles")?.let { files ->for(path in files.keys()) {
                    local(path);check('/' !in path)
                    val document=tree.findChild(folder,folder.treeDocumentId,path) ?: tree.create(folder,folder.treeDocumentId,"application/octet-stream",path) ?: error("Cannot create test sidecar")
                    check(tree.write(folder,document.documentId,files.getString(path).toByteArray()))
                } }
                config.optJSONArray("deleteFiles")?.let { files ->repeat(files.length()) {
                    val path=files.getString(it);local(path);check('/' !in path)
                    val document=tree.findChild(folder,folder.treeDocumentId,path) ?: error("Missing tracked sidecar")
                    check(tree.delete(folder,document.documentId))
                } }
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
                metadataSync()
                val state=sync.groupStatus();assertEquals(0L,state.getLong("pendingUploads"))
                assertTrue(lan.groups.status.value.optString("lastError").isEmpty())
            }
            if(config.optBoolean("fileSync")) {
                lan.cancel();lan.awaitSync();lan.start();withTimeout(90_000) {
                    while(!lan.isSyncRunning() && lan.store.read().optBoolean("manualPending"))delay(50)
                    lan.awaitSync()
                }
                assertTrue("File sync errors: ${lan.status.value}",lan.status.value.problems.isEmpty() && lan.status.value.fileDeferredCount==0)
                metadataSync()
                config.optJSONObject("expectedFiles")?.let { files ->for(path in files.keys())assertEquals(path,files.getString(path),local(path).readText()) }
                config.optJSONArray("absentFiles")?.let { files ->repeat(files.length()) { assertFalse(local(files.getString(it)).exists()) } }
                if(config.optBoolean("expectTrackMetadata")) {
                    val track=db.trackDao().all().single { it.fileName=="same.wav" && it.trackKey.contains("JeekGroupAcceptance_") }
                    assertEquals("NON_SPORT",db.tagDao().overrides().single { it.trackKey==track.trackKey && it.dimension=="MOOD" }.value)
                    assertEquals(150L,db.lyricsDao().allOffsets().single { it.trackKey==track.trackKey }.offsetMs)
                    assertTrue("Analysis cache did not match the actual Android track",db.tagDao().allFeatures().any { it.trackKey==track.trackKey && it.complete() })
                    assertTrue("Manual vocal language did not match the actual Android track",db.languageDao().forTrack(track.trackKey).any { it.source=="MANUAL" && it.state=="known" && it.languages=="zh" })
                }
                screenshot("collection-synced")
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
