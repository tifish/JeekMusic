package com.tinyfish.jeekmusic.data.scanner

import android.net.Uri
import android.os.ParcelFileDescriptor
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.data.sync.LanAtomicFiles
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class ModifiedPrecisionDeviceTest {
    @Test fun publicMediaStoreAndDatabaseKeepSameSecondChronology() = kotlinx.coroutines.runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        org.junit.Assume.assumeTrue(android.os.Environment.isExternalStorageManager())
        val directory = File(android.os.Environment.getExternalStorageDirectory(), "Music/JeekMtimeTest").apply { mkdirs() }
        val files = listOf(File(directory, "a-older.wav"), File(directory, "z-newer.wav"))
        val keep = InstrumentationRegistry.getArguments().getString("keepMtimeFixture") == "true"
        val db = if (keep) androidx.room.Room.databaseBuilder(context, com.tinyfish.jeekmusic.data.db.JeekDatabase::class.java, "jeek.db")
            .addMigrations(com.tinyfish.jeekmusic.data.db.MIGRATION_7_8).build()
        else androidx.room.Room.inMemoryDatabaseBuilder(context, com.tinyfish.jeekmusic.data.db.JeekDatabase::class.java).build()
        try {
            for ((i, file) in files.withIndex()) {
                val buffer = java.nio.ByteBuffer.allocate(2044).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                buffer.put("RIFF".toByteArray()).putInt(2036).put("WAVEfmt ".toByteArray()).putInt(16)
                    .putShort(1).putShort(1).putInt(8000).putInt(16000).putShort(2).putShort(16)
                    .put("data".toByteArray()).putInt(2000)
                file.writeBytes(buffer.array())
                ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_WRITE).use {
                    LanAtomicFiles.setModified(it.fd, 1793099744000000000L + if(i == 0) 188764700 else 318915800)
                }
            }
            val latch = java.util.concurrent.CountDownLatch(2)
            android.media.MediaScannerConnection.scanFile(context, files.map { it.path }.toTypedArray(), null) { _, _ -> latch.countDown() }
            assertTrue(latch.await(20, java.util.concurrent.TimeUnit.SECONDS))
            val audio = MediaStoreSource(context).query("external_primary", "Music/JeekMtimeTest/")
            assertEquals(2, audio.size)
            assertTrue(audio.all { it.dateModified == 1793099744L })
            val folder = com.tinyfish.jeekmusic.data.db.LibraryFolderEntity(treeUri = "content://com.android.externalstorage.documents/tree/primary%3AMusic%2FJeekMtimeTest",
                treeDocumentId = "primary:Music/JeekMtimeTest", volumeName = "external_primary", relativePath = "Music/JeekMtimeTest/", displayName = "Mtime test", addedAt = 0)
            val folderId = db.folderDao().insert(folder)
            val factory = TrackFactory(com.tinyfish.jeekmusic.data.settings.Settings().scan)
            db.trackDao().insert(audio.map { factory.build(it, folderId, null).entity.copy(modifiedNanos = null) })
            for(a in audio) db.trackDao().updateModifiedPrecision(a.fileKey, a.dateModified, a.size, a.modifiedNanos)
            val tracks = db.trackDao().all().filter { it.folderId == folderId }
            val sorter = com.tinyfish.jeekmusic.data.filter.SongSorter(com.tinyfish.jeekmusic.data.settings.SettingsRepository(context))
            assertEquals(listOf("z-newer.wav", "a-older.wav"), sorter.sorted(tracks, com.tinyfish.jeekmusic.data.settings.SongSort.MODIFIED) { it }.map { it.fileName })
            assertEquals(setOf(188764700, 318915800), tracks.map { it.modifiedNanos }.toSet())
        } finally {
            db.close()
            if (!keep) {
                files.forEach { it.delete() }
                android.media.MediaScannerConnection.scanFile(context, files.map { it.path }.toTypedArray(), null, null)
                directory.delete()
            }
        }
    }

    @Test fun readsActualFractionWithoutChangingFileAndRejectsStaleIndex() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val source = MediaStoreSource(context)
        for (parent in listOf(context.cacheDir, context.externalCacheDir!!)) {
            val file = File.createTempFile("mtime-test-", ".wav", parent)
            try {
                file.writeBytes(byteArrayOf(1, 2, 3))
                ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_WRITE).use {
                    LanAtomicFiles.setModified(it.fd, 1793099744318915800L)
                }
                assertEquals(318915800, source.readModifiedNanos(Uri.fromFile(file), 1793099744, 3))
                assertNull(source.readModifiedNanos(Uri.fromFile(file), 1793099743, 3))
                assertNull(source.readModifiedNanos(Uri.fromFile(file), 1793099744, 4))
                assertArrayEquals(byteArrayOf(1, 2, 3), file.readBytes())
                assertEquals(318915800, source.readModifiedNanos(Uri.fromFile(file), 1793099744, 3))
            } finally { file.delete() }
        }
    }
}
