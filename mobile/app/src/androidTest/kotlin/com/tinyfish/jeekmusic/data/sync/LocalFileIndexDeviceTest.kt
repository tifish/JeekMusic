package com.tinyfish.jeekmusic.data.sync

import android.content.ContextWrapper
import android.os.FileObserver
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.data.scanner.LocalFileIndex
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.UUID
import java.util.concurrent.atomic.AtomicInteger

/** Real filesystem notifications and descriptors on the emulator; no user pairings or files. */
@RunWith(AndroidJUnit4::class)
class LocalFileIndexDeviceTest {
    @Test fun unchangedInventorySharesDailyMetadataAndRefreshesOnlyChangedFiles() = runBlocking {
        check(android.os.Build.HARDWARE in setOf("ranchu", "goldfish"))
        val base = InstrumentationRegistry.getInstrumentation().targetContext
        val parent = File(base.cacheDir, "local-index-${UUID.randomUUID()}").apply { mkdirs() }
        val context = object : ContextWrapper(base) { override fun getNoBackupFilesDir() = File(parent, "private").apply { mkdirs() } }
        val root = File(parent, "music").apply { mkdirs() }.canonicalFile
        val song = File(root, "A.wav").apply { writeBytes(ByteArray(100)) }
        val cover = File(root, "cover.jpg").apply { writeBytes(ByteArray(20)) }
        val index = LocalFileIndex(); val events = AtomicInteger()
        val watch = object : FileObserver(root, LocalFileIndex.MASK) {
            override fun onEvent(event: Int, path: String?) {
                if (event and LocalFileIndex.MASK != 0 && path?.startsWith('.') != true) { index.changed(root, path, event); events.incrementAndGet() }
            }
        }
        val db = LanFileDatabase(context)
        try {
            watch.startWatching(); index.watched(true)
            LanLocalFiles(root.path, JSONObject().put("key", "test"), db, true).use { io ->
                val rows = mutableListOf<JSONObject>()
                suspend fun scan(): com.tinyfish.jeekmusic.data.scanner.LocalIndexResult {
                    rows.clear(); return io.indexedScan(index, listOf(""), { rows += it }, { fail(it) })
                }
                assertFalse(scan().hit)
                val afterFirst = LanFileMetrics.metadataObservations.get()
                assertTrue(scan().hit); assertEquals(afterFirst, LanFileMetrics.metadataObservations.get())
                val beforeEvent = events.get(); song.writeBytes(ByteArray(120))
                repeat(100) { if (events.get() == beforeEvent) delay(10) }; assertTrue(events.get() > beforeEvent); delay(100)
                // Invoke the actual daily scanner reader, including the same shared singleton.
                val source = com.tinyfish.jeekmusic.data.scanner.MediaStoreSource(context, index)
                val observedStamp = LanFileMetrics.stamp(song).split(':')
                assertNotNull(source.readModifiedNanos(android.net.Uri.fromFile(song), observedStamp[2].toLong() / 1_000_000_000, observedStamp[1].toLong()))
                val shared = scan(); assertTrue(shared.hit); assertEquals(1, shared.shared); assertEquals(0, shared.refreshed)
                assertEquals(120L, rows.first { it.getString("path") == "A.wav" }.getLong("size")); assertEquals(afterFirst, LanFileMetrics.metadataObservations.get())
                val parts = LanFileMetrics.stamp(song).split(':'); db.lastEdit(song.path, parts[1].toLong(), parts[2].toLong(), 1234)
                assertTrue(scan().hit); assertEquals(1234L, rows.first { it.getString("path") == "A.wav" }.getLong("editedAtMs"))
                val beforeDelete = events.get(); assertTrue(cover.delete())
                repeat(100) { if (events.get() == beforeDelete) delay(10) }; assertTrue(events.get() > beforeDelete); delay(100)
                val deleted = scan(); assertTrue(deleted.hit); assertEquals(1, deleted.refreshed); assertEquals(listOf("A.wav"), rows.map { it.getString("path") })
                assertEquals(afterFirst + 1, LanFileMetrics.metadataObservations.get())
                index.stopWatching(); assertFalse(scan().hit)
                File(root, "Parent").mkdirs()
                val ancestors = io.watchDirectories(listOf("Parent/Missing/Leaf"))
                assertTrue(ancestors.contains(root)); assertTrue(ancestors.contains(File(root, "Parent")))
                println("LOCAL_INDEX_EVIDENCE unchangedStats=0 libraryShared=1 editedAtVisible=true sidecarDeleteStats=1 stoppedWatcherFullScan=true")
            }
        } finally { index.stopWatching(); watch.stopWatching(); db.close(); parent.deleteRecursively() }
    }
}
