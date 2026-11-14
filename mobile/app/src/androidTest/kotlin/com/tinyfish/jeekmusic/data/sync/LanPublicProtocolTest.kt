package com.tinyfish.jeekmusic.data.sync

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.json.JSONArray
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Opt-in isolated synthetic fixture. The host removes the private test credentials. */
@RunWith(AndroidJUnit4::class)
class LanPublicProtocolTest {
    @Test fun measureMetadataAndFileSync() {
        assumeTrue(InstrumentationRegistry.getArguments().getString("lanPerformanceFixture")=="true")
        val base=InstrumentationRegistry.getInstrumentation().targetContext
        val fixture=JSONObject(File(base.filesDir,"isolated-lan-performance.json").readText())
        val connection=fixture.getJSONObject("connection")
        require(connection.getString("host")=="10.0.2.2")
        val result=JSONObject()
        LanProtocol(connection).use { client ->
            val start=System.nanoTime()
            repeat(16) { assertEquals(4,client.call("capabilities").getInt("protocol")) }
            result.put("metadata16Ms",(System.nanoTime()-start)/1_000_000.0)
        }
        val folder=File(base.cacheDir,"sync-performance-"+java.util.UUID.randomUUID()).apply { mkdirs() }
        val context=object: android.content.ContextWrapper(base) {
            override fun getFilesDir()=File(folder,"private").apply { mkdirs() }
            override fun getNoBackupFilesDir()=getFilesDir()
        }
        val music=File(folder,"music").canonicalFile.apply { mkdirs() }
        try { LanFileDatabase(context).use { db ->
            val sync=LanFileSynchronizer(context,db,com.tinyfish.jeekmusic.data.library.PlayingFiles()) { mapping,pin -> LanLocalFiles(music.path,mapping,db,pin) }
            val mapping=JSONObject().put("key",java.util.UUID.randomUUID().toString()).put("rootId",fixture.getString("rootId")).put("selection",JSONArray(listOf("Selected"))).put("volume","test").put("relative","")
            kotlinx.coroutines.runBlocking {
                for(label in listOf("initial","unchanged")) {
                    val stages=JSONArray();var first=-1.0;val start=System.nanoTime()
                    val report=LanProtocol(connection).use { client -> sync.synchronize(mapping,client,{}, { _,r -> if(r.bytes>0 && first<0)first=(System.nanoTime()-start)/1_000_000.0 },{ _,_,_-> },{},phase={ stage,count,total -> stages.put(JSONObject().put("stage",stage).put("count",count).put("total",total).put("ms",(System.nanoTime()-start)/1_000_000.0)) }) }
                    assertEquals(report.deferred.toString(),0,report.deferredCount);assertEquals(0,report.conflictCount);assertEquals(0L,report.hashBytes)
                    if(label=="unchanged")assertEquals(0L,report.bytes)
                    result.put(label,JSONObject().put("elapsedMs",(System.nanoTime()-start)/1_000_000.0).put("firstTransferMs",first).put("bytes",report.bytes).put("processed",report.processed).put("stages",stages))
                }
            }
            assertEquals(21,music.walkTopDown().count { it.isFile() && !it.name.startsWith(".") })
        } } finally { folder.deleteRecursively() }
        File(base.filesDir,"isolated-lan-performance-result.json").writeText(result.toString())
    }
    @Test fun retriesBusyRevisionAfterHostReleasesSession() {
        assumeTrue(InstrumentationRegistry.getArguments().getString("lanBudgetFixture") == "true")
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val fixture = JSONObject(File(context.filesDir, "isolated-lan-budget-test.json").readText())
        LanProtocol(fixture.getJSONObject("connection")).use { client ->
            val result = client.call("file_revision", JSONObject().put("fileProtocol", 3).put("scope", fixture.getString("scope")))
            assertTrue(result.has("revision"))
        }
    }
    @Test fun realPinnedTlsPagingReadWriteLearningAndWrongPin() {
        assumeTrue(InstrumentationRegistry.getArguments().getString("lanFixture") == "true")
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val fixture = JSONObject(File(context.filesDir, "isolated-lan-test.json").readText())
        val readOnly = fixture.getJSONObject("readOnly")
        val writable = fixture.getJSONObject("writable")
        LanProtocol(readOnly).use { client ->
            LanProtocol.requirePairedCapabilities(client.call("capabilities"))
            val root = client.call("roots").getJSONArray("roots").getJSONObject(0).getString("rootId")
            fun args() = JSONObject().put("rootId", root).put("path", "")
            assertEquals(200, client.call("tree", args().put("offset",0)).getJSONArray("directories").length())
            assertEquals(5, client.call("tree", args().put("offset",200)).getJSONArray("directories").length())
            val plan = client.call("plan",args().put("fileProtocol",3).put("selection",JSONArray(listOf(""))))
            assertEquals(205,plan.getInt("total"))
            fun page(offset: Int) = client.call("manifest",args().put("plan",plan.getString("plan")).put("offset",offset)).getJSONArray("files")
            val files=page(0); assertEquals(200,files.length()); assertEquals(5,page(200).length())
            val entry=files.getJSONObject(0)
            val output=java.io.ByteArrayOutputStream()
            while(output.size().toLong()<entry.getLong("size")) {
                val data=client.call("read",args().put("plan",plan.getString("plan")).put("path",entry.getString("path")).put("offset",output.size())).getString("data")
                val bytes=android.util.Base64.decode(data,android.util.Base64.DEFAULT); assertTrue(bytes.isNotEmpty()); output.write(bytes)
            }
            assertEquals(entry.getLong("size"),output.size().toLong());assertFalse(entry.has("hash"))
        }
        LanProtocol(writable).use { client ->
            val state=JSONObject().put("format",2).put("device",JSONObject().put("id",writable.getString("deviceId")).put("name","Android synthetic fixture")).put("written_at",0).put("playlists",JSONArray()).put("deleted_playlists",JSONArray())
            assertTrue(client.call("state",JSONObject().put("state",state).put("includeFeatures",false)).has("state"))
            val generation=LanProtocol.sha("personal-label-learning-v3\n".toByteArray())
            val page=JSONObject().put("protocol",3).put("generation",generation).put("resetBoundary",JSONArray()).put("events",JSONArray()).put("nextCursor",0).put("hasMore",false)
            assertTrue(client.call("preference_delta",JSONObject().put("protocol",3).put("sender","isolated-android").put("page",page).put("after",0)).has("acceptedCursor"))
            assertTrue(client.call("preference_training",JSONObject().put("protocol",3).put("action","status")).has("learning"))
            assertTrue(client.call("preference_training",JSONObject().put("protocol",3).put("action","cancel")).has("state"))
        }
        val wrongPin=JSONObject(readOnly.toString()).put("pin","0".repeat(64))
        assertTrue(runCatching { LanProtocol(wrongPin).use { it.call("roots") } }.isFailure)
    }
    @Test fun cancelsTheOwnedRemotePlanWithoutBlockingTheCaller() {
        assumeTrue(InstrumentationRegistry.getArguments().getString("lanCancelFixture") == "true")
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val fixture=JSONObject(File(context.filesDir,"isolated-lan-test.json").readText())
        val connection=fixture.getJSONObject("readOnly")
        val client=LanProtocol(connection)
        val executor=java.util.concurrent.Executors.newSingleThreadExecutor()
        try {
            val future=executor.submit<JSONObject> { client.call("plan",JSONObject().put("fileProtocol",3).put("rootId",fixture.getString("rootId")).put("selection",JSONArray(listOf("Heavy")))) }
            val deadline=System.nanoTime()+java.util.concurrent.TimeUnit.SECONDS.toNanos(10)
            while(client.pendingPlanJobId==null && !future.isDone && System.nanoTime()<deadline) Thread.sleep(5)
            assertNotNull(client.pendingPlanJobId)
            val started=System.nanoTime();client.cancel()
            assertTrue(java.util.concurrent.TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-started)<500)
            while(client.remoteCancellationState=="queued" && System.nanoTime()<deadline) Thread.sleep(10)
            assertEquals("cancelled",client.remoteCancellationState)
            assertTrue(runCatching { future.get(2,java.util.concurrent.TimeUnit.SECONDS) }.isFailure)
        } finally { client.cancel();client.close();executor.shutdownNow() }
    }

}
