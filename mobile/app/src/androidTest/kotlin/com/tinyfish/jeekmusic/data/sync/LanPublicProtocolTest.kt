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
