package com.tinyfish.jeekmusic.data.scanner

import android.os.FileObserver
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.io.File

class LocalFileIndexTest {
    private fun row(path: String, size: Long = 10) = JSONObject().put("path", path).put("known", true).put("exists", true).put("size", size).put("modifiedNs", 100).put("metadataVersion", "m2:$size:100")
    @Test fun unchangedRoundsReuseAllFilesButEventsRefreshOnlyTheAffectedPaths() = runBlocking {
        val index = LocalFileIndex()
        val root = File("local-index-unit").absolutePath
        var scans = 0; var stats = 0
        val files = linkedMapOf("A.wav" to row("A.wav"), "cover.jpg" to row("cover.jpg"))
        suspend fun read(): Pair<LocalIndexResult, List<JSONObject>> {
            val out = mutableListOf<JSONObject>()
            val result = index.read("scope", root, listOf(""), { path -> stats++; files[path] ?: JSONObject().put("known", true).put("exists", false) },
                { emit, _ -> scans++; for (r in files.values) emit(r) }, { out += it }, {})
            return result to out
        }
        assertFalse(read().first.hit); assertTrue(read().first.hit); assertEquals(1, scans); assertEquals(0, stats)
        files["A.wav"] = row("A.wav", 20); index.changed(File(root), "A.wav", FileObserver.CLOSE_WRITE)
        val changed = read(); assertTrue(changed.first.hit); assertEquals(1, stats); assertEquals(20L, changed.second.first { it.getString("path") == "A.wav" }.getLong("size"))
        files.remove("cover.jpg"); index.changed(File(root), "cover.jpg", FileObserver.DELETE)
        assertEquals(listOf("A.wav"), read().second.map { it.getString("path") }); assertEquals(2, stats); assertEquals(1, scans)
        assertTrue(read().first.hit)
        index.requestRefresh(); assertFalse(read().first.hit); assertEquals(2, scans)
    }
    @Test fun concurrentChangesSurviveInventoryPublicationAndRequireOnlyOnePathObservation() = runBlocking {
        val index=LocalFileIndex()
        val root=File("local-index-unit").absolutePath
        var scans=0;var stats=0;var mutate=true
        suspend fun read()=index.read("scope",root,listOf(""),{ stats++;row(it,20) },{ emit,_ ->
            scans++;emit(row("A.wav"));if(mutate)index.changed(File(root),"A.wav",FileObserver.MODIFY)
        },{}, {})
        val first=read();mutate=false
        index.confirm("scope",first.revision)
        assertEquals(setOf("A.wav"),index.pendingPaths("scope"))
        val changed=read();assertTrue(changed.hit);assertEquals(1,stats);assertEquals(1,scans)
        assertEquals(20L,index.row("scope","A.wav")!!.getLong("size"))
        // Reading a candidate never acknowledges it: a crash before sync must retry.
        assertEquals(setOf("A.wav"),index.pendingPaths("scope"))
        index.changed(File(root),"cover.jpg",FileObserver.CLOSE_WRITE)
        assertEquals(setOf("A.wav"),index.pendingPaths("scope",changed.revision))
        index.confirm("scope",changed.revision)
        assertEquals(setOf("cover.jpg"),index.pendingPaths("scope"))
        index.requestRefresh();read()
        assertEquals(setOf("cover.jpg"),index.pendingPaths("scope"))
    }
    @Test fun incompleteInventoriesAreRejectedAndManualRefreshIsExplicit() = runBlocking {
        val index=LocalFileIndex();val root=File("local-index-unit").absolutePath
        assertTrue(runCatching { index.read("scope",root,listOf(""),{ row(it) },{ _,unknown -> unknown("Denied") },{}, {}) }.isFailure)
        assertFalse(index.hasSnapshot("scope"))
        suspend fun read(selection: List<String> = listOf(""))=index.read("scope",root,selection,{ row(it) },{ emit,_ -> emit(row("A.wav")) },{}, {})
        read();assertTrue(read().hit)
        index.changed(File(root),"unrelated.txt",FileObserver.CLOSE_WRITE)
        assertTrue(index.pendingPaths("scope")!!.isEmpty())
        assertFalse(read(listOf("Album")).hit)
        index.requestRefresh();assertNull(index.pendingPaths("scope"));assertFalse(read().hit)
    }
}
