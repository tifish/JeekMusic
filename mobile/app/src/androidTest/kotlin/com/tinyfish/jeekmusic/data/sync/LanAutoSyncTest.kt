package com.tinyfish.jeekmusic.data.sync

import android.util.Base64
import com.tinyfish.jeekmusic.data.library.write
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.lifecycle.Lifecycle
import com.tinyfish.jeekmusic.MainActivity
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.collect
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.UUID

/** Real foreground scheduler, authenticated PC watcher revisions and public SAF. Never calls start(). */
@RunWith(AndroidJUnit4::class)
class LanAutoSyncTest {
    @Test fun backgroundHandoffWaitsForForegroundCleanup()=runBlocking {
        val raw=InstrumentationRegistry.getArguments().getString("jeekFileFixture") ?: error("Explicit isolated fixture required")
        val config=JSONObject(String(Base64.decode(raw,Base64.NO_WRAP),Charsets.UTF_8))
        require(config.getBoolean("isolated") && config.getBoolean("publicSaf") && config.getString("host")=="127.0.0.1")
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val service=EntryPointAccessors.fromApplication(context,LanSyncWorker.Dependencies::class.java).lan()
        val original=service.store.read()
        service.cancel();service.awaitSync()
        val manager=androidx.work.WorkManager.getInstance(context)
        manager.cancelUniqueWork(LanAutoSync.WORK).result.get()
        manager.cancelUniqueWork(LanAutoSync.WORK+"-handoff").result.get()
        val mapping=JSONObject().put("layoutVersion",2).put("key",UUID.randomUUID().toString()).put("rootId",config.getString("rootId")).put("name","Isolated handoff")
            .put("selection",JSONArray(listOf(""))).put("treeUri","content://com.android.externalstorage.documents/tree/primary%3ADocuments%2FJeekLanFinalDirect")
            .put("documentId","primary:Documents/JeekLanFinalDirect").put("volume","external_primary").put("relative","Documents/JeekLanFinalDirect/").put("enabled",true)
        val field=LanSyncService::class.java.getDeclaredField("gate").apply { isAccessible=true }
        val gate=field.get(service) as kotlinx.coroutines.sync.Mutex
        val jobField=LanSyncService::class.java.getDeclaredField("job").apply { isAccessible=true }
        suspend fun waitFor(label: String,check: ()->Boolean) {
            try { withTimeout(30_000) { while(!check())delay(50) } }
            catch(e: TimeoutCancellationException) { throw AssertionError("$label: ${service.schedule.value.json()} status=${service.status.value.text}",e) }
        }
        val texts=java.util.Collections.synchronizedList(mutableListOf<String>())
        val collector=launch(Dispatchers.Unconfined) { service.status.collect { texts+=it.text } }
        try {
            service.store.update { it.put("connection",JSONObject(config.toString())).put("mappings",JSONArray(listOf(mapping))).put("autoCancelled",true) }
            service.initializeAutomation()
            ActivityScenario.launch(MainActivity::class.java).use { activity ->
                waitFor("foreground") { service.schedule.value.foreground }
                gate.lock()
                service.start()
                waitFor("preparing") { service.status.value.running }
                val foregroundJob=jobField.get(service) as Job
                texts.clear()
                activity.moveToState(Lifecycle.State.CREATED)
                waitFor("worker_handoff") { foregroundJob.isCompleted && manager.getWorkInfosForUniqueWork(LanAutoSync.WORK+"-handoff").get().any { it.state==androidx.work.WorkInfo.State.RUNNING } }
                assertTrue(foregroundJob.isCompleted)
                assertEquals(0L,service.schedule.value.backoffUntil)
                assertTrue(texts.contains(context.getString(com.tinyfish.jeekmusic.R.string.lan_background_waiting)))
                assertFalse(texts.contains(context.getString(com.tinyfish.jeekmusic.R.string.lan_cancelled)))
                service.setEnabled(config.getString("rootId"),true)
                waitFor("pending_hint") { service.schedule.value.dirty }
                assertEquals("manual",service.schedule.value.trigger)
                assertFalse(service.status.value.running)
                println("HANDOFF_EVIDENCE foregroundCompleted=true workerStarted=true noUserCancel=true noFailureBackoff=true")
                collector.cancelAndJoin()
                service.cancel();gate.unlock();service.awaitSync()
            }
        } finally {
            collector.cancelAndJoin();service.cancel();if(gate.isLocked)gate.unlock();service.awaitSync()
            manager.cancelUniqueWork(LanAutoSync.WORK+"-handoff").result.get()
            service.store.update { state -> state.keys().asSequence().toList().forEach(state::remove);original.keys().forEach { state.put(it,original.get(it)) } }
        }
    }
    private fun id()=UUID.randomUUID().toString().replace("-","")
    private fun audio(marker: Int)=ByteArray(64).also { "RIFF".toByteArray().copyInto(it);"WAVE".toByteArray().copyInto(it,8);it[63]=marker.toByte() }
    @Test fun actualEventsReconcileBothDirectionsAndRespectCancelAndPause()=runBlocking {
        val raw=InstrumentationRegistry.getArguments().getString("jeekFileFixture") ?: error("Explicit isolated fixture required")
        val config=JSONObject(String(Base64.decode(raw,Base64.NO_WRAP),Charsets.UTF_8));require(config.getBoolean("isolated") && config.getBoolean("publicSaf") && config.getString("host")=="127.0.0.1")
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val service=EntryPointAccessors.fromApplication(context,LanSyncWorker.Dependencies::class.java).lan()
        val priorSettings=service.store.read()
        service.cancel();service.awaitSync();val original=service.store.read()
        if(priorSettings.has("autoCancelled"))original.put("autoCancelled",priorSettings.get("autoCancelled")) else original.remove("autoCancelled")
        val prefix="Auto-"+id();val path="$prefix/A.wav"
        val root=File(android.os.Environment.getExternalStorageDirectory(),"Documents/JeekLanFinalDirect")
        val local=File(root,path);local.parentFile!!.mkdirs()
        val mapping=JSONObject().put("layoutVersion",2).put("key",UUID.randomUUID().toString()).put("rootId",config.getString("rootId")).put("name","Synthetic automatic sync")
            .put("selection",JSONArray(listOf(prefix))).put("treeUri","content://com.android.externalstorage.documents/tree/primary%3ADocuments%2FJeekLanFinalDirect").put("documentId","primary:Documents/JeekLanFinalDirect").put("volume","external_primary").put("relative","Documents/JeekLanFinalDirect/").put("enabled",false)
        val otherMapping=UUID.randomUUID().toString()
        var otherScope=""
        fun call(op: String,args: JSONObject=JSONObject()): JSONObject=LanProtocol(config.getJSONObject("writer")).use { it.call(op,args.put("fileProtocol",3)) }.also { check(!it.optBoolean("deferred") && !it.optBoolean("conflict")) { it.toString() } }
        fun remote(): JSONObject=call("file_probe",JSONObject().put("scope",otherScope).put("path",path))
        fun remoteBytes(bytes: ByteArray?) {
            val current=remote();val token=LanFileMetrics.version(current)
            val verified=call("file_verify",JSONObject().put("scope",otherScope).put("path",path).put("expectedVersion",token ?: JSONObject.NULL))
            val hash=if(verified.isNull("stamp"))null else verified.getString("stamp")
            val baseline=call("file_baseline",JSONObject().put("scope",otherScope).put("path",path))
            val ack=call("file_ack",JSONObject().put("scope",otherScope).put("path",path).put("operation",id()).put("baselineVersion",baseline.optString("version")).put("remoteVersion",token ?: JSONObject.NULL).put("localVersion",token ?: JSONObject.NULL).put("localKnown",true).put("decision","ack"))
            val args=JSONObject().put("scope",otherScope).put("path",path).put("operation",id()).put("baselineVersion",ack.optString("version")).put("expectedStamp",hash ?: JSONObject.NULL).put("expectedVersion",token ?: JSONObject.NULL)
            if(bytes==null)call("file_delete",args)
            else {
                args.put("stamp",LanFileMetrics.token(bytes.size.toLong(),1_800_000_000_000_000_000L+bytes.last()*1_000_000_000L)).put("size",bytes.size).put("modifiedNs",1_800_000_000_000_000_000L+bytes.last()*1_000_000_000L).put("resolve",true)
                call("file_begin",args);call("file_chunk",JSONObject().put("scope",otherScope).put("operation",args.getString("operation")).put("offset",0).put("data",Base64.encodeToString(bytes,Base64.NO_WRAP)));call("file_finish",JSONObject().put("scope",otherScope).put("operation",args.getString("operation")))
            }
        }
        suspend fun waitFor(label: String,predicate: ()->Boolean) {
            try { withTimeout(150_000) { while(!predicate())delay(500) } }catch(e: TimeoutCancellationException) { throw AssertionError(label+" status="+service.status.value+" schedule="+service.schedule.value.json(),e) }
            println("AUTO_EVIDENCE $label ${service.schedule.value.json()}")
        }
        ActivityScenario.launch(MainActivity::class.java).use { activity ->
            try {
                otherScope=call("file_open",JSONObject().put("rootId",config.getString("rootId")).put("mapping",otherMapping).put("selection",JSONArray(listOf(prefix)))).getString("scope")
                service.store.update { state -> state.put("connection",JSONObject(config.toString())).put("mappings",JSONArray(listOf(mapping))) }
                service.setEnabled(config.getString("rootId"),true)
                waitFor("startup_auto") { !service.isSyncRunning() && service.schedule.value.lastSuccess>0 && service.store.mappings().first().optString("directoryIdentity").isNotEmpty() }
                remoteBytes(audio(1))
                waitFor("PC_watch_download") { !service.isSyncRunning() && runCatching { local.readBytes().contentEquals(audio(1)) }.getOrDefault(false) }
                // Use the same ordered synthetic clock as remoteBytes. Wall time can
                // be older than the fixture's PC files under the newer-mtime policy.
                local.writeBytes(audio(2));LanFileMetrics.setModified(local,1_800_000_002_000_000_000L);service.localFileIndex.changed(local.parentFile!!,local.name,android.os.FileObserver.CLOSE_WRITE)
                waitFor("phone_watch_upload") { remote().optString("metadataVersion")==LanFileMetrics.token(local.length(),local.lastModified()*1_000_000) && !service.isSyncRunning() }
                assertArrayEquals(audio(2),local.readBytes())
                // A burst of same-directory changes is a hint, never one task per event.
                val beforeBurst=service.schedule.value.lastSuccess
                repeat(12) { local.writeBytes(audio(3));LanFileMetrics.setModified(local,1_800_000_003_000_000_000L);service.localFileIndex.changed(local.parentFile!!,local.name,android.os.FileObserver.CLOSE_WRITE) }
                // A fast empty follow-up may already replace the per-round action summary.
                // Assert the durable remote version and a new successful round instead.
                waitFor("burst_coalesced") { service.schedule.value.lastSuccess>beforeBurst && !service.isSyncRunning() && service.status.value.problems.isEmpty() && remote().optString("metadataVersion")==LanFileMetrics.token(local.length(),local.lastModified()*1_000_000) }
                assertArrayEquals(audio(3),local.readBytes())
                delay(6000)
                assertEquals(0,service.schedule.value.watchDirectories)
                // Drain our own baseline update, then verify the production service
                // retains its durable local index across manual no-change rounds.
                repeat(2) {
                    val previous=service.schedule.value.lastSuccess;service.start()
                    waitFor("manual_local_index_$it") { service.schedule.value.lastSuccess>previous && !service.isSyncRunning() }
                }
                assertEquals(1,service.schedule.value.lastLocalIndexHits)
                assertEquals(0,service.schedule.value.lastLocalIndexMisses)
                assertEquals(0L,service.schedule.value.lastMetadataObservations)
                assertEquals(0L,service.schedule.value.lastMetadataRows)
                assertEquals(0L,service.schedule.value.lastTransferBytes)
                service.cancel();service.awaitSync();val held=remote().optString("metadataVersion")
                local.writeBytes(audio(4));LanFileMetrics.setModified(local,1_800_000_004_000_000_000L);service.localFileIndex.changed(local.parentFile!!,local.name,android.os.FileObserver.CLOSE_WRITE);delay(6000)
                assertEquals(held,remote().optString("metadataVersion"));assertTrue(service.schedule.value.cancelled)
                service.setEnabled(config.getString("rootId"),true)
                waitFor("cancel_resume") { remote().optString("metadataVersion")==LanFileMetrics.token(local.length(),local.lastModified()*1_000_000) && !service.isSyncRunning() }
                service.setEnabled(config.getString("rootId"),false);service.awaitSync();remoteBytes(audio(5));delay(35_000)
                assertArrayEquals(audio(4),local.readBytes())
                service.setEnabled(config.getString("rootId"),true)
                waitFor("paused_resume") { !service.isSyncRunning() && runCatching { local.readBytes().contentEquals(audio(5)) }.getOrDefault(false) }
                local.delete();service.localFileIndex.changed(local.parentFile!!,local.name,android.os.FileObserver.DELETE)
                waitFor("phone_watch_delete") { !remote().optBoolean("exists") && !service.isSyncRunning() }
                remoteBytes(audio(6));waitFor("remote_recreate") { !service.isSyncRunning() && runCatching { local.readBytes().contentEquals(audio(6)) }.getOrDefault(false) }
                remoteBytes(null);waitFor("PC_watch_delete") { !local.exists() && !service.isSyncRunning() }
                // Offline is unknown, and returning to the foreground must catch missed changes.
                service.store.update { it.getJSONObject("connection").put("port",0) }
                remoteBytes(audio(7))
                activity.moveToState(Lifecycle.State.CREATED);delay(1200)
                assertFalse(local.exists())
                service.store.update { it.getJSONObject("connection").put("port",config.getInt("port")) }
                activity.moveToState(Lifecycle.State.RESUMED)
                waitFor("offline_foreground_reconcile") { !service.isSyncRunning() && runCatching { local.readBytes().contentEquals(audio(7)) }.getOrDefault(false) }
                delay(40_000)
                val settled=service.schedule.value.lastSuccess
                val settledBytes=service.schedule.value.lastTransferBytes
                val settledObservations=LanFileMetrics.metadataObservations.get()
                val settledScans=service.localFileIndex.fullScans.get()
                delay(8000)
                assertFalse(service.isSyncRunning());assertEquals(settled,service.schedule.value.lastSuccess)
                assertEquals(0L,service.schedule.value.lastHashBytes);assertEquals(settledBytes,service.schedule.value.lastTransferBytes)
                assertEquals(settledObservations,LanFileMetrics.metadataObservations.get());assertEquals(settledScans,service.localFileIndex.fullScans.get())
                println("AUTO_EVIDENCE self_writes_settled "+service.schedule.value.json())
            }finally {
                service.cancel();service.awaitSync()
                service.store.update { state -> state.keys().asSequence().toList().forEach(state::remove);original.keys().forEach { key -> state.put(key,original.get(key)) } }
                File(root,prefix).deleteRecursively()
            }
        }
    }
    @Test fun unchangedChecksStayQuietAndExternalSidecarsRequireManualRefresh()=runBlocking {
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        check(android.os.Build.HARDWARE in setOf("ranchu","goldfish"))
        val config=JSONObject(String(Base64.decode(InstrumentationRegistry.getArguments().getString("jeekFileFixture") ?: error("Isolated fixture required"),Base64.NO_WRAP),Charsets.UTF_8))
        check(config.getBoolean("isolated") && config.getBoolean("publicSaf") && config.getString("host")=="127.0.0.1")
        val context=instrumentation.targetContext
        val service=EntryPointAccessors.fromApplication(context,LanSyncWorker.Dependencies::class.java).lan()
        val original=service.store.read();service.cancel();service.awaitSync()
        val prefix="Quiet-"+id()
        val root=File("/storage/emulated/0/Documents/JeekLanFinalDirect")
        val local=File(root,prefix).apply { mkdirs() }
        val mapping=JSONObject().put("layoutVersion",2).put("key",UUID.randomUUID().toString()).put("rootId",config.getString("rootId")).put("name","Quiet sync fixture")
            .put("selection",JSONArray(listOf(prefix))).put("treeUri","content://com.android.externalstorage.documents/tree/primary%3ADocuments%2FJeekLanFinalDirect")
            .put("documentId","primary:Documents/JeekLanFinalDirect").put("volume","external_primary").put("relative","Documents/JeekLanFinalDirect/").put("enabled",false)
        val gate=LanSyncService::class.java.getDeclaredField("gate").apply { isAccessible=true }.get(service) as kotlinx.coroutines.sync.Mutex
        var gateOwned=false
        suspend fun waitFor(label: String,predicate: ()->Boolean) {
            try { withTimeout(100_000) { while(!predicate())delay(100) } }
            catch(e: TimeoutCancellationException) { throw AssertionError("$label status=${service.status.value} schedule=${service.schedule.value.json()}",e) }
        }
        fun remote(path: String): JSONObject=LanProtocol(config).use { client ->
            val scope=client.call("file_open",JSONObject().put("fileProtocol",3).put("rootId",config.getString("rootId")).put("mapping",mapping.getString("key")).put("selection",JSONArray(listOf(prefix)))).getString("scope")
            client.call("file_probe",JSONObject().put("fileProtocol",3).put("scope",scope).put("path","$prefix/$path"))
        }
        val ui=instrumentation.uiAutomation
        fun find(value: String): android.view.accessibility.AccessibilityNodeInfo? {
            fun visit(node: android.view.accessibility.AccessibilityNodeInfo): android.view.accessibility.AccessibilityNodeInfo? {
                if(node.viewIdResourceName==value || node.text?.toString()==value || node.contentDescription?.toString()==value)return node
                for(i in 0 until node.childCount)node.getChild(i)?.let { child -> visit(child)?.let { return it } }
                return null
            }
            return ui.rootInActiveWindow?.let(::visit)
        }
        suspend fun tap(value: String) {
            withTimeout(12_000) {
                while(true) {
                    var target=find(value)
                    while(target?.isClickable==false)target=target.parent
                    if(target?.isEnabled==true && target.performAction(android.view.accessibility.AccessibilityNodeInfo.ACTION_CLICK))break
                    delay(50)
                }
            }
            delay(250)
        }
        suspend fun reveal(value: String) {
            repeat(10) {
                if(find(value)!=null)return
                ui.executeShellCommand("input swipe 500 1600 500 650 350").close();delay(400)
            }
            error("Setting not visible: $value")
        }
        fun shot(name: String) {
            android.os.SystemClock.sleep(400)
            val dir=File(context.cacheDir,"sync-quiet-ui").apply { mkdirs() }
            val bitmap=ui.takeScreenshot() ?: error("Screenshot unavailable")
            File(dir,"$name.png").outputStream().use { bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG,100,it) };bitmap.recycle()
        }
        try {
            service.store.update { it.put("connection",JSONObject(config.toString())).put("mappings",JSONArray(listOf(mapping))).put("autoCancelled",true) }
            ActivityScenario.launch(MainActivity::class.java).use { activity ->
                service.setEnabled(config.getString("rootId"),true)
                waitFor("initial inventory") { !service.isSyncRunning() && service.schedule.value.lastSuccess>0 && service.store.mappings().single().optString("directoryIdentity").isNotEmpty() }
                // Real app-owned sidecar writer, through the same SAF grant and notifier.
                val tree=com.tinyfish.jeekmusic.data.library.DocumentTree(context,service.localFileIndex)
                val folder=com.tinyfish.jeekmusic.data.db.LibraryFolderEntity(treeUri=mapping.getString("treeUri"),treeDocumentId=mapping.getString("documentId"),volumeName="external_primary",relativePath=mapping.getString("relative"),displayName="Quiet sync fixture",addedAt=0)
                val documentId="primary:Documents/JeekLanFinalDirect/$prefix/known.lrc"
                File(local,"known.lrc").writeText("placeholder")
                check(tree.write(folder,documentId,"[00:00]app edit".toByteArray()))
                waitFor("known app edit uploaded") { !service.isSyncRunning() && remote("known.lrc").optBoolean("exists") }
                assertEquals(1,service.schedule.value.lastLocalIndexHits)
                val fullScans=service.localFileIndex.fullScans.get()
                // Drain the PC watcher acknowledgement, then check stable idle repeatedly.
                repeat(2) { service.checkScheduled("quiet_fixture_settle");service.awaitSync();delay(250) }
                val success=service.schedule.value.lastSuccess
                val observations=LanFileMetrics.metadataObservations.get()
                repeat(3) { assertFalse(service.checkScheduled("quiet_fixture_unchanged")) }
                assertEquals(success,service.schedule.value.lastSuccess)
                assertEquals(observations,LanFileMetrics.metadataObservations.get())
                assertEquals(fullScans,service.localFileIndex.fullScans.get())
                tap("open_settings");tap("settings_group_devices");reveal("lan_refresh")
                assertNull(find("lan_progress"));shot("unchanged")
                activity.moveToState(Lifecycle.State.CREATED);delay(500);activity.moveToState(Lifecycle.State.RESUMED);delay(3000)
                assertEquals(success,service.schedule.value.lastSuccess);assertEquals(fullScans,service.localFileIndex.fullScans.get())
                assertFalse(service.status.value.running);shot("foreground-return")
                // An external sidecar is intentionally invisible until explicit refresh.
                File(local,"external.lrc").writeText("[00:00]external edit")
                assertFalse(service.checkScheduled("quiet_fixture_external"));assertFalse(remote("external.lrc").optBoolean("exists"))
                gate.lock();gateOwned=true
                tap("lan_refresh")
                waitFor("manual refresh visible") { service.status.value.running && !service.status.value.quiet }
                reveal("lan_progress");shot("manual-refresh")
                gate.unlock();gateOwned=false
                waitFor("external sidecar uploaded") { !service.isSyncRunning() && remote("external.lrc").optBoolean("exists") }
                assertTrue(service.status.value.problems.toString(),service.status.value.problems.isEmpty())
                assertEquals(context.getString(com.tinyfish.jeekmusic.R.string.lan_done),service.status.value.text)
                assertTrue(service.localFileIndex.fullScans.get()>fullScans)
                shot("refresh-complete")
                println("QUIET_SYNC_EVIDENCE unchangedRounds=0 unchangedStats=0 lifecycleFullScans=0 appEditIncremental=true externalSidecarManual=true realUi=true")
            }
        } finally {
            if(gateOwned)gate.unlock()
            service.cancel();service.awaitSync()
            service.store.update { saved -> saved.keys().asSequence().toList().forEach(saved::remove);original.keys().forEach { saved.put(it,original.get(it)) } }
            local.deleteRecursively()
        }
    }

}
