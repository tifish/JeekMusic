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
        val index = LocalFileIndex(); index.watched(true)
        val root = File("local-index-unit").absolutePath
        var scans = 0; var stats = 0; var time = 1L
        val files = linkedMapOf("A.wav" to row("A.wav"), "cover.jpg" to row("cover.jpg"))
        suspend fun read(): Pair<LocalIndexResult, List<JSONObject>> {
            val out = mutableListOf<JSONObject>()
            val result = index.read("scope", root, listOf(""), { path -> stats++; files[path] ?: JSONObject().put("known", true).put("exists", false) },
                { emit, _ -> scans++; for (r in files.values) emit(r) }, { out += it }, {}, { time })
            return result to out
        }
        assertFalse(read().first.hit); assertTrue(read().first.hit); assertEquals(1, scans); assertEquals(0, stats)
        files["A.wav"] = row("A.wav", 20); index.changed(File(root), "A.wav", FileObserver.CLOSE_WRITE)
        val changed = read(); assertTrue(changed.first.hit); assertEquals(1, stats); assertEquals(20L, changed.second.first { it.getString("path") == "A.wav" }.getLong("size"))
        files.remove("cover.jpg"); index.changed(File(root), "cover.jpg", FileObserver.DELETE)
        assertEquals(listOf("A.wav"), read().second.map { it.getString("path") }); assertEquals(2, stats); assertEquals(1, scans)
        time += LocalFileIndex.FALLBACK_MS; assertFalse(read().first.hit); assertEquals(2, scans)
    }
    @Test fun incompleteOrConcurrentScansAndLostWatchesNeverPublishAReusableSnapshot() = runBlocking {
        val index = LocalFileIndex(); index.watched(true)
        val root = File("local-index-unit").absolutePath
        var scans = 0; var mutate = true
        suspend fun read(selection: List<String> = listOf("")): LocalIndexResult = index.read("scope", root, selection,
            { row(it) }, { emit, _ -> scans++; emit(row("A.wav")); if (mutate) index.changed(File(root), "A.wav", FileObserver.MODIFY) }, {}, {}, { 1L })
        read(); mutate = false; assertFalse(read().hit); assertTrue(read().hit)
        index.stopWatching(); assertFalse(read().hit); index.watched(true); assertFalse(read().hit); assertTrue(read().hit)
        assertFalse(read(listOf("Album")).hit)
        index.changed(File(root), "NewFolder", FileObserver.CREATE or 0x40000000); assertTrue(index.needsWatches); assertFalse(read().hit)
        index.watched(false); assertFalse(read().hit); assertFalse(read().hit)
        index.stopWatching(); val ticket = index.watchTicket()
        index.changed(File(root), "DuringDiscovery", FileObserver.CREATE or 0x40000000)
        index.watched(true, ticket); assertTrue(index.needsWatches); assertFalse(read().hit)
        val incomplete = runCatching { index.read("other", root, listOf(""), { row(it) }, { _, unavailable -> unavailable("Unknown") }, {}, {}, { 1L }) }
        assertTrue(incomplete.isFailure)
    }
}
