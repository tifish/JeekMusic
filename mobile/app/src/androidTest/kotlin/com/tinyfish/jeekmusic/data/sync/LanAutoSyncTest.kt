package com.tinyfish.jeekmusic.data.sync

import android.util.Base64
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
                waitFor("PC_watch_download") { local.exists() && local.readBytes().contentEquals(audio(1)) && !service.isSyncRunning() }
                local.writeBytes(audio(2));local.setLastModified(System.currentTimeMillis()+2000)
                waitFor("phone_watch_upload") { remote().optString("metadataVersion")==LanFileMetrics.token(local.length(),local.lastModified()*1_000_000) && !service.isSyncRunning() }
                // A burst of same-directory changes is a hint, never one task per event.
                repeat(12) { local.writeBytes(audio(3));local.setLastModified(System.currentTimeMillis()+3000) }
                waitFor("burst_coalesced") { service.schedule.value.lastSuccess>0 && !service.isSyncRunning() && service.status.value.fileActions["upload"]==1 && remote().optString("metadataVersion")==LanFileMetrics.token(local.length(),local.lastModified()*1_000_000) }
                delay(6000)
                assertTrue(service.schedule.value.watchDirectories>0)
                service.cancel();service.awaitSync();val held=remote().optString("metadataVersion")
                local.writeBytes(audio(4));local.setLastModified(System.currentTimeMillis()+4000);delay(6000)
                assertEquals(held,remote().optString("metadataVersion"));assertTrue(service.schedule.value.cancelled)
                service.setEnabled(config.getString("rootId"),true)
                waitFor("cancel_resume") { remote().optString("metadataVersion")==LanFileMetrics.token(local.length(),local.lastModified()*1_000_000) && !service.isSyncRunning() }
                service.setEnabled(config.getString("rootId"),false);service.awaitSync();remoteBytes(audio(5));delay(35_000)
                assertArrayEquals(audio(4),local.readBytes())
                service.setEnabled(config.getString("rootId"),true)
                waitFor("paused_resume") { local.readBytes().contentEquals(audio(5)) && !service.isSyncRunning() }
                local.delete()
                waitFor("phone_watch_delete") { !remote().optBoolean("exists") && !service.isSyncRunning() }
                remoteBytes(audio(6));waitFor("remote_recreate") { local.exists() && local.readBytes().contentEquals(audio(6)) && !service.isSyncRunning() }
                remoteBytes(null);waitFor("PC_watch_delete") { !local.exists() && !service.isSyncRunning() }
                // Offline is unknown, and returning to the foreground must catch missed changes.
                service.store.update { it.getJSONObject("connection").put("port",0) }
                remoteBytes(audio(7))
                activity.moveToState(Lifecycle.State.CREATED);delay(1200)
                assertFalse(local.exists())
                service.store.update { it.getJSONObject("connection").put("port",config.getInt("port")) }
                activity.moveToState(Lifecycle.State.RESUMED)
                waitFor("offline_foreground_reconcile") { local.exists() && local.readBytes().contentEquals(audio(7)) && !service.isSyncRunning() }
                delay(40_000)
                val settled=service.schedule.value.lastSuccess
                delay(8000)
                assertFalse(service.isSyncRunning());assertEquals(settled,service.schedule.value.lastSuccess)
                assertEquals(0L,service.schedule.value.lastHashBytes);assertEquals(0L,service.schedule.value.lastTransferBytes)
                println("AUTO_EVIDENCE self_writes_settled "+service.schedule.value.json())
            }finally {
                service.cancel();service.awaitSync()
                service.store.update { state -> state.keys().asSequence().toList().forEach(state::remove);original.keys().forEach { key -> state.put(key,original.get(key)) } }
                File(root,prefix).deleteRecursively()
            }
        }
    }
}
