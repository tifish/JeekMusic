package com.tinyfish.jeekmusic.analysis

import android.content.Context
import android.os.SystemClock
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.db.languageSignature
import com.tinyfish.jeekmusic.data.filter.LanguageRepository
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.net.HttpURLConnection
import java.net.URI
import java.security.MessageDigest
import javax.inject.Inject
import javax.inject.Singleton

data class LanguageTask(val state: String = "idle", val processed: Int = 0, val total: Int = 0, val failed: Int = 0,
    val downloaded: Long = 0, val error: String? = null) {
    val busy get() = state in listOf("preparing", "running", "cancelling")
}

/** Explicit local CPU task; never uploads audio, writes media or feeds learning answers. */
@Singleton
class LanguageAnalysisService @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val db: JeekDatabase,
    private val repository: LanguageRepository,
    private val decoder: PcmDecoder,
    private val tagger: AudioTagger,
) {
    private class ModelDownloadRequired : Exception()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mutable = MutableStateFlow(LanguageTask())
    val task = mutable.asStateFlow()
    private val model get() = File(context.noBackupFilesDir, "models/singing-language/ggml-tiny.bin")
    val modelReady get() = model.isFile && model.length() == MODEL_BYTES
    private var work: Job? = null
    @Volatile private var native: NativeWhisper? = null
    @Volatile private var connection: HttpURLConnection? = null

    @Synchronized fun start(confirmed: Boolean = false, targets: List<TrackEntity>? = null, force: Boolean = false) {
        if (task.value.busy) return
        if (!modelReady && !confirmed) { mutable.value = LanguageTask("download_required"); return }
        mutable.value = LanguageTask(if (modelReady) "running" else "preparing")
        work = scope.launch {
            try {
                ensureModel(confirmed)
                val pending = (targets ?: db.trackDao().all()).distinctBy { it.trackKey }.filter { force || repository.result(it) == null }
                mutable.value = LanguageTask("running", total = pending.size)
                if (pending.isNotEmpty()) NativeWhisper(model.absolutePath).use { engine ->
                    native = engine
                    for (track in pending) {
                        ensureActive()
                        val deadline = SystemClock.elapsedRealtime() + 120_000
                        val job = currentCoroutineContext().job
                        fun check() { job.ensureActive(); check(SystemClock.elapsedRealtime() < deadline) { "Language recognition timed out" } }
                        try {
                            validate(track)
                            val start = track.startMs ?: 0
                            val duration = minOf(track.durationMs, (track.endMs ?: (start + track.durationMs)) - start).coerceAtLeast(0)
                            val samples = mutableListOf<LanguageSample>()
                            val uri = MediaStoreSource.contentUri(track.volumeName, track.mediaStoreId)
                            for (window in SingingLanguage.windows(start, duration)) {
                                check()
                                val pcm = decoder.decode(uri, listOf(window), checkCancelled = ::check) ?: error("Cannot decode language sample")
                                check(pcm.size >= (window.last + 1 - window.first) * 16 - 1600) { "Truncated language sample" }
                                val scores = tagger.analyzeWindows(pcm, ::check).first?.let(AudioTagger::scores)
                                if (scores == null || scores.vocal < TagClassifier.VOCAL_THRESHOLD) samples += LanguageSample(scores?.vocal)
                                else {
                                    val (language, probability, speech) = engine.recognize(pcm, deadline)
                                    check()
                                    samples += LanguageSample(scores.vocal, language, probability, speech)
                                }
                            }
                            check(); validate(track)
                            repository.save(track, SingingLanguage.aggregate(samples))
                        } catch (e: CancellationException) { throw e }
                        catch (e: Exception) {
                            currentCoroutineContext().ensureActive()
                            mutable.value = mutable.value.copy(failed = task.value.failed + 1, error = e.message)
                        }
                        mutable.value = mutable.value.copy(processed = task.value.processed + 1)
                    }
                    native = null
                }
                ensureActive()
                mutable.value = mutable.value.copy(state = if (task.value.failed > 0) "completed_with_errors" else "completed")
            } catch (e: ModelDownloadRequired) { mutable.value = mutable.value.copy(state = "download_required", error = null) }
            catch (e: CancellationException) { mutable.value = mutable.value.copy(state = "cancelled") }
            catch (e: Exception) { mutable.value = if (!currentCoroutineContext().isActive) mutable.value.copy(state = "cancelled") else mutable.value.copy(state = "failed", error = e.message) }
            catch (e: LinkageError) { mutable.value = mutable.value.copy(state = "failed", error = e.message) }
            finally { native = null; connection?.disconnect(); connection = null; tagger.close() }
        }
    }

    @Synchronized fun cancel() {
        if (!task.value.busy) return
        mutable.value = mutable.value.copy(state = "cancelling")
        work?.cancel(); native?.cancel(); connection?.disconnect()
    }

    private suspend fun validate(track: TrackEntity) {
        val current = db.trackDao().byId(track.id)
        check(current?.trackKey == track.trackKey && current.languageSignature() == track.languageSignature()) { "Recording changed; rescan first" }
        context.contentResolver.openFileDescriptor(MediaStoreSource.contentUri(track.volumeName, track.mediaStoreId), "r").use { fd ->
            check(fd != null && (fd.statSize < 0 || fd.statSize == track.size)) { "Recording changed or unavailable" }
            val info = android.system.Os.fstat(fd.fileDescriptor)
            check(android.system.OsConstants.S_ISREG(info.st_mode) && info.st_mtim.tv_sec == track.dateModified &&
                (track.modifiedNanos == null || info.st_mtim.tv_nsec == track.modifiedNanos.toLong())) { "Recording changed; rescan first" }
        }
    }

    private suspend fun ensureModel(confirmed: Boolean) {
        val digest = MessageDigest.getInstance("SHA-256")
        fun hash(file: File) = file.inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) { val count = input.read(buffer); if (count < 0) break; digest.update(buffer, 0, count) }
            digest.digest().joinToString("") { "%02x".format(it) }
        }
        if (modelReady && hash(model) == MODEL_SHA256) return
        if (!confirmed) throw ModelDownloadRequired()
        model.parentFile!!.mkdirs()
        val temporary = File(model.parentFile, "ggml-tiny.download")
        try {
            mutable.value = mutable.value.copy(state = "preparing")
            val conn = URI("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin").toURL().openConnection() as HttpURLConnection
            connection = conn
            conn.connectTimeout = 15_000; conn.readTimeout = 15_000
            conn.setRequestProperty("User-Agent", "JeekMusic/1.0")
            val deadline = SystemClock.elapsedRealtime() + 10 * 60_000
            conn.inputStream.use { input -> temporary.outputStream().use { output ->
                val buffer = ByteArray(64 * 1024); var total = 0L
                while (true) {
                    currentCoroutineContext().ensureActive()
                    check(SystemClock.elapsedRealtime() < deadline) { "Model download timed out" }
                    val count = input.read(buffer); if (count < 0) break
                    total += count; check(total <= MODEL_BYTES) { "Unexpected model size" }
                    output.write(buffer, 0, count)
                    mutable.value = mutable.value.copy(downloaded = total)
                }
            } }
            check(temporary.length() == MODEL_BYTES && hash(temporary) == MODEL_SHA256) { "Model checksum mismatch" }
            currentCoroutineContext().ensureActive()
            check(temporary.renameTo(model)) { "Cannot install model" }
        } finally { temporary.delete(); connection?.disconnect(); connection = null }
    }
    companion object {
        const val MODEL_BYTES = 77_691_713L
        const val MODEL_SHA256 = "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"
    }
}
