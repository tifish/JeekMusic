package com.tinyfish.jeekmusic.data.sync

import android.content.ContextWrapper
import android.util.Base64
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.data.library.PlayingFiles
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.UUID

/** Only runs against the explicitly supplied isolated loopback fixture, never stored pairings. */
@RunWith(AndroidJUnit4::class)
class LanFileSynchronizerTest {
    private fun id()=UUID.randomUUID().toString().replace("-","")
    private fun audio(marker: Int)=ByteArray(64).also { "RIFF".toByteArray().copyInto(it);"WAVE".toByteArray().copyInto(it,8);it[63]=marker.toByte() }
    private fun hash(bytes: ByteArray)=LanProtocol.sha(bytes)
    private fun fixture(body: suspend Fixture.()->Unit)=runBlocking {
        val raw=InstrumentationRegistry.getArguments().getString("jeekFileFixture") ?: error("Explicit isolated fixture required")
        val config=JSONObject(String(Base64.decode(raw,Base64.NO_WRAP),Charsets.UTF_8))
        require(config.getBoolean("isolated") && config.getString("host")=="127.0.0.1")
        val base=InstrumentationRegistry.getInstrumentation().targetContext
        val parent=File(base.cacheDir,"file-sync-test-${id()}").apply { mkdirs() }
        val context=object: ContextWrapper(base) {
            override fun getNoBackupFilesDir()=File(parent,"private").apply { mkdirs() }
            override fun getFilesDir()=File(parent,"files").apply { mkdirs() }
        }
        val root=File(parent,"music").apply { mkdirs() }.canonicalFile
        val prefix=config.getString("prefix")+"-"+id().take(8)
        val mapping=JSONObject().put("key",UUID.randomUUID().toString()).put("rootId",config.getString("rootId")).put("selection",JSONArray(listOf(prefix))).put("volume","test").put("relative","")
        val db=LanFileDatabase(context)
        val synchronizer=LanFileSynchronizer(context,db,PlayingFiles()) { m,pin -> LanLocalFiles(root.path,m,db,pin) }
        try { Fixture(config,root,prefix,mapping,db,synchronizer).body() } finally { parent.deleteRecursively() }
    }
    private class Fixture(val connection: JSONObject,val root: File,val prefix: String,val mapping: JSONObject,val db: LanFileDatabase,val sync: LanFileSynchronizer) {
        private var otherScope: String?=null
        val path get()="$prefix/A.wav"
        fun local(bytes: ByteArray?) { val f=File(root,path);f.parentFile!!.mkdirs();if(bytes==null)f.delete() else f.writeBytes(bytes) }
        fun localBytes()=File(root,path).takeIf { it.exists() }?.readBytes()
        fun call(op: String,args: JSONObject=JSONObject()): JSONObject=LanProtocol(connection).use { it.call(op,args.put("fileProtocol",1)) }.also { check(!it.optBoolean("conflict") && !it.optBoolean("deferred")) { it.toString() } }
        fun scope(): String { if(otherScope==null)otherScope=call("file_open",JSONObject().put("rootId",mapping.getString("rootId")).put("mapping",UUID.randomUUID().toString()).put("selection",JSONArray(listOf(prefix)))).getString("scope");return otherScope!! }
        fun remoteHash(): String? { val r=call("file_probe",JSONObject().put("scope",scope()).put("path",path));return if(r.isNull("hash"))null else r.getString("hash") }
        fun remote(bytes: ByteArray?) {
            val scope=scope();val before=remoteHash()
            var baseline=call("file_baseline",JSONObject().put("scope",scope).put("path",path))
            if(before!=null)baseline=call("file_ack",JSONObject().put("scope",scope).put("path",path).put("operation",UUID.randomUUID().toString().replace("-","")).put("baselineVersion",baseline.optString("version")).put("hash",before).put("localHash",before).put("localKnown",true))
            if(bytes==null) { if(before!=null)call("file_delete",JSONObject().put("scope",scope).put("path",path).put("operation",UUID.randomUUID().toString().replace("-","")).put("expectedHash",before).put("baselineVersion",baseline.getString("version")));return }
            val operation=UUID.randomUUID().toString().replace("-","")
            call("file_begin",JSONObject().put("scope",scope).put("path",path).put("operation",operation).put("hash",LanProtocol.sha(bytes)).put("size",bytes.size).put("expectedHash",before ?: JSONObject.NULL).put("baselineVersion",baseline.optString("version")).put("resolve",true))
            call("file_chunk",JSONObject().put("scope",scope).put("operation",operation).put("offset",0).put("data",Base64.encodeToString(bytes,Base64.NO_WRAP)))
            call("file_finish",JSONObject().put("scope",scope).put("operation",operation))
        }
        suspend fun run(): LanFileReport=LanProtocol(connection).use { sync.synchronize(mapping,it,{}, { _,_-> },{ _,_,_-> },{}) }
    }
    @Test fun initialAndTrackedChangesReconcileBothDirections()=fixture {
        val a=audio(1);val b=audio(2);val c=audio(3)
        local(a);var report=run();assertEquals(0,report.conflictCount);assertEquals(hash(a),remoteHash())
        local(b);report=run();assertEquals(0,report.conflictCount);assertEquals(hash(b),remoteHash())
        remote(c);report=run();assertEquals(0,report.conflictCount);assertArrayEquals(c,localBytes())
        local(null);report=run();assertEquals(0,report.conflictCount);assertNull(remoteHash())
        remote(a);report=run();assertEquals(0,report.conflictCount);assertArrayEquals(a,localBytes())
        remote(null);report=run();assertEquals(0,report.conflictCount);assertNull(localBytes())
        local(b);remote(b);report=run();assertEquals(0,report.conflictCount);assertEquals(hash(b),remoteHash());assertArrayEquals(b,localBytes())
    }
    @Test fun realConflictsRetainBothSidesUntilExplicitChoice()=fixture {
        val a=audio(11);val b=audio(12);val c=audio(13)
        local(a);remote(b);var report=run();assertEquals(1,report.conflictCount);assertArrayEquals(a,localBytes());assertEquals(hash(b),remoteHash())
        sync.choose(report.conflicts.single().getString("id"),"local");report=run();assertEquals(0,report.conflictCount);assertEquals(hash(a),remoteHash())
        local(b);remote(c);report=run();assertEquals(1,report.conflictCount);assertEquals("both_modified",report.conflicts.single().getString("reason"))
        sync.choose(report.conflicts.single().getString("id"),"remote");run();assertArrayEquals(c,localBytes())
        local(a);remote(null);report=run();assertEquals(1,report.conflictCount);assertEquals("modified_vs_deleted",report.conflicts.single().getString("reason"));assertArrayEquals(a,localBytes());assertNull(remoteHash())
        sync.choose(report.conflicts.single().getString("id"),"local");run();assertEquals(hash(a),remoteHash())
    }
    @Test fun sameContentIsReusedAndRestartKeepsConfirmedBaseline()=fixture {
        val a=audio(21);val b=audio(22)
        remote(a);run();assertArrayEquals(a,localBytes())
        local(b);remote(b);val report=run();assertEquals(0,report.conflictCount);assertEquals(1,report.actions["ack"])
        assertFalse(root.walkTopDown().any { it.name.startsWith("JeekMusic-") })
        local(null);run();assertNull(remoteHash())
    }
}
