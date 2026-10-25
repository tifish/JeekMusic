package com.tinyfish.jeekmusic.data.scanner

import android.net.Uri
import android.os.Environment
import android.provider.DocumentsContract
import androidx.room.Room
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.data.cover.CoverStore
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.library.AudioFileEditor
import com.tinyfish.jeekmusic.data.library.DocumentTree
import com.tinyfish.jeekmusic.data.library.PlayingFiles
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import com.tinyfish.jeekmusic.data.settings.CoverSaveMode
import com.tinyfish.jeekmusic.data.settings.LyricsSaveLocation
import com.tinyfish.jeekmusic.data.settings.ScanConfig
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.playback.PlaybackLog
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.io.File

/** Run separately with only this private Debug app's MANAGE_EXTERNAL_STORAGE app-op denied. */
class NativeTagPermissionTest {
    @Test fun safOnlyFallsBackWithoutTouchingAudio() = runBlocking {
        assertFalse("Run this class with all-files access denied", Environment.isExternalStorageManager())
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        val settings = SettingsRepository(context)
        val previous = settings.current().lyricsSaveLocation
        val db = Room.inMemoryDatabaseBuilder(context, JeekDatabase::class.java).build()
        val tree = DocumentTree(context)
        try {
            settings.setLyricsSaveLocation(LyricsSaveLocation.EMBED)
            val folder0 = LibraryFolderEntity(treeUri = DocumentsContract.buildTreeDocumentUri("com.android.externalstorage.documents", "primary:Music/JeekTaglibNative").toString(), treeDocumentId = "primary:Music/JeekTaglibNative", volumeName = "external_primary", relativePath = "Music/JeekTaglibNative/", displayName = "Native validation", addedAt = 0)
            val folder = folder0.copy(id = db.folderDao().insert(folder0))
            val name = "transaction.mp3" // Created and indexed by NativeTagContractTest, synthetic only.
            val documentId = "primary:Music/JeekTaglibNative/$name"
            val before = tree.read(folder, documentId)!!
            val file = File("/sdcard/Music/JeekTaglibNative/$name")
            val audio = MediaStoreAudio(0, "external_primary", folder.relativePath, name, 1_600_000_000, before.size.toLong(), 0, null, null)
            val built = TrackFactory(ScanConfig()).build(audio, folder.id, TagReader(context).read(Uri.fromFile(file))).entity
            val track = built.copy(id = db.trackDao().insert(listOf(built)).single())
            val editor = AudioFileEditor(context, tree, db.trackDao(), db.tagDao(), PlayingFiles(), settings, PlaybackLog(context))
            val covers = CoverStore(context, db.trackDao(), db.folderDao(), tree, editor)
            val lyrics = LyricsRepository(context, db.folderDao(), db.lyricsDao(), tree, settings, dagger.Lazy { error("Offline synthetic test") }, editor, db.trackDao())
            val image = instrumentation.context.assets.open("corpus/front.jpg").use { it.readBytes() }
            val text = "[00:00.123]权限回退? 𠮷😀\r\n[00:00.123]Synthetic translation\r\n"
            val coverSaved = covers.save(listOf(track), image, CoverSaveMode.EMBED, mapOf(track.fileKey to lyrics.embedOperation(text)))!!
            assertEquals(CoverSaveMode.HIDDEN_FOLDER, coverSaved.mode)
            assertTrue(coverSaved.alsoDone.isEmpty())
            assertEquals(LyricsSaveLocation.HIDDEN_FOLDER, lyrics.save(track, text))
            assertArrayEquals(before, tree.read(folder, documentId))
            assertArrayEquals(text.toByteArray(), tree.read(folder, "primary:Music/JeekTaglibNative/.jeekmusic/lyrics/transaction.lrc"))
        } finally { settings.setLyricsSaveLocation(previous); db.close() }
    }
}
