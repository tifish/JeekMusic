package com.tinyfish.jeekmusic.data.sync

import android.content.ContextWrapper
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.UUID

/** Real SQLite in isolated private directories; no existing mappings or media are touched. */
@RunWith(AndroidJUnit4::class)
class LanFileDatabaseBatchTest {
    private fun fixture(test: (ContextWrapper)->Unit) {
        val target=InstrumentationRegistry.getInstrumentation().targetContext
        val root=File(target.cacheDir,"lan-snapshot-batch-${UUID.randomUUID()}").apply { mkdirs() }
        val context=object: ContextWrapper(target) { override fun getNoBackupFilesDir(): File=root }
        try { test(context) } finally { root.deleteRecursively() }
    }
    private fun row(path: String,value: Int=1)=JSONObject().put("path",path).put("value",value)

    @Test fun boundedPagesPersistAcrossReopenAndRejectOversizeBeforeWriting()=fixture { context ->
        val rows=List(200) { row("Music/$it.wav",it) }
        LanFileDatabase(context).use { db ->
            db.entries("mapping","local",JSONArray(rows));db.baselines("mapping",JSONArray(rows))
            assertEquals(200,db.paths("mapping",0).size)
            assertThrows(IllegalArgumentException::class.java) { db.entries("mapping","remote",List(201) { row("Remote/$it.wav") }) }
            assertThrows(IllegalArgumentException::class.java) { db.baselines("other",List(201) { row("Remote/$it.wav") }) }
            assertNull(db.entry("mapping","remote","Remote/0.wav"));assertNull(db.baseline("other","Remote/0.wav"))
        }
        LanFileDatabase(context).use { db ->
            for(index in 0 until 200) {
                assertEquals(index,db.entry("mapping","local","Music/$index.wav")!!.getInt("value"))
                assertEquals(index,db.baseline("mapping","Music/$index.wav")!!.getInt("value"))
            }
        }
    }
    @Test fun scanBufferMatchesSingleRowWritesAcrossFullAndTrailingPages()=fixture { context ->
        LanFileDatabase(context).use { db ->
            val buffer=ArrayList<JSONObject>(200)
            fun emit(row: JSONObject) { buffer.add(row);if(buffer.size==200) { db.entries("batch","local",buffer);buffer.clear() } }
            for(index in 0 until 413) { val value=row("Music/$index.wav",index);emit(value);db.entry("single","local",value) }
            db.entries("batch","local",buffer)
            for(offset in listOf(0,200,400))assertEquals(db.paths("single",offset),db.paths("batch",offset))
            for(index in 0 until 413)assertEquals(db.entry("single","local","Music/$index.wav").toString(),db.entry("batch","local","Music/$index.wav").toString())
        }
    }
    @Test fun collisionRollsBackWholePageIncludingEarlierReplacement()=fixture { context ->
        LanFileDatabase(context).use { db ->
            db.entry("mapping","local",row("Keep.wav",1))
            assertThrows(IllegalArgumentException::class.java) {
                db.entries("mapping","local",listOf(row("Keep.wav",2),row("A.wav"),row("a.wav")))
            }
            assertEquals(1,db.entry("mapping","local","Keep.wav")!!.getInt("value"))
            assertNull(db.entry("mapping","local","A.wav"));assertNull(db.entry("mapping","local","a.wav"))
            db.entries("mapping","local",listOf(row("After.wav")))
            assertNotNull(db.entry("mapping","local","After.wav"))
        }
    }
    @Test fun invalidBaselineRollsBackAndResetKeepsDurableJournal()=fixture { context ->
        LanFileDatabase(context).use { db ->
            db.baseline("mapping",row("Keep.wav",1))
            assertThrows(IllegalArgumentException::class.java) { db.baselines("mapping",listOf(row("Keep.wav",2),row("../escape.wav"))) }
            assertEquals(1,db.baseline("mapping","Keep.wav")!!.getInt("value"))
            val operation=UUID.randomUUID().toString()
            db.transaction(JSONObject().put("operation",operation).put("mapping","mapping").put("path","Keep.wav").put("scope","scope").put("state","created"))
            db.entries("mapping","local",listOf(row("Keep.wav")));db.hold("mapping","Keep.wav")
            db.resetSnapshots("mapping")
            assertNull(db.baseline("mapping","Keep.wav"));assertNull(db.entry("mapping","local","Keep.wav"));assertFalse(db.held("mapping","Keep.wav"))
            assertEquals(operation,db.receipt(operation)!!.getString("operation"))
        }
    }
}
