package com.tinyfish.jeekmusic.data.scanner

import android.net.Uri
import android.os.Environment
import android.os.ParcelFileDescriptor
import android.provider.DocumentsContract
import android.system.Os
import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.kyant.taglib.TagLib
import com.tinyfish.jeekmusic.data.cover.CoverStore
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.library.AtomicAudioFile
import com.tinyfish.jeekmusic.data.library.AudioFileEditor
import com.tinyfish.jeekmusic.data.library.DocumentTree
import com.tinyfish.jeekmusic.data.library.PlayingFiles
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import com.tinyfish.jeekmusic.data.settings.CoverSaveMode
import com.tinyfish.jeekmusic.data.settings.LyricsSaveLocation
import com.tinyfish.jeekmusic.data.settings.ScanConfig
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.data.tags.SafeTagWriter
import com.tinyfish.jeekmusic.data.tags.TagEdit
import com.tinyfish.jeekmusic.playback.PlaybackLog
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Runs the production repositories, real SAF grants, Linux rename and the pinned JNI reader. */
@RunWith(AndroidJUnit4::class)
class NativeTagContractTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val context = instrumentation.targetContext
    private val root = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC), "JeekTaglibNative")
    private val output = File(context.getExternalFilesDir(null), "native-tag-validation").apply { mkdirs() }
    private fun asset(name: String) = instrumentation.context.assets.open(if ('/' in name) name else "corpus/$name").use { it.readBytes() }
    private fun uri(name: String) = DocumentsContract.buildDocumentUriUsingTree(
        DocumentsContract.buildTreeDocumentUri("com.android.externalstorage.documents", "primary:Music/JeekTaglibNative"),
        "primary:Music/JeekTaglibNative/$name")
    private fun metadata(file: File) = ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use {
        TagLib.getMetadata(it.dup().detachFd(), false)!!.propertyMap
    }

    @Test fun corpusProductionPaths() = runBlocking {
        assertTrue(Environment.isExternalStorageManager())
        val settings = SettingsRepository(context)
        val previous = settings.current().lyricsSaveLocation
        val db = Room.inMemoryDatabaseBuilder(context, JeekDatabase::class.java).build()
        val report = JSONArray()
        try {
            settings.setLyricsSaveLocation(LyricsSaveLocation.EMBED)
            val folder0 = LibraryFolderEntity(treeUri = DocumentsContract.buildTreeDocumentUri("com.android.externalstorage.documents", "primary:Music/JeekTaglibNative").toString(), treeDocumentId = "primary:Music/JeekTaglibNative", volumeName = "external_primary", relativePath = "Music/JeekTaglibNative/", displayName = "Native validation", addedAt = 0)
            val folder = folder0.copy(id = db.folderDao().insert(folder0))
            val tree = DocumentTree(context)
            val playing = PlayingFiles()
            val editor = AudioFileEditor(context, tree, db.trackDao(), db.tagDao(), playing, settings, PlaybackLog(context))
            val covers = CoverStore(context, db.trackDao(), db.folderDao(), tree, editor)
            val lyrics = LyricsRepository(context, db.folderDao(), db.lyricsDao(), tree, settings, dagger.Lazy { error("Offline synthetic test") }, editor, db.trackDao())
            val cover = asset("front.jpg")
            val regularText = asset("lyrics-after.lrc").toString(Charsets.UTF_8)
            val names = listOf("id3v23.mp3", "id3v24.mp3", "untagged.mp3", "native.flac", "native.ogg", "native.opus", "native.m4a", "untagged.m4a", "faststart.m4a", "multiple-lyrics.mp3", "native.wav", "native.aiff", "native.wma", "native.wv") +
                listOf("private-v23.mp3", "private-v24.mp3", "ambiguous-sylt.mp3", "private.flac", "private.ogg", "private.opus", "private.m4a", "faststart-padded.m4a", "unsafe-faststart.m4a", "unsafe-chained.ogg", "unsafe-chapters.mp3", "unsafe-cenc.m4a", "unsafe-saio.m4a", "large.ogg", "large.opus").map { "boundaries/$it" } +
                listOf("unsafe-alter-v23.mp3", "unsafe-alter-v24.mp3", "unsafe-iloc-udta.m4a", "unsafe-iloc-movie.m4a", "unsafe-iloc-top.m4a", "unsafe-uuid-udta.m4a", "unsafe-uuid-movie.m4a", "unsafe-uuid-top.m4a", "safe-opaque.m4a").map { "review-boundaries/$it" }
            val errors = ArrayList<String>()
            root.mkdirs()
            for (name in names) for (operation in listOf("cover", "lyrics", "combined", "serial")) {
                val extension = name.substringAfterLast('.')
                val text = if ("large." in name) "L".repeat(70000) else regularText
                val file = File(root, "corpus-${name.substringAfterLast('/').substringBeforeLast('.')}-$operation.$extension")
                val before = asset(name)
                file.writeBytes(before)
                file.setLastModified(1_600_000_000_000)
                val row = JSONObject().put("fixture", name).put("operation", operation).put("file", file.name)
                try {
                    val audio = MediaStoreAudio(0, "external_primary", folder.relativePath, file.name, 1_600_000_000, file.length(), 0, null, null)
                    val built = TrackFactory(ScanConfig()).build(audio, folder.id, TagReader(context).read(Uri.fromFile(file))).entity
                    val track = built.copy(id = db.trackDao().insert(listOf(built)).single())
                    val oldMap = metadata(file)
                    val supported = extension in setOf("mp3", "flac", "ogg", "opus", "m4a") && "unsafe-" !in name && name !in setOf("faststart.m4a", "boundaries/private.m4a")
                    val pcmBefore = if (supported) pcm(file) else null
                    val lyricSafe = supported && name != "multiple-lyrics.mp3" && "ambiguous-" !in name
                    val coverEmbedded = supported && (operation != "combined" || lyricSafe)
                    var saved: CoverStore.Saved? = null
                    var location: LyricsSaveLocation? = null
                    if (operation != "lyrics") saved = covers.save(listOf(track), cover, CoverSaveMode.EMBED,
                        if (operation == "combined") mapOf(track.fileKey to lyrics.embedOperation(text)) else emptyMap())
                    if (operation != "cover") {
                        if (saved?.alsoDone?.contains(track.fileKey) == true) { lyrics.onEmbedded(track); location = LyricsSaveLocation.EMBED }
                        else location = lyrics.save(track, text)
                    }
                    if (operation == "serial") {
                        saved = covers.save(listOf(track), cover, CoverSaveMode.EMBED)
                        location = lyrics.save(track, text)
                    }
                    if (operation != "lyrics") assertEquals("$name $operation cover", if (coverEmbedded) CoverSaveMode.EMBED else CoverSaveMode.HIDDEN_FOLDER, saved?.mode)
                    if (operation != "cover") assertEquals("$name $operation lyrics", if (lyricSafe) LyricsSaveLocation.EMBED else LyricsSaveLocation.HIDDEN_FOLDER, location)
                    if (operation == "combined") assertEquals(lyricSafe, saved?.alsoDone?.contains(track.fileKey) == true)
                    val afterMap = metadata(file)
                    if (extension in setOf("ogg", "opus") && supported) assertIncompleteOggGranules(file.readBytes())
                    if (coverEmbedded && operation != "lyrics") {
                        ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { fd ->
                            assertArrayEquals("Actual JNI selected cover: $name", cover, TagLib.getFrontCover(fd.dup().detachFd())!!.data)
                        }
                    }
                    if (pcmBefore != null) assertArrayEquals("Android MediaCodec PCM: $name", pcmBefore, pcm(file))
                    for ((key, value) in oldMap) {
                        if (operation != "cover" && lyricSafe && key in TagReader.lyricsKeys(oldMap.keys)) continue
                        assertArrayEquals("Non-target $name $key", value, afterMap[key])
                    }
                    if (operation != "cover" && lyricSafe) assertEquals(text, afterMap["LYRICS"]?.single())
                    if (!supported || (!lyricSafe && operation in setOf("lyrics", "combined"))) assertArrayEquals("Refusal is byte-exact", before, file.readBytes())
                    if (!lyricSafe && operation != "cover") {
                        val hidden = File(root, ".jeekmusic/lyrics/${lyrics.lrcNames(track).first()}")
                        assertArrayEquals("Fallback preserves exact LRC bytes", text.toByteArray(Charsets.UTF_8), hidden.readBytes())
                        assertFalse(db.trackDao().byId(track.id)!!.hasEmbeddedLyrics && !track.hasEmbeddedLyrics)
                    }
                    // Reopening the real persisted SAF URI must resolve the replacement, not a
                    // staging inode; the same grant must still authorize a subsequent serial edit.
                    val reopened = context.contentResolver.openInputStream(uri(file.name))!!.use { it.readBytes() }
                    assertArrayEquals(file.readBytes(), reopened)
                    assertEquals(1_600_000_000_000, file.lastModified())
                    playing.update(setOf(track.fileKey))
                    val busyHash = SafeTagWriter.sha256(file)
                    assertFalse(editor.edit(folder, track, TagEdit(cover, text)))
                    assertEquals(busyHash, SafeTagWriter.sha256(file))
                    playing.update(emptySet())
                    row.put("coverEmbedded", coverEmbedded && operation != "lyrics").put("lyricsEmbedded", lyricSafe && operation != "cover")
                        .put("sha256", SafeTagWriter.sha256(file)).put("mismatches", JSONArray())
                    pcmBefore?.let { row.put("androidPcmSha256", java.security.MessageDigest.getInstance("SHA-256").digest(it).joinToString("") { byte -> "%02x".format(byte) }) }
                } catch (e: Throwable) { row.put("exception", e.stackTraceToString()); errors += "$name $operation: $e" }
                report.put(row)
                File(output, "contract.json").writeText(report.toString(2))
            }
            assertTrue(errors.joinToString("\n"), errors.isEmpty())
        } finally { settings.setLyricsSaveLocation(previous); db.close() }
    }

    @Test fun actualSafAtomicCommitAndFailures() {
        assertTrue(Environment.isExternalStorageManager())
        root.mkdirs()
        val file = File(root, "transaction.mp3")
        val before = asset("id3v24.mp3")
        val change = TagEdit(asset("front.jpg"), asset("lyrics-after.lrc").toString(Charsets.UTF_8))
        val report = JSONArray()
        for (fault in listOf("partial-throw", "false-before-commit", "corrupt-success", "changed-source", "source-path-replaced", "success")) {
            file.writeBytes(before)
            file.setLastModified(1_600_000_000_000)
            val permissions = Os.stat(file.path)
            val rival = before + byteArrayOf(1, 2, 3)
            var directory: File? = null
            var mediaUri: Uri? = null
            try {
                if (fault == "success") {
                    val scanned = java.util.concurrent.CountDownLatch(1)
                    android.media.MediaScannerConnection.scanFile(context, arrayOf(file.path), null) { _, value -> mediaUri = value; scanned.countDown() }
                    assertTrue(scanned.await(15, java.util.concurrent.TimeUnit.SECONDS))
                    assertNotNull(mediaUri)
                }
                val result = runCatching {
                    context.contentResolver.openFileDescriptor(uri(file.name), "rw")!!.use { authorized ->
                        AtomicAudioFile.replace(file, authorized.fileDescriptor, { stage ->
                            when (fault) {
                                "partial-throw" -> { stage.writeBytes(byteArrayOf(1)); error("Injected partial write") }
                                "corrupt-success" -> stage.writeBytes(byteArrayOf(1, 2, 3))
                                else -> SafeTagWriter.write(stage, change, "mp3")
                            }
                        }, { stage -> SafeTagWriter.validate(file, stage, change, "mp3") }, {
                            when (fault) {
                                "false-before-commit" -> false
                                "changed-source" -> { file.writeBytes(rival); true }
                                "source-path-replaced" -> {
                                    // Move ONLY this synthetic source into a task-owned directory,
                                    // replacing its pathname by that directory: rename must refuse.
                                    val holding = File(root, "transaction-holding").apply { mkdirs() }
                                    Os.rename(file.path, File(holding, "original.mp3").path)
                                    assertTrue(file.mkdir())
                                    directory = holding
                                    true
                                }
                                else -> true
                            }
                        })
                    }
                }
                if (fault == "success") {
                    assertEquals(true, result.getOrThrow())
                    assertFalse(before.contentEquals(file.readBytes()))
                    assertEquals(permissions.st_mode, Os.stat(file.path).st_mode)
                    assertEquals(permissions.st_uid, Os.stat(file.path).st_uid)
                    assertEquals(permissions.st_gid, Os.stat(file.path).st_gid)
                    assertEquals(1_600_000_000_000, file.lastModified())
                    val readBack = context.contentResolver.openInputStream(uri(file.name))!!.use { it.readBytes() }
                    assertArrayEquals(file.readBytes(), readBack)
                    assertArrayEquals("Existing MediaStore URI resolves the new inode", file.readBytes(), context.contentResolver.openInputStream(mediaUri!!)!!.use { it.readBytes() })
                } else if (fault == "source-path-replaced") {
                    assertTrue(result.isFailure)
                    assertArrayEquals(before, File(directory!!, "original.mp3").readBytes())
                } else {
                    assertTrue(result.isFailure || !result.getOrThrow())
                    assertArrayEquals(if (fault == "changed-source") rival else before, file.readBytes())
                }
                assertTrue("No abandoned sibling staging files", root.listFiles()!!.none { it.name.startsWith(".jeekmusic-tags-") || it.name.startsWith(".jeek-tags-") })
                report.put(JSONObject().put("fault", fault).put("passed", true))
            } finally {
                directory?.let { holding -> file.delete(); Os.rename(File(holding, "original.mp3").path, file.path); holding.delete() }
                File(output, "transactions.json").writeText(report.toString(2))
            }
        }
    }

    @Test fun actualRenamePermissionFailureLeavesOriginal() {
        val directory = File(context.cacheDir, "tag-rename-permission-test").apply { mkdirs() }
        val file = File(directory, "source.mp3")
        val before = asset("id3v24.mp3")
        file.writeBytes(before)
        file.setLastModified(1_600_000_000_000)
        val edit = TagEdit(lyrics = "[00:00.123]Synthetic 𠮷😀?\r\n")
        try {
            val result = runCatching {
                ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_WRITE).use { fd ->
                    AtomicAudioFile.replace(file, fd.fileDescriptor,
                        { SafeTagWriter.write(it, edit, "mp3") },
                        { SafeTagWriter.validate(file, it, edit, "mp3") },
                        { Os.chmod(directory.path, 0x16d); true }) // 0555: the actual rename syscall must fail.
                }
            }
            val exception = result.exceptionOrNull()
            assertTrue("Actual filesystem EACCES: $exception", exception is android.system.ErrnoException && exception.errno == android.system.OsConstants.EACCES)
            assertArrayEquals(before, file.readBytes())
            File(output, "rename-permission.json").writeText(JSONObject().put("passed", true).put("errno", "EACCES").toString(2))
        } finally {
            Os.chmod(directory.path, 0x1c0)
            directory.listFiles()?.forEach { it.delete() }
            directory.delete()
        }
    }

    private fun pcm(file: File): ByteArray {
        val samples = com.tinyfish.jeekmusic.analysis.PcmDecoder(context).decode(Uri.fromFile(file), listOf(0L until 1000L))
        check(samples != null && samples.isNotEmpty()) { "Android PCM decoder failed: ${file.name}" }
        return java.nio.ByteBuffer.allocate(samples.size * 4).order(java.nio.ByteOrder.LITTLE_ENDIAN).also { buffer -> samples.forEach(buffer::putFloat) }.array()
    }

    private fun assertIncompleteOggGranules(bytes: ByteArray) {
        var at = 0
        while (at < bytes.size) {
            assertEquals("OggS", String(bytes, at, 4, Charsets.US_ASCII))
            val segments = bytes[at + 26].toInt() and 255
            val lacing = bytes.copyOfRange(at + 27, at + 27 + segments)
            if (lacing.isNotEmpty() && lacing.all { it == (-1).toByte() }) {
                assertArrayEquals("An Ogg page with no completed packet has granule -1", ByteArray(8) { -1 }, bytes.copyOfRange(at + 6, at + 14))
            }
            at += 27 + segments + lacing.sumOf { it.toInt() and 255 }
        }
        assertEquals(bytes.size, at)
    }

    @Test fun cooperatingExternalLockIsRespected() {
        val file = File(root, "transaction.mp3")
        val before = asset("id3v24.mp3")
        file.writeBytes(before)
        file.setLastModified(1_600_000_000_000)
        val edit = TagEdit(lyrics = "[00:00.123]Cross-process lock? 𠮷😀\r\n")
        val apk = instrumentation.context.applicationInfo.sourceDir
        val progress = File(output, "lock-progress.txt").apply { writeText("launch\n") }
        val builder = ProcessBuilder("/system/bin/app_process", "/system/bin", "com.tinyfish.jeekmusic.data.scanner.TagLockProcess", file.path).redirectErrorStream(true)
        builder.environment()["CLASSPATH"] = apk
        val process = builder.start()
        try { process.inputStream.bufferedReader().use { reader ->
            val acquired = reader.readLine()
            progress.appendText("helper: $acquired\n")
            assertTrue("Actual external lock helper: $acquired", acquired?.startsWith("LOCKED:") == true)
            assertNotEquals(android.os.Process.myPid().toString(), acquired!!.substringAfter(':'))
            context.contentResolver.openFileDescriptor(uri(file.name), "rw")!!.use { fd ->
                val result = runCatching { AtomicAudioFile.replace(file, fd.fileDescriptor,
                    { progress.appendText("transform\n"); SafeTagWriter.write(it, edit, "mp3") },
                    { progress.appendText("validate\n"); SafeTagWriter.validate(file, it, edit, "mp3") },
                    { progress.appendText("commit check\n"); true }) }
                progress.appendText("replace result: $result\n")
                assertFalse("A different OS process owns the advisory lock", result.getOrDefault(false))
            }
            assertArrayEquals(before, file.readBytes())
            assertEquals("RELEASED", reader.readLine())
            progress.appendText("released\n")
        } } finally {
            if (!process.waitFor(12, java.util.concurrent.TimeUnit.SECONDS)) process.destroyForcibly()
        }
        context.contentResolver.openFileDescriptor(uri(file.name), "rw")!!.use { fd ->
            assertTrue(AtomicAudioFile.replace(file, fd.fileDescriptor,
                { SafeTagWriter.write(it, edit, "mp3") }, { SafeTagWriter.validate(file, it, edit, "mp3") }, { true }))
        }
        File(output, "external-lock.json").writeText(JSONObject().put("passed", true).put("separateProcess", true).put("protocol", "POSIX advisory").toString(2))
    }
}
