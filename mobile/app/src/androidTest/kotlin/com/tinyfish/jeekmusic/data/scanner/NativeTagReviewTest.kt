package com.tinyfish.jeekmusic.data.scanner

import android.os.Environment
import android.os.ParcelFileDescriptor
import android.provider.DocumentsContract
import android.system.Os
import android.net.Uri
import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.data.library.AtomicAudioFile
import com.tinyfish.jeekmusic.data.library.AudioFileEditor
import com.tinyfish.jeekmusic.data.library.DocumentTree
import com.tinyfish.jeekmusic.data.library.PlayingFiles
import com.tinyfish.jeekmusic.data.cover.CoverStore
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import com.tinyfish.jeekmusic.data.settings.CoverSaveMode
import com.tinyfish.jeekmusic.data.settings.ScanConfig
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.playback.PlaybackLog
import com.tinyfish.jeekmusic.data.tags.SafeTagWriter
import com.tinyfish.jeekmusic.data.tags.TagEdit
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import kotlinx.coroutines.runBlocking

/** Deterministic acceptance for the independent 7c439e2 review findings. */
@RunWith(AndroidJUnit4::class)
class NativeTagReviewTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val context = instrumentation.targetContext
    private val root = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC), "JeekTaglibNative").apply { mkdirs() }
    private val output = File(context.getExternalFilesDir(null), "native-tag-validation").apply { mkdirs() }
    private fun asset(path: String) = instrumentation.context.assets.open(path).use { it.readBytes() }
    private val edit get() = TagEdit(asset("corpus/front.jpg"), asset("corpus/lyrics-after.lrc").toString(Charsets.UTF_8))

    @Test fun candidateValidationIsBoundToCommittedBytes() {
        val report = JSONArray()
        val errors = arrayListOf<String>()
        for (moment in listOf("validate", "external-validate", "commit")) for (fault in listOf("truncate", "same-size", "replace-identical")) {
            val file = File(root, "review-candidate.mp3")
            val before = asset("corpus/id3v24.mp3")
            file.writeBytes(before)
            val sidecar = File(root, "review-candidate.lrc").apply { writeText("[00:00.123]Keep external fallback? 𠮷😀\r\n") }
            val sidecarBefore = sidecar.readBytes()
            var stage: File? = null
            fun interfere() {
                val target = stage!!
                if (moment == "external-validate") {
                    val process = helper(fault, target)
                    try {
                        assertTrue(process.waitFor(10, java.util.concurrent.TimeUnit.SECONDS))
                        assertEquals(0, process.exitValue())
                        val message = process.inputStream.bufferedReader().readText().trim()
                        assertTrue(message.startsWith("MUTATED:"))
                        assertNotEquals(android.os.Process.myPid().toString(), message.substringAfter(':'))
                    } finally { process.destroyForcibly() }
                    return
                }
                when (fault) {
                    "truncate" -> target.writeBytes(byteArrayOf(1, 2, 3))
                    "same-size" -> {
                        val timestamp = target.lastModified()
                        val bytes = target.readBytes().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() }
                        target.writeBytes(bytes)
                        target.setLastModified(timestamp)
                    }
                    "replace-identical" -> {
                        val rival = File(root, "review-rival.tmp")
                        rival.writeBytes(target.readBytes())
                        rival.setLastModified(target.lastModified())
                        Os.rename(rival.path, target.path)
                    }
                }
            }
            val result = runCatching {
                ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_WRITE).use { fd ->
                    AtomicAudioFile.replace(file, fd.fileDescriptor,
                        { stage = it; SafeTagWriter.write(it, edit, "mp3") },
                        { SafeTagWriter.validate(file, it, edit, "mp3"); if (moment != "commit") interfere() },
                        { if (moment == "commit") interfere(); true })
                }
            }
            // Mirrors the caller contract: cleanup is allowed only after embedded-success.
            if (result.getOrDefault(false)) sidecar.delete()
            val passed = !result.getOrDefault(false) && before.contentEquals(file.readBytes()) &&
                sidecar.exists() && sidecarBefore.contentEquals(sidecar.readBytes())
            report.put(JSONObject().put("moment", moment).put("fault", fault).put("passed", passed)
                .put("result", result.toString()).put("sourceSha256", SafeTagWriter.sha256(file)))
            if (!passed) errors += "$moment/$fault: $result"
            File(output, "review-candidate.json").writeText(report.toString(2))
        }
        assertTrue(errors.joinToString("\n"), errors.isEmpty())
    }

    @Test fun nestedMp4LocationsAreRefused() = formats(listOf(
        "unsafe-iloc-udta.m4a", "unsafe-iloc-movie.m4a", "unsafe-iloc-top.m4a",
        "unsafe-uuid-udta.m4a", "unsafe-uuid-movie.m4a", "unsafe-uuid-top.m4a"), "review-mp4.json")

    @Test fun id3TagAlterFlagsAreRefused() = formats(listOf("unsafe-alter-v23.mp3", "unsafe-alter-v24.mp3"), "review-id3.json")

    private fun helper(mode: String, file: File): Process {
        val builder = ProcessBuilder("/system/bin/app_process", "/system/bin", "com.tinyfish.jeekmusic.data.scanner.TagCandidateProcess", mode, file.path).redirectErrorStream(true)
        builder.environment()["CLASSPATH"] = instrumentation.context.applicationInfo.sourceDir
        return builder.start()
    }

    @Test fun candidateGuardExcludesAnotherProcess() {
        val file = File(root, "review-guard.mp3").apply { writeBytes(asset("corpus/id3v24.mp3")) }
        var candidate: File? = null
        ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_WRITE).use { fd ->
            assertTrue(AtomicAudioFile.replace(file, fd.fileDescriptor,
                { candidate = it; SafeTagWriter.write(it, edit, "mp3") },
                { SafeTagWriter.validate(file, it, edit, "mp3") }, {
                    val process = helper("probe", candidate!!)
                    try {
                        assertTrue(process.waitFor(10, java.util.concurrent.TimeUnit.SECONDS))
                        assertEquals(0, process.exitValue())
                        assertEquals("BLOCKED", process.inputStream.bufferedReader().readText().trim())
                    } finally { process.destroyForcibly() }
                    true
                }))
        }
        File(output, "review-candidate-guard.json").writeText(JSONObject().put("passed", true).put("separateProcess", true).toString(2))
    }

    @Test fun repositoryCandidateLockFailureRetainsSidecars() = runBlocking {
        val db = Room.inMemoryDatabaseBuilder(context, JeekDatabase::class.java).build()
        val settings = SettingsRepository(context)
        val report = JSONArray()
        try {
            val folder0 = LibraryFolderEntity(treeUri = DocumentsContract.buildTreeDocumentUri("com.android.externalstorage.documents", "primary:Music/JeekTaglibNative").toString(), treeDocumentId = "primary:Music/JeekTaglibNative", volumeName = "external_primary", relativePath = "Music/JeekTaglibNative/", displayName = "Review validation", addedAt = 0)
            val folder = folder0.copy(id = db.folderDao().insert(folder0))
            val tree = DocumentTree(context)
            val editor = AudioFileEditor(context, tree, db.trackDao(), db.tagDao(), PlayingFiles(), settings, PlaybackLog(context))
            val covers = CoverStore(context, db.trackDao(), db.folderDao(), tree, editor)
            val lyrics = LyricsRepository(context, db.folderDao(), db.lyricsDao(), tree, settings, dagger.Lazy { error("Offline fixture") }, editor, db.trackDao())
            for (operation in listOf("cover", "lyrics", "combined")) {
                val file = File(root, "review-repository-$operation.mp3")
                // Streaming padding keeps the named staging file observable by the independent
                // watcher. It is synthetic and valid for the raw MP3-tail preservation contract.
                val before = asset("corpus/untagged.mp3") + ByteArray(4 * 1024 * 1024)
                file.writeBytes(before)
                val audio = MediaStoreAudio(0, "external_primary", folder.relativePath, file.name, 0, file.length(), 0, null, null)
                val built = TrackFactory(ScanConfig()).build(audio, folder.id, TagReader(context).read(Uri.fromFile(file))).entity
                    .copy(album = "Review candidate $operation", albumKey = "review-candidate-$operation")
                val track = built.copy(id = db.trackDao().insert(listOf(built)).single())
                val coverSidecar = File(root, file.nameWithoutExtension + ".jpg").apply { writeBytes(edit.cover!!) }
                val lyricSidecar = File(root, file.nameWithoutExtension + ".lrc").apply { writeBytes(edit.lyrics!!.toByteArray()) }
                val process = helper("watch", root)
                try {
                    val reader = process.inputStream.bufferedReader()
                    assertTrue(reader.readLine().startsWith("WATCHING:"))
                    when (operation) {
                        "cover" -> assertEquals(0, covers.embedDownloaded(listOf(track)))
                        "lyrics" -> assertEquals(0, lyrics.embedDownloaded(listOf(track)))
                        else -> {
                            val saved = covers.save(listOf(track), edit.cover!!, CoverSaveMode.EMBED, mapOf(track.fileKey to edit))!!
                            assertEquals(CoverSaveMode.HIDDEN_FOLDER, saved.mode)
                            assertFalse(saved.alsoDone.contains(track.fileKey))
                        }
                    }
                    assertTrue("Independent helper must actually lock the candidate", reader.readLine().startsWith("LOCKED:"))
                    assertArrayEquals(before, file.readBytes())
                    assertArrayEquals(edit.cover, coverSidecar.readBytes())
                    assertArrayEquals(edit.lyrics!!.toByteArray(), lyricSidecar.readBytes())
                    assertFalse(db.trackDao().byId(track.id)!!.hasEmbeddedLyrics)
                    report.put(JSONObject().put("operation", operation).put("passed", true).put("separateProcess", true).put("sourceSha256", SafeTagWriter.sha256(file)))
                } finally {
                    process.outputStream.close()
                    if (!process.waitFor(5, java.util.concurrent.TimeUnit.SECONDS)) process.destroyForcibly()
                    File(output, "review-repository-sidecars.json").writeText(report.toString(2))
                }
            }
        } finally { db.close() }
    }

    private fun formats(names: List<String>, reportName: String) {
        val report = JSONArray()
        val errors = arrayListOf<String>()
        for (name in names) for ((operation, change) in listOf("cover" to TagEdit(cover = edit.cover), "lyrics" to TagEdit(lyrics = edit.lyrics), "combined" to edit)) {
            val file = File(root, "review-$operation-$name")
            val before = asset("review-boundaries/$name")
            file.writeBytes(before)
            val result = runCatching { SafeTagWriter.write(file, change); SafeTagWriter.validate(file, file, change, file.extension) }
            val passed = result.isFailure && before.contentEquals(file.readBytes())
            report.put(JSONObject().put("fixture", name).put("operation", operation).put("passed", passed)
                .put("result", result.toString()).put("file", file.name).put("sha256", SafeTagWriter.sha256(file)))
            if (!passed) errors += "$name/$operation: $result"
        }
        File(output, reportName).writeText(report.toString(2))
        assertTrue(errors.joinToString("\n"), errors.isEmpty())
    }
}
