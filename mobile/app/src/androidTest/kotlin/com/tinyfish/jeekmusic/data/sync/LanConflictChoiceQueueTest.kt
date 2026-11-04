package com.tinyfish.jeekmusic.data.sync

import android.content.ContextWrapper
import androidx.test.platform.app.InstrumentationRegistry
import dagger.hilt.android.EntryPointAccessors
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.sync.Mutex
import org.json.JSONObject
import org.json.JSONArray
import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.util.UUID

class LanConflictChoiceQueueTest {
    private fun row(id: String,path: String="A.mp3")=JSONObject().put("id",id).put("mapping","queue-test").put("path",path)
        .put("localVersion","m2:65:1234567890000000000").put("remoteVersion","m2:64:1234567890000000001")
    @Test fun queuedChoiceSurvivesRestartAndConcurrentReportButNotChangedIdentity() {
        val base=InstrumentationRegistry.getInstrumentation().targetContext
        val dir=File(base.cacheDir,"choice-test-${UUID.randomUUID()}").apply { mkdirs() }
        val context=object: ContextWrapper(base) { override fun getNoBackupFilesDir()=dir }
        val id="a".repeat(64)
        try {
            LanFileDatabase(context).use { db ->
                db.issue(row(id));db.queueChoice(id,"local")
                db.updateIssue(row(id).put("choice",JSONObject.NULL)) // server reply begun before user click
                assertEquals("local",db.issuePage().single().getString("queuedChoice"))
            }
            LanFileDatabase(context).use { db ->
                assertEquals("local",db.issue(id)!!.getString("choice"))
                db.updateIssue(row(id).put("choice","remote")) // another device already confirmed
                assertFalse(db.issuePage().single().has("queuedChoice"))
                assertEquals("remote",db.issue(id)!!.getString("choice"))
                db.updateIssue(row(id).put("choice",JSONObject.NULL)) // delayed pre-choice status reply
                assertEquals("remote",db.issuePage().single().getString("choice"))
                assertThrows(IllegalStateException::class.java) { db.queueChoice(id,"local") }
                val next="b".repeat(64);db.issue(row(next))
                assertNull(db.issue(id));assertFalse(db.issue(next)!!.has("queuedChoice"))
                assertThrows(IllegalStateException::class.java) { db.queueChoice(id,"local") }
            }
        } finally { dir.deleteRecursively() }
    }

    @Test fun issueObserversSeeDurableStateImmediatelyIncludingSameCountChoiceAndRemoval() {
        val base=InstrumentationRegistry.getInstrumentation().targetContext
        val dir=File(base.cacheDir,"choice-notify-${UUID.randomUUID()}").apply { mkdirs() }
        val context=object: ContextWrapper(base) { override fun getNoBackupFilesDir()=dir }
        try { LanFileDatabase(context).use { db ->
            val counts=mutableListOf<Int>();val choices=mutableListOf<String>()
            db.onIssuesChanged={ counts+=db.issueCount();choices+=db.issuePage().firstOrNull()?.let { it.optString("queuedChoice",it.optString("choice")) }.orEmpty() }
            val id="c".repeat(64)
            db.issue(row(id));db.queueChoice(id,"local");db.updateIssue(row(id).put("choice","remote"));db.resolved("queue-test","A.mp3")
            assertEquals(listOf(1,1,1,0),counts)
            assertEquals(listOf("","local","remote",""),choices)
            db.updateIssue(row(id).put("choice","remote")) // stale response must not resurrect
            assertEquals(0,db.issueCount());assertEquals(4,counts.size)
        } } finally { dir.deleteRecursively() }
    }

    @Test fun productionServiceAcceptsMultipleChoicesWhileSyncOwnsGate()=runBlocking {
        check(android.os.Build.HARDWARE in setOf("ranchu","goldfish"))
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        check(context.packageName.endsWith(".debug"))
        val service=EntryPointAccessors.fromApplication(context,LanSyncWorker.Dependencies::class.java).lan()
        val original=service.store.read()
        service.cancel();service.awaitSync()
        service.store.update { it.put("mappings",JSONArray()) }
        fun field(name: String)=LanSyncService::class.java.getDeclaredField(name).apply { isAccessible=true }
        val gate=field("gate").get(service) as Mutex
        val database=field("fileDatabase").get(service) as LanFileDatabase
        @Suppress("UNCHECKED_CAST") val status=field("_status").get(service) as MutableStateFlow<LanStatus>
        val old=status.value;val oldJob=field("job").get(service);val active=Job()
        val first=UUID.randomUUID().toString();val second=UUID.randomUUID().toString()
        gate.lock()
        try {
            field("job").set(service,active);status.value=old.copy(running=true)
            database.issue(row(first,"queue-one.mp3"));database.issue(row(second,"queue-two.mp3"))
            service.resolveConflict(first,"local");service.resolveConflict(second,"remote")
            withTimeout(5000) { while(database.issue(first)?.optString("queuedChoice")!="local" || database.issue(second)?.optString("queuedChoice")!="remote")delay(20) }
            assertTrue(gate.isLocked);assertTrue(active.isActive);assertTrue(service.status.value.running)
            assertFalse(service.conflictError.value)
        } finally {
            active.cancel();field("job").set(service,oldJob);status.value=old;gate.unlock()
            database.resolved("queue-test","queue-one.mp3");database.resolved("queue-test","queue-two.mp3")
            service.store.update { saved -> saved.keys().asSequence().toList().forEach(saved::remove);original.keys().forEach { saved.put(it,original.get(it)) } }
        }
    }
}
