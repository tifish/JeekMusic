package com.tinyfish.jeekmusic.data.scanner

import android.graphics.Bitmap
import android.os.ParcelFileDescriptor
import android.os.Environment
import android.net.Uri
import android.provider.DocumentsContract
import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.kyant.taglib.Picture
import com.kyant.taglib.TagLib
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.cover.CoverStore
import com.tinyfish.jeekmusic.data.library.AudioFileEditor
import com.tinyfish.jeekmusic.data.library.DocumentTree
import com.tinyfish.jeekmusic.data.library.PlayingFiles
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.data.settings.ScanConfig
import com.tinyfish.jeekmusic.data.settings.CoverSaveMode
import com.tinyfish.jeekmusic.data.settings.LyricsSaveLocation
import com.tinyfish.jeekmusic.playback.PlaybackLog
import com.tinyfish.jeekmusic.analysis.PcmDecoder
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream
import java.io.File
import java.nio.charset.Charset
import java.security.MessageDigest

/** Actual AAR/JNI characterization. All inputs are synthetic; never opens a user library. */
@RunWith(AndroidJUnit4::class)
class NativeTagWriteTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val context = instrumentation.targetContext
    private val output = File(context.getExternalFilesDir(null), "native-tag-validation").apply { mkdirs() }

    init {
        val library = File(context.applicationInfo.nativeLibraryDir, "libtaglib.so")
        val digest = hash(library.readBytes())
        val abi = android.os.Build.SUPPORTED_ABIS.first()
        File(output, "environment.json").writeText(JSONObject()
            .put("dependency", "io.github.kyant0:taglib:1.0.6").put("api", android.os.Build.VERSION.SDK_INT)
            .put("abi", abi).put("build", android.os.Build.FINGERPRINT).put("nativeLibrary", library.path)
            .put("nativeSha256", digest).toString(2))
        val expected = mapOf("x86_64" to "12489f9f7a3829bd944721c6ec68f6f5a5269e705efb3d84bb30d7d5e2478991", "arm64-v8a" to "3e3d66d9b27957616c2f44b8f8c8564e1775b6ce11895f27662fd090a6ae311f")
        assertEquals("Re-characterize JNI behavior when the pinned native binary changes", expected[abi], digest)
    }

    @Test fun nativeBmpMatrix() = matrix(false)
    @Test fun nativeSupplementaryMatrix() = matrix(true)

    @Test fun nativeMultiValueAndEmptyProperties() {
        val file = File(output, "multi-value.mp3")
        val before = fixture("UTF-8", false, false, true)
        file.writeBytes(before)
        val artists = arrayOf("周深", "Björk", "Ελληνικά العربية 𠮷😀?")
        val map = metadata(file)
        map["ARTIST"] = artists
        map["COMMENT"] = emptyArray()
        map["X_JEEK_EMPTY"] = arrayOf("")
        map["LYRICS"] = arrayOf(lyrics(true))
        assertTrue(fd(file) { TagLib.savePropertyMap(it.dup().detachFd(), map) })
        val after = file.readBytes()
        val actual = metadata(file)
        assertArrayEquals(artists, actual["ARTIST"])
        assertEquals(artists.toList(), text(after, "TPE1")!!.split('\u0000'))
        assertTrue(actual["COMMENT"].orEmpty().all { it.isEmpty() })
        assertTrue(actual["X_JEEK_EMPTY"].orEmpty().all { it.isEmpty() })
        assertEquals(text(before, "TIT2"), text(after, "TIT2"))
        assertEquals(lyrics(true), text(after, "USLT"))
        assertArrayEquals(payload(before), payload(after))
    }

    @Test fun productionWritePaths() = runBlocking {
        assertTrue("Set MANAGE_EXTERNAL_STORAGE app-op for this isolated emulator", Environment.isExternalStorageManager())
        assertTrue("Grant the Music/JeekTaglibNative folder in the app's Android folder picker first",
            context.contentResolver.persistedUriPermissions.any { it.isReadPermission && it.isWritePermission && it.uri.toString() == DocumentsContract.buildTreeDocumentUri("com.android.externalstorage.documents", "primary:Music/JeekTaglibNative").toString() })
        instrumentation.uiAutomation.adoptShellPermissionIdentity()
        val root = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC), "JeekTaglibNative").apply { mkdirs() }
        val db = Room.inMemoryDatabaseBuilder(context, JeekDatabase::class.java).build()
        val settings = SettingsRepository(context)
        val oldLocation = settings.current().lyricsSaveLocation
        try {
            settings.setLyricsSaveLocation(LyricsSaveLocation.EMBED)
            val tree = DocumentTree(context)
            val folder0 = LibraryFolderEntity(treeUri = DocumentsContract.buildTreeDocumentUri("com.android.externalstorage.documents", "primary:Music/JeekTaglibNative").toString(), treeDocumentId = "primary:Music/JeekTaglibNative", volumeName = "external_primary", relativePath = "Music/JeekTaglibNative/", displayName = "Native validation", addedAt = 0)
            val folder = folder0.copy(id = db.folderDao().insert(folder0))
            val editor = AudioFileEditor(context, tree, db.trackDao(), db.tagDao(), PlayingFiles(), settings, PlaybackLog(context))
            val covers = CoverStore(context, db.trackDao(), db.folderDao(), tree, editor)
            val lyrics = LyricsRepository(context, db.folderDao(), db.lyricsDao(), tree, settings, dagger.Lazy { error("No network in native validation") }, editor, db.trackDao())
            val report = JSONArray()
            val errors = mutableListOf<String>()
            var expectedPcm: ByteArray? = null
            for ((charset, supplementary) in listOf("UTF-8" to false, "UTF-8" to true, "UTF-16LE" to false, "UTF-16LE" to true, "GBK" to false))
                for (v1 in listOf(false, true)) for (pictureText in listOf("none", "unicode", "ascii")) for (operation in listOf("cover", "lyrics", "combined", "serial")) {
                    val existing = pictureText != "none"
                    val fallback = false // Raw target editing now preserves Unicode back descriptions.
                    val name = "$charset-$supplementary-$v1-$pictureText-$operation"
                    val file = File(root, "$name.mp3")
                    val before = fixture(charset, v1, existing, supplementary, asciiPictures = pictureText == "ascii")
                    file.writeBytes(before)
                    if (expectedPcm == null) expectedPcm = pcm(file)
                    file.setLastModified(1_600_000_000_000)
                    val audio = MediaStoreAudio(0, "external_primary", folder.relativePath, file.name, 1_600_000_000, file.length(), 0, "audio/mpeg", null)
                    val built = TrackFactory(ScanConfig()).build(audio, folder.id, TagReader(context).read(Uri.fromFile(file))).entity
                    val track = built.copy(id = db.trackDao().insert(listOf(built)).single())
                    val row = JSONObject().put("case", name)
                    try {
                        val expectedTitle = if (charset == "GBK") "花火? (Live版)" else "花火? Café Ελληνικά العربية" + if (supplementary) " 𠮷😀" else ""
                        assertEquals(expectedTitle, track.title)
                        var coverResult: CoverStore.Saved? = null
                        var lyricResult: LyricsSaveLocation? = null
                        if (operation != "lyrics") coverResult = covers.save(listOf(track), jpeg(), CoverSaveMode.EMBED,
                            if (operation == "combined") mapOf(track.fileKey to lyrics.embedOperation(lyrics(supplementary))) else emptyMap())
                        if (operation != "cover" && coverResult?.alsoDone?.contains(track.fileKey) != true) lyricResult = lyrics.save(track, lyrics(supplementary))
                        if (operation == "serial") {
                            coverResult = covers.save(listOf(track), jpeg(), CoverSaveMode.EMBED)
                            lyricResult = lyrics.save(track, lyrics(supplementary))
                        }
                        val after = file.readBytes()
                        val displayed = TagReader(context).read(Uri.fromFile(file))!!
                        assertEquals(expectedTitle, displayed.title)
                        assertEquals(listOf("周深"), displayed.artists)
                        assertEquals("时光音乐会2 第6期", displayed.album)
                        val mismatch = JSONArray()
                        for (id in listOf("TIT2", "TPE1", "TALB", "COMM")) if (text(before, id) != text(after, id)) mismatch.put(id)
                        if (v1 && !before.takeLast(128).toByteArray().contentEquals(after.takeLast(128).toByteArray())) mismatch.put("ID3V1")
                        if (!v1 && hasFooter(after)) mismatch.put("ADDED_ID3V1")
                        if (!payload(before).contentEquals(payload(after))) mismatch.put("AUDIO")
                        if (operation != "cover" && text(after, "USLT") != lyrics(supplementary)) mismatch.put("NEW_LYRICS")
                        if (operation != "cover" && lyricResult != null && lyricResult != LyricsSaveLocation.EMBED) mismatch.put("NOT_EMBEDDED")
                        if (operation != "lyrics") {
                            val expectedMode = if (fallback) CoverSaveMode.HIDDEN_FOLDER else CoverSaveMode.EMBED
                            if (coverResult?.mode != expectedMode) mismatch.put("COVER_LOCATION")
                            if (fallback) {
                                val hidden = File(root, ".jeekmusic/covers/${CoverStore.hiddenNames(track).first()}")
                                if (android.graphics.BitmapFactory.decodeFile(hidden.path) == null) mismatch.put("HIDDEN_COVER")
                            } else if (pictures(after).count { it.type == 3 } != 1) mismatch.put("FRONT_COVER")
                        }
                        if (pictures(before).filter { it.type == 4 } != pictures(after).filter { it.type == 4 }) mismatch.put("BACK_PICTURE")
                        if ((operation == "lyrics" || fallback) && pictures(before) != pictures(after)) mismatch.put("OLD_PICTURES")
                        if (existing && !fallback && operation != "lyrics" && pictures(before).first { it.type == 3 }.sha256 == pictures(after).first { it.type == 3 }.sha256) mismatch.put("COVER_NOT_REPLACED")
                        if (operation == "cover" && text(before, "USLT") != text(after, "USLT")) mismatch.put("OLD_LYRICS")
                        if (fallback && operation == "cover" && !before.contentEquals(after)) mismatch.put("FALLBACK_CHANGED_FILE")
                        val decoded = pcm(file)
                        if (!expectedPcm!!.contentEquals(decoded)) mismatch.put("PCM")
                        if (file.lastModified() != 1_600_000_000_000) mismatch.put("MTIME")
                        row.put("coverMode", coverResult?.mode).put("lyricLocation", lyricResult).put("mismatches", mismatch).put("audioSha256", hash(payload(after))).put("pcmSha256", hash(decoded)).put("displayTitle", displayed.title)
                        if (mismatch.length() > 0) errors += "$name $mismatch"
                    } catch (e: Throwable) { row.put("exception", e.stackTraceToString()); errors += "$name: $e" }
                    report.put(row)
                    File(output, "production.json").writeText(report.toString(2))
                }
            // Native containers use different metadata serializers; include a WAV whose PCM ends in TAG.
            for (extension in listOf("flac", "m4a", "ogg", "wav")) for (operation in listOf("cover", "lyrics", "combined", "serial")) {
                val name = "container-$extension-$operation"
                val file = File(root, "$name.$extension")
                val before = instrumentation.context.assets.open("native-unicode.$extension").use { it.readBytes() }
                file.writeBytes(before)
                file.setLastModified(1_600_000_000_000)
                val row = JSONObject().put("case", name)
                try {
                    val beforeMap = metadata(file)
                    val beforePcm = if (extension == "wav") null else pcm(file)
                    val audio = MediaStoreAudio(0, "external_primary", folder.relativePath, file.name, 1_600_000_000, file.length(), 0, null, null)
                    val built = TrackFactory(ScanConfig()).build(audio, folder.id, TagReader(context).read(Uri.fromFile(file))).entity
                    val track = built.copy(id = db.trackDao().insert(listOf(built)).single())
                    assertEquals("Valid UTF-8 source title", "花火? Café Ελληνικά العربية 𠮷😀", beforeMap["TITLE"]?.single())
                    var saved: CoverStore.Saved? = null
                    if (operation != "lyrics") saved = covers.save(listOf(track), jpeg(), CoverSaveMode.EMBED,
                        if (operation == "combined") mapOf(track.fileKey to lyrics.embedOperation(lyrics(true))) else emptyMap())
                    val lyricLocation = if (extension == "wav") LyricsSaveLocation.HIDDEN_FOLDER else LyricsSaveLocation.EMBED
                    if (operation != "cover" && saved?.alsoDone?.contains(track.fileKey) != true) assertEquals(lyricLocation, lyrics.save(track, lyrics(true)))
                    if (operation == "serial") {
                        saved = covers.save(listOf(track), jpeg(), CoverSaveMode.EMBED)
                        assertEquals(lyricLocation, lyrics.save(track, lyrics(true)))
                    }
                    if (operation != "lyrics") assertEquals(if (extension == "wav") CoverSaveMode.HIDDEN_FOLDER else CoverSaveMode.EMBED, saved?.mode)
                    val actual = metadata(file)
                    for (key in listOf("TITLE", "ARTIST", "ALBUM", "COMMENT")) assertArrayEquals(key, beforeMap[key], actual[key])
                    if (operation == "cover" || extension == "wav") assertArrayEquals(beforeMap["LYRICS"], actual["LYRICS"])
                    else assertEquals(lyrics(true), actual["LYRICS"]?.single())
                    if (beforePcm != null) assertArrayEquals(beforePcm, pcm(file))
                    else assertArrayEquals("Unsupported WAV must remain entirely unchanged", before, file.readBytes())
                    assertEquals(1_600_000_000_000, file.lastModified())
                    if (operation != "lyrics" && extension != "wav") {
                        // MP4 covr has no ID3 picture-type classification; validate the selected image.
                        val selected = fd(file) { TagLib.getPictures(it.dup().detachFd()) }.first()
                        val bitmap = android.graphics.BitmapFactory.decodeByteArray(selected.data, 0, selected.data.size)
                        val color = bitmap.getPixel(0, 0)
                        assertTrue(kotlin.math.abs(android.graphics.Color.green(color) - 128) <= 3)
                        assertTrue(kotlin.math.abs(android.graphics.Color.blue(color) - 128) <= 3)
                        bitmap.recycle()
                    }
                    row.put("beforeMap", JSONObject(beforeMap.mapValues { JSONArray(it.value.toList()) }))
                        .put("afterMap", JSONObject(actual.mapValues { JSONArray(it.value.toList()) })).put("pcmSha256", beforePcm?.let(::hash)).put("embedded", extension != "wav").put("mismatches", JSONArray())
                } catch (e: Throwable) { row.put("exception", e.stackTraceToString()); errors += "$name: $e" }
                report.put(row)
                File(output, "production.json").writeText(report.toString(2))
            }
            val showcase = File(root, "Showcase").apply { mkdirs() }
            val examples = listOf("UTF-8-true-true-ascii-combined.mp3", "GBK-false-true-ascii-combined.mp3").map { name ->
                File(root, name).copyTo(File(showcase, name), overwrite = true)
            }
            val scanned = java.util.concurrent.CountDownLatch(examples.size)
            android.media.MediaScannerConnection.scanFile(context, examples.map { it.path }.toTypedArray(), null) { _, _ -> scanned.countDown() }
            assertTrue("Index synthetic showcase tracks", scanned.await(15, java.util.concurrent.TimeUnit.SECONDS))
            assertTrue(errors.joinToString("\n"), errors.isEmpty())
        } finally {
            settings.setLyricsSaveLocation(oldLocation)
            db.close()
            instrumentation.uiAutomation.dropShellPermissionIdentity()
        }
    }

    private fun matrix(supplementary: Boolean) {
        val report = JSONArray()
        val errors = mutableListOf<String>()
        for (charset in listOf("UTF-8", "UTF-16LE", "GBK"))
            for (v1 in listOf(false, true)) for (existing in listOf(false, true))
                for (operation in listOf("cover", "lyrics", "combined")) {
                    if (supplementary && charset == "GBK") continue
                    val name = "${charset}-${v1}-${existing}-${operation}-${supplementary}"
                    val file = File(output, "$name.mp3")
                    val before = fixture(charset, v1, existing, supplementary)
                    file.writeBytes(before)
                    val row = JSONObject().put("case", name)
                    try {
                        val beforeMap = metadata(file)
                        val success = fd(file) { pfd ->
                            var ok = true
                            if (operation != "lyrics") {
                                val others = TagLib.getPictures(pfd.dup().detachFd()).filter { it.pictureType != "Front Cover" }
                                ok = TagLib.savePictures(pfd.dup().detachFd(), (listOf(Picture(jpeg(), "", "Front Cover", "image/jpeg")) + others).toTypedArray())
                            }
                            if (operation != "cover") {
                                val map = TagLib.getMetadata(pfd.dup().detachFd(), false)!!.propertyMap
                                map["LYRICS"] = arrayOf(lyrics(supplementary))
                                ok = TagLib.savePropertyMap(pfd.dup().detachFd(), map) && ok
                            }
                            ok
                        }
                        val after = file.readBytes()
                        val mismatches = JSONArray()
                        for (id in listOf("TIT2", "TPE1", "TALB", "COMM")) {
                            if (text(before, id) != text(after, id)) mismatches.put(id)
                        }
                        if (!payload(before).contentEquals(payload(after))) mismatches.put("AUDIO")
                        if (v1 && !before.takeLast(128).toByteArray().contentEquals(after.takeLast(128).toByteArray())) mismatches.put("ID3V1")
                        if (!v1 && hasFooter(after)) mismatches.put("ADDED_ID3V1")
                        if (operation != "cover" && text(after, "USLT") != lyrics(supplementary)) mismatches.put("NEW_LYRICS")
                        if (pictures(before).filter { it.type == 4 } != pictures(after).filter { it.type == 4 }) mismatches.put("PICTURE_DESCRIPTION")
                        row.put("saveSuccess", success).put("mismatches", mismatches)
                            .put("frameEncodingsBefore", JSONObject(frames(before).filter { it.first in listOf("TIT2", "TPE1", "TALB", "USLT") }.associate { it.first to it.second[0].toInt() }))
                            .put("frameEncodingsAfter", JSONObject(frames(after).filter { it.first in listOf("TIT2", "TPE1", "TALB", "USLT") }.associate { it.first to it.second[0].toInt() }))
                            .put("beforeMap", JSONObject(beforeMap.mapValues { JSONArray(it.value.toList()) }))
                            .put("afterMap", JSONObject(metadata(file).mapValues { JSONArray(it.value.toList()) }))
                            .put("footerBeforeHex", if (hasFooter(before)) before.takeLast(128).toByteArray().joinToString("") { "%02x".format(it) } else JSONObject.NULL)
                            .put("footerAfterHex", if (hasFooter(after)) after.takeLast(128).toByteArray().joinToString("") { "%02x".format(it) } else JSONObject.NULL)
                            .put("audioSha256", hash(payload(after)))
                            .put("pictures", JSONArray(fd(file) { TagLib.getPictures(it.dup().detachFd()) }.map { "${it.pictureType}: ${it.description}" }))
                        // Pinned 1.0.6 behavior, not a claim that these raw writes are safe.
                        // Product tests must preserve all bytes/values instead of accepting these defects.
                        val known = buildSet {
                            if (v1 && operation != "cover") add("ID3V1")
                            if (!v1) add("ADDED_ID3V1")
                            if (existing && operation != "lyrics") add("PICTURE_DESCRIPTION")
                        }
                        val observed = (0 until mismatches.length()).map { mismatches.getString(it) }.toSet()
                        if (!success || observed != known) errors += "$name: unexpected native result $mismatches, known=$known"
                    } catch (e: Throwable) {
                        row.put("exception", e.stackTraceToString())
                        errors += "$name: $e"
                    }
                    report.put(row)
                    File(output, "raw-${supplementary}.json").writeText(report.toString(2))
                }
        assertTrue(errors.joinToString("\n"), errors.isEmpty())
    }

    private fun metadata(file: File) = fd(file) { TagLib.getMetadata(it.dup().detachFd(), false)!!.propertyMap }
    private fun <T> fd(file: File, block: (ParcelFileDescriptor) -> T): T =
        ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_WRITE).use(block)

    private fun fixture(charset: String, v1: Boolean, existing: Boolean, supplementary: Boolean, asciiPictures: Boolean = false): ByteArray {
        val codec = Charset.forName(charset)
        val marker = when (charset) { "UTF-8" -> 3; "GBK" -> 0; else -> 1 }
        val version = if (charset == "UTF-8") 4 else 3
        val special = if (supplementary) " 𠮷😀" else ""
        val title = if (charset == "GBK") "花火? (Live版)" else "花火? Café Ελληνικά العربية$special"
        val frames = ByteArrayOutputStream()
        fun encoded(value: String) = (if (marker == 1) byteArrayOf(-1, -2) else byteArrayOf()) + value.toByteArray(codec)
        fun frame(id: String, body: ByteArray) {
            frames.write(id.toByteArray(Charsets.US_ASCII)); frames.write(size(body.size, version == 4)); frames.write(byteArrayOf(0, 0)); frames.write(body)
        }
        for ((id, value) in listOf("TIT2" to title, "TPE1" to "周深", "TALB" to "时光音乐会2 第6期", "COMM" to "保留备注")) {
            val prefix = if (id == "COMM") "eng".toByteArray() + encoded("\u0000") else byteArrayOf()
            frame(id, byteArrayOf(marker.toByte()) + prefix + encoded(value))
        }
        if (existing) {
            frame("USLT", byteArrayOf(marker.toByte()) + "eng".toByteArray() + encoded("\u0000") + encoded("[00:00.00]原有歌词$special"))
            for ((type, description) in listOf(3 to "旧封面", 4 to if (asciiPictures) "Keep back" else "背面 Café")) {
                frame("APIC", byteArrayOf(marker.toByte()) + "image/jpeg\u0000".toByteArray() + byteArrayOf(type.toByte()) + encoded(description) + "\u0000".toByteArray(codec) + jpeg(android.graphics.Color.BLUE))
            }
        }
        val audio = instrumentation.context.assets.open("tag-writer-tone.mp3").use { it.readBytes() }
        val legacy = ByteArrayOutputStream().apply {
            write("TAG".toByteArray())
            for (value in listOf("旧版花火", "旧歌手", "旧专辑")) write(value.toByteArray(Charset.forName("GBK")).copyOf(30))
            write("2020".toByteArray()); write("旧备注".toByteArray(Charset.forName("GBK")).copyOf(28)); write(byteArrayOf(0, 7, 17))
        }.toByteArray()
        return "ID3".toByteArray() + byteArrayOf(version.toByte(), 0, 0) + size(frames.size(), true) + frames.toByteArray() + audio + if (v1) legacy else byteArrayOf()
    }

    private fun lyrics(supplementary: Boolean) = "[offset:-123]\r\n\r\n[00:01.234]合成歌词? Café Ελληνικά العربية" + (if (supplementary) " 𠮷😀" else "") + "\r\n[00:01.234]Synthetic translation\r\n"
    private fun jpeg(color: Int = android.graphics.Color.rgb(0, 128, 128)) = ByteArrayOutputStream().also { output ->
        Bitmap.createBitmap(8, 8, Bitmap.Config.ARGB_8888).apply { eraseColor(color) }.let { bitmap ->
            try { bitmap.compress(Bitmap.CompressFormat.JPEG, 90, output) } finally { bitmap.recycle() }
        }
    }.toByteArray()
    private fun hash(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
    private fun pcm(file: File): ByteArray {
        // WAV is already PCM: Android has no MediaCodec decoder for audio/raw.
        if (file.extension == "wav") {
            val extractor = android.media.MediaExtractor()
            try {
                extractor.setDataSource(context, Uri.fromFile(file), null)
                val index = (0 until extractor.trackCount).first { extractor.getTrackFormat(it).getString(android.media.MediaFormat.KEY_MIME) == "audio/raw" }
                extractor.selectTrack(index)
                val buffer = java.nio.ByteBuffer.allocate(64 * 1024)
                val result = ByteArrayOutputStream()
                while (true) {
                    buffer.clear()
                    val length = extractor.readSampleData(buffer, 0)
                    if (length < 0) break
                    val bytes = ByteArray(length)
                    buffer.position(0); buffer.get(bytes); result.write(bytes)
                    extractor.advance()
                }
                return result.toByteArray().also { check(it.isNotEmpty() && it.any { byte -> byte != 0.toByte() }) }
            } finally { extractor.release() }
        }
        val samples = PcmDecoder(context).decode(Uri.fromFile(file), listOf(0L until 1000L))
        check(samples != null && samples.isNotEmpty()) { "PCM decoding failed" }
        check(samples.any { it != 0f }) { "Expected generated tone, not empty/silent decoder output" }
        return java.nio.ByteBuffer.allocate(samples.size * 4).order(java.nio.ByteOrder.LITTLE_ENDIAN).also { b -> samples.forEach(b::putFloat) }.array()
    }
    private fun size(n: Int, sync: Boolean): ByteArray = if (sync) byteArrayOf((n shr 21 and 127).toByte(), (n shr 14 and 127).toByte(), (n shr 7 and 127).toByte(), (n and 127).toByte()) else byteArrayOf((n shr 24).toByte(), (n shr 16).toByte(), (n shr 8).toByte(), n.toByte())
    private fun size(bytes: ByteArray, offset: Int, sync: Boolean) = if (sync) (bytes[offset].toInt() and 127 shl 21) or (bytes[offset + 1].toInt() and 127 shl 14) or (bytes[offset + 2].toInt() and 127 shl 7) or (bytes[offset + 3].toInt() and 127) else java.nio.ByteBuffer.wrap(bytes, offset, 4).int
    private fun payload(bytes: ByteArray): ByteArray {
        val end = if (hasFooter(bytes)) bytes.size - 128 else bytes.size
        return bytes.copyOfRange(10 + size(bytes, 6, true), end)
    }
    private fun hasFooter(bytes: ByteArray) = bytes.size >= 128 && String(bytes, bytes.size - 128, 3, Charsets.US_ASCII) == "TAG"
    private fun frames(bytes: ByteArray): List<Pair<String, ByteArray>> {
        val result = mutableListOf<Pair<String, ByteArray>>()
        var offset = 10
        val end = 10 + size(bytes, 6, true)
        while (offset + 10 <= end && bytes[offset] != 0.toByte()) {
            val length = size(bytes, offset + 4, bytes[3] == 4.toByte())
            var body = bytes.copyOfRange(offset + 10, offset + 10 + length)
            if (bytes[3] == 4.toByte()) {
                val flags = bytes[offset + 9].toInt()
                if (flags and 2 != 0) body = body.filterIndexed { i, b -> !(b == 0.toByte() && i > 0 && body[i - 1] == (-1).toByte()) }.toByteArray()
                if (flags and 1 != 0) body = body.copyOfRange(4, body.size)
            }
            result += String(bytes, offset, 4, Charsets.US_ASCII) to body
            offset += 10 + length
        }
        return result
    }
    private fun codec(marker: Byte) = when (marker.toInt()) { 0 -> Charsets.ISO_8859_1; 1 -> Charsets.UTF_16; 2 -> Charsets.UTF_16BE; 3 -> Charsets.UTF_8; else -> error("encoding") }
    private fun decode(bytes: ByteArray, marker: Byte) = codec(marker).newDecoder().onMalformedInput(java.nio.charset.CodingErrorAction.REPORT).decode(java.nio.ByteBuffer.wrap(bytes)).toString().trim('\uFEFF', '\u0000')
    private fun text(bytes: ByteArray, id: String): String? {
        val body = frames(bytes).firstOrNull { it.first == id }?.second ?: return null
        var value = decode(body.copyOfRange(if (id == "COMM" || id == "USLT") 4 else 1, body.size), body[0])
        if (id == "COMM" || id == "USLT") value = value.substringAfter('\u0000').trim('\uFEFF', '\u0000')
        return value
    }
    private data class RawPicture(val type: Int, val description: String, val mime: String, val sha256: String)
    private fun pictures(bytes: ByteArray) = frames(bytes).filter { it.first == "APIC" }.map { (_, b) ->
        val mimeEnd = (1 until b.size).first { b[it] == 0.toByte() }
        val start = mimeEnd + 2
        val width = if (b[0] == 1.toByte() || b[0] == 2.toByte()) 2 else 1
        val end = (start until b.size step width).first { b[it] == 0.toByte() && (width == 1 || b[it + 1] == 0.toByte()) }
        RawPicture(b[mimeEnd + 1].toInt(), decode(b.copyOfRange(start, end), b[0]), String(b, 1, mimeEnd - 1, Charsets.US_ASCII), hash(b.copyOfRange(end + width, b.size)))
    }
}
