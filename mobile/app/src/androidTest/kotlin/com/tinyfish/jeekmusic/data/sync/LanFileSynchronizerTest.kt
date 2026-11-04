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
        val publicSaf=config.optBoolean("publicSaf")
        val root=if(publicSaf)File(android.os.Environment.getExternalStorageDirectory(),"Documents/JeekLanFinalDirect").canonicalFile else File(parent,"music").apply { mkdirs() }.canonicalFile
        val prefix=config.getString("prefix")+"-"+id().take(8)
        val mapping=JSONObject().put("key",UUID.randomUUID().toString()).put("rootId",config.getString("rootId")).put("selection",JSONArray(listOf(prefix))).put("volume","test").put("relative","")
        if(publicSaf)mapping.put("layoutVersion",2).put("treeUri","content://com.android.externalstorage.documents/tree/primary%3ADocuments%2FJeekLanFinalDirect").put("documentId","primary:Documents/JeekLanFinalDirect").put("volume","external_primary").put("relative","Documents/JeekLanFinalDirect/")
        val db=LanFileDatabase(context)
        val synchronizer=LanFileSynchronizer(context,db,PlayingFiles()) { m,pin -> if(publicSaf)LanLocalFiles.fromMapping(context,m,db,pin) else LanLocalFiles(root.path,m,db,pin) }
        try { Fixture(context,config,root,prefix,mapping,db,synchronizer).body() } finally { db.close();if(publicSaf)File(root,prefix).deleteRecursively();parent.deleteRecursively() }
    }
    private class Fixture(val context: android.content.Context,val connection: JSONObject,val root: File,val prefix: String,val mapping: JSONObject,val db: LanFileDatabase,val sync: LanFileSynchronizer) {
        private var otherScope: String?=null
        val path get()="$prefix/A.wav"
        fun local(bytes: ByteArray?) { val f=File(root,path);f.parentFile!!.mkdirs();if(bytes==null)f.delete() else f.writeBytes(bytes) }
        fun localBytes()=File(root,path).takeIf { it.exists() }?.readBytes()
        fun call(op: String,args: JSONObject=JSONObject()): JSONObject=LanProtocol(connection).use { client ->
            if(op in setOf("file_begin","file_delete","file_ack")) {
                val observed=client.call("file_probe",JSONObject().put("fileProtocol",2).put("scope",args.getString("scope")).put("path",args.getString("path")))
                if(op=="file_ack")args.put("remoteVersion",observed.opt("metadataVersion") ?: JSONObject.NULL).put("localVersion",observed.opt("metadataVersion") ?: JSONObject.NULL).put("decision","ack")
                else args.put("expectedVersion",observed.opt("metadataVersion") ?: JSONObject.NULL)
            }
            client.call(op,args.put("fileProtocol",2))
        }.also { check(!it.optBoolean("conflict") && !it.optBoolean("deferred")) { it.toString() } }
        fun scope(): String { if(otherScope==null)otherScope=call("file_open",JSONObject().put("rootId",mapping.getString("rootId")).put("mapping",UUID.nameUUIDFromBytes(("synthetic-writer:"+connection.getString("deviceId")).toByteArray()).toString()).put("selection",JSONArray(listOf("")))).getString("scope");return otherScope!! }
        fun remoteHash(): String? { val observed=call("file_probe",JSONObject().put("scope",scope()).put("path",path));val r=call("file_verify",JSONObject().put("scope",scope()).put("path",path).put("expectedVersion",observed.opt("metadataVersion") ?: JSONObject.NULL));return if(r.isNull("hash"))null else r.getString("hash") }
        fun remote(bytes: ByteArray?) {
            val scope=scope();val before=remoteHash()
            var baseline=call("file_baseline",JSONObject().put("scope",scope).put("path",path))
            if(before!=null)baseline=call("file_ack",JSONObject().put("scope",scope).put("path",path).put("operation",UUID.randomUUID().toString().replace("-","")).put("baselineVersion",baseline.optString("version")).put("hash",before).put("localHash",before).put("localKnown",true))
            if(bytes==null) { if(before!=null)call("file_delete",JSONObject().put("scope",scope).put("path",path).put("operation",UUID.randomUUID().toString().replace("-","")).put("expectedHash",before).put("baselineVersion",baseline.getString("version")));return }
            val operation=UUID.randomUUID().toString().replace("-","")
            call("file_begin",JSONObject().put("scope",scope).put("path",path).put("operation",operation).put("hash",LanProtocol.sha(bytes)).put("size",bytes.size).put("expectedHash",before ?: JSONObject.NULL).put("baselineVersion",baseline.optString("version")).put("modifiedNs",1_800_000_000_000_000_000L+(bytes.last().toLong() and 255)*1_000_000_000L).put("resolve",true))
            call("file_chunk",JSONObject().put("scope",scope).put("operation",operation).put("offset",0).put("data",Base64.encodeToString(bytes,Base64.NO_WRAP)))
            call("file_finish",JSONObject().put("scope",scope).put("operation",operation))
        }
        suspend fun run(): LanFileReport=LanProtocol(connection).use { sync.synchronize(mapping,it,{}, { _,_-> },{ _,_,_-> },{}) }
        suspend fun exchangeViaProductionService(issue: JSONObject,queued: String?,expected: String) {
            check(android.os.Build.HARDWARE in setOf("ranchu","goldfish"))
            val target=InstrumentationRegistry.getInstrumentation().targetContext
            check(target.packageName.endsWith(".debug"))
            val service=dagger.hilt.android.EntryPointAccessors.fromApplication(target,LanSyncWorker.Dependencies::class.java).lan()
            service.cancel();service.awaitSync()
            val original=service.store.read()
            fun field(name: String)=LanSyncService::class.java.getDeclaredField(name).apply { isAccessible=true }
            val database=field("fileDatabase").get(service) as LanFileDatabase
            val gate=field("gate").get(service) as kotlinx.coroutines.sync.Mutex
            val oldJob=field("job").get(service);val active=kotlinx.coroutines.Job()
            gate.lock()
            try {
                field("job").set(service,active)
                service.store.update { it.put("connection",connection).put("mappings",JSONArray().put(JSONObject(mapping.toString()).put("layoutVersion",2).put("enabled",true))) }
                database.issue(JSONObject(issue.toString()))
                if(queued!=null)database.queueChoice(issue.getString("id"),queued)
                service.exchangeConflictStatus(issue.getString("id"))
                assertTrue(gate.isLocked);assertTrue(active.isActive)
                assertEquals(expected,database.issue(issue.getString("id"))!!.getString("choice"))
                assertEquals(expected,service.status.value.fileConflicts.single { it.getString("id")==issue.getString("id") }.getString("choice"))
                val rev=service.revision.value
                database.resolved(mapping.getString("key"),path)
                assertTrue(service.revision.value>rev)
                assertTrue(service.status.value.fileConflicts.none { it.getString("id")==issue.getString("id") })
            } finally {
                active.cancel();field("job").set(service,oldJob);gate.unlock()
                database.resolved(mapping.getString("key"),path)
                service.store.update { saved -> saved.keys().asSequence().toList().forEach(saved::remove);original.keys().forEach { saved.put(it,original.get(it)) } }
            }
        }
    }
    @Test fun initialAndTrackedChangesReconcileBothDirections()=fixture {
        val a=audio(1);val b=audio(2);val c=audio(3)
        local(a);var report=run();assertEquals(0,report.conflictCount);assertEquals(hash(a),remoteHash());val unchanged=run();assertEquals(0L,unchanged.hashBytes);assertTrue(unchanged.actions.isEmpty())
        File(root,path).setLastModified(File(root,path).lastModified()+2000)
        report=run();assertEquals(0,report.conflictCount);assertEquals(1,report.actions["upload"]) // mtime alone is a change, even when the operation digest is equal.
        local(a+byteArrayOf(1));report=run();assertEquals(0,report.conflictCount);assertEquals(hash(a+byteArrayOf(1)),remoteHash())
        local(b);report=run();assertEquals(0,report.conflictCount);assertEquals(hash(b),remoteHash())
        remote(c);report=run();assertEquals(0,report.conflictCount);assertArrayEquals(c,localBytes())
        local(null);report=run();assertEquals(0,report.conflictCount);assertNull(remoteHash())
        remote(a);report=run();assertEquals(0,report.conflictCount);assertArrayEquals(a,localBytes())
        remote(null);report=run();assertEquals(0,report.conflictCount);assertNull(localBytes())
        local(b);remote(b);File(root,path).setLastModified(1_800_000_000_000L+(b.last().toLong() and 255)*1000L);report=run();assertEquals(0,report.conflictCount);assertEquals(hash(b),remoteHash());assertArrayEquals(b,localBytes())
    }
    @Test fun sharedChoiceWinsAndChangedVersionsNeedAnotherDecision()=fixture {
        val a=audio(61);val b=audio(62);val c=audio(63)
        remote(a);run();local(b);remote(c)
        val issue=run().conflicts.single();assertTrue(issue.getBoolean("shared"))
        assertTrue(issue.getJSONObject("local").getBoolean("exists"));assertEquals(64,issue.getJSONObject("remote").getInt("size"))
        call("file_conflict_choose",JSONObject().put("scope",issue.getString("scope")).put("id",issue.getString("id")).put("choice","remote"))
        exchangeViaProductionService(issue,"local","remote") // status channel works even while the file-sync gate is held
        // A queued opposing phone choice cannot override the first confirmed selection.
        db.queueChoice(issue.getString("id"),"local");run();assertArrayEquals(c,localBytes())
        local(a);remote(b);val next=run().conflicts.single()
        call("file_conflict_choose",JSONObject().put("scope",next.getString("scope")).put("id",next.getString("id")).put("choice","remote"))
        local(c);val changed=run().conflicts.single();assertNotEquals(next.getString("id"),changed.getString("id"))
        assertArrayEquals(c,localBytes());assertEquals(hash(b),remoteHash())
        exchangeViaProductionService(changed,"local","local") // sends phone intent without waiting for the reconciler
        val observed=call("file_conflict_status",JSONObject().put("scope",changed.getString("scope")).put("ids",JSONArray().put(changed.getString("id"))))
        assertEquals("local",observed.getJSONArray("rows").getJSONObject(0).getString("choice"))
        db.queueChoice(changed.getString("id"),"local")
        var removalPublished=false
        db.onIssuesChanged={ if(db.issueCount()==0)removalPublished=true }
        LanProtocol(connection).use { sync.synchronize(mapping,it,{}, { _,_-> },{ _,_,_-> },{ assertTrue(removalPublished) }) }
        assertArrayEquals(c,localBytes());assertEquals(hash(c),remoteHash())
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
        local(b);remote(b);File(root,path).setLastModified(1_800_000_000_000L+(b.last().toLong() and 255)*1000L);val report=run();assertEquals(0,report.conflictCount);assertEquals(1,report.actions["ack"])
        assertFalse(root.walkTopDown().any { it.name.startsWith("JeekMusic-") })
        local(null);run();assertNull(remoteHash())
    }
    @Test fun lostConfirmationThenLocalEditUsesDurableLandingAfterDatabaseReopen()=fixture {
        val a=audio(31);val b=audio(32);val c=audio(33)
        remote(a);run();remote(b)
        val scope=call("file_open",JSONObject().put("rootId",mapping.getString("rootId")).put("mapping",mapping.getString("key")).put("selection",mapping.getJSONArray("selection"))).getString("scope")
        val observed=call("file_probe",JSONObject().put("scope",scope).put("path",path))
        val part=File(File(root,path).parentFile,".synthetic-part").apply { writeBytes(b) }
        val row=JSONObject().put("operation",id()).put("mapping",mapping.getString("key")).put("scope",scope).put("path",path).put("kind","download").put("expectedHash",hash(a)).put("hash",hash(b)).put("size",b.size)
            .put("modifiedNs",observed.getLong("modifiedNs")).put("sourceVersion",LanFileMetrics.version(observed)).put("baselineVersion",db.baseline(mapping.getString("key"),path)!!.getString("version")).put("state","created")
        LanLocalFiles(root.path,mapping,db,false).use { io -> row.put("expectedVersion",LanFileMetrics.version(io.observe(path)));io.install(path,part,row) {} }
        assertEquals("committed",row.getString("state"));assertTrue(row.has("localVersion"));part.delete()
        local(c) // The phone changed after durable commit, before any acknowledgement.
        LanFileDatabase(context).use { reopened ->
            val restarted=LanFileSynchronizer(context,reopened,PlayingFiles()) { m,pin -> if(connection.optBoolean("publicSaf"))LanLocalFiles.fromMapping(context,m,reopened,pin) else LanLocalFiles(root.path,m,reopened,pin) }
            val report=LanProtocol(connection).use { restarted.synchronize(mapping,it,{}, { _,_-> },{ _,_,_-> },{}) }
            assertEquals(report.deferred.toString(),0,report.deferredCount);assertEquals(0,report.conflictCount);assertEquals(hash(c),remoteHash());assertArrayEquals(c,localBytes())
        }
    }
    @Test fun equalMetadataReusesEvenDifferentBytesByExplicitPolicy()=fixture {
        val a=audio(41);val b=audio(42);remote(a);local(b)
        File(root,path).setLastModified(1_800_000_000_000L+41*1000L)
        val report=run();assertEquals(report.deferred.toString(),0,report.deferredCount);assertEquals(0,report.conflictCount);assertEquals(1,report.actions["ack"])
        assertArrayEquals(b,localBytes());assertEquals(hash(a),remoteHash())
        val second=run();assertEquals(0L,second.hashBytes);assertTrue(second.actions.isEmpty())
    }
}
