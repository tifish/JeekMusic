package com.tinyfish.jeekmusic.data.scanner

import android.os.FileObserver
import android.os.SystemClock
import android.system.Os
import com.tinyfish.jeekmusic.data.sync.LanAtomicFiles
import com.tinyfish.jeekmusic.data.sync.LanLocalDeferred
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import org.json.JSONObject
import java.io.File
import javax.inject.Inject
import javax.inject.Singleton

internal data class LocalIndexResult(val hit: Boolean, val refreshed: Int = 0, val shared: Int = 0)

/** Complete filesystem inventories shared with exact observations from the library scanner.
 * MediaStore's song list is never used to infer filesystem absence or sidecar deletion.
 * All cached observations require uninterrupted directory watches; no persisted freshness guess.
 */
@Singleton
class LocalFileIndex @Inject constructor() {
    private data class Snapshot(val root: String, val selection: List<String>, val rows: Map<String, String>, val revision: Long, val checkedAt: Long)
    private data class Observation(val revision: Long, val json: String)
    private val lock = Any()
    private var revision = 0L
    private var topology = 0L
    private var watching = false
    private var rebuild = true
    private val snapshots = linkedMapOf<String, Snapshot>()
    private val changes = linkedMapOf<String, Long>()
    private val observations = linkedMapOf<String, Observation>()
    internal val needsWatches get() = synchronized(lock) { rebuild }

    internal fun stopWatching() = synchronized(lock) {
        revision++; topology++; watching = false; rebuild = true
        snapshots.clear(); changes.clear(); observations.clear()
    }
    internal fun watchTicket() = synchronized(lock) { topology }
    internal fun watched(healthy: Boolean, ticket: Long = watchTicket()) = synchronized(lock) {
        watching = healthy && ticket == topology; rebuild = !healthy || ticket != topology; revision++
    }
    private fun selected(path: String, selection: List<String>) = selection.any { it.isEmpty() || path == it || path.startsWith("$it/") }
    private fun relative(root: String, path: String): String? = path.takeIf { it.startsWith(root + File.separator) }?.substring(root.length + 1)?.replace(File.separatorChar, '/')
    private fun supported(path: String) = path.split('/').none { it.startsWith('.') } && path.substringAfterLast('.').lowercase(java.util.Locale.ROOT) in EXTENSIONS
    internal fun changed(directory: File, name: String?, event: Int) = synchronized(lock) {
        if (event and MASK == 0 || name?.startsWith('.') == true) return@synchronized
        if (name == null || event and (0x40000000 or 0x4000 or 0x8000 or FileObserver.DELETE_SELF or FileObserver.MOVE_SELF) != 0 || changes.size >= MAX_FILES) {
            revision++; topology++; snapshots.clear(); observations.clear(); changes.clear(); rebuild = true
        } else {
            val path = File(directory, name).absolutePath
            if (supported(name)) { changes[path] = ++revision; observations.remove(path) }
        }
    }

    /** The caller already opened this descriptor for its normal library scan. Share its
     * exact fstat, using the descriptor's real path, without a second open or content read.
     */
    fun observeDescriptor(fd: Int): LongArray {
        val ticket = synchronized(lock) { revision.takeIf { watching && snapshots.isNotEmpty() } }
        val identity = LanAtomicFiles.identity(fd)
        if (ticket == null) return identity
        val path = try { Os.readlink("/proc/self/fd/$fd") } catch (_: Exception) { return identity }
        synchronized(lock) {
            if (!watching || ticket != revision || !supported(path) || snapshots.values.none { s -> relative(s.root, path)?.let { selected(it, s.selection) } == true }) return identity
            if (changes.size >= MAX_FILES || observations.size >= MAX_FILES) { snapshots.clear(); changes.clear(); observations.clear(); revision++; return identity }
            val ns = Math.addExact(Math.multiplyExact(identity[3], 1_000_000_000L), identity[4])
            val row = JSONObject().put("known", true).put("exists", true).put("size", identity[2]).put("modifiedNs", ns).put("precisionNs", 1L).put("metadataVersion", "m2:${identity[2]}:$ns")
            val sequence = ++revision
            changes[path] = sequence; observations[path] = Observation(sequence, row.toString())
        }
        return identity
    }

    internal suspend fun read(key: String, root: String, selection: List<String>,
        observe: suspend (String) -> JSONObject,
        fullScan: suspend (suspend (JSONObject) -> Unit, (String) -> Unit) -> Unit,
        emit: suspend (JSONObject) -> Unit, unavailable: (String) -> Unit,
        now: () -> Long = SystemClock::elapsedRealtime): LocalIndexResult {
        val start = synchronized(lock) {
            val cached = snapshots[key]?.takeIf { watching && !rebuild && it.root == root && it.selection == selection && now() - it.checkedAt in 0 until FALLBACK_MS }
            Triple(revision, cached, if (cached == null) emptyMap() else changes.filterValues { it > cached.revision }.toMap())
        }
        val rows = linkedMapOf<String, String>()
        var complete = true; var refreshed = 0; var shared = 0
        val cached = start.second
        if (cached != null) {
            rows.putAll(cached.rows)
            for ((absolute, sequence) in start.third) {
                currentCoroutineContext().ensureActive()
                val path = relative(root, absolute)?.takeIf { selected(it, selection) } ?: continue
                val common = synchronized(lock) { observations[absolute]?.takeIf { it.revision == sequence }?.json }
                val row = if (common != null) { shared++; JSONObject(common) } else { refreshed++; observe(path) }
                if (!row.optBoolean("known", true)) { complete = false; rows[path] = row.put("path", path).toString() }
                else if (row.optBoolean("exists")) rows[path] = row.put("path", path).toString()
                else rows.remove(path)
            }
        } else {
            fullScan({ row ->
                if (!row.optBoolean("known", true)) complete = false
                rows[row.getString("path")] = row.toString()
                // Retain streaming first transfers; never hold an inventory lock over I/O.
                emit(row)
            }, { path -> complete = false; unavailable(path) })
        }
        if (!complete) throw LanLocalDeferred("local_snapshot_incomplete")
        currentCoroutineContext().ensureActive()
        if (rows.size > MAX_FILES) throw LanLocalDeferred("scan_file_budget")
        if (rows.values.sumOf { JSONObject(it).optLong("size") } > 500L * 1024 * 1024 * 1024) throw LanLocalDeferred("scan_byte_budget")
        synchronized(lock) {
            if (watching && !rebuild && revision == start.first) {
                snapshots.remove(key)
                if (rows.values.sumOf { it.length.toLong() * 2 } <= MAX_MEMORY) {
                    snapshots[key] = Snapshot(root, selection.toList(), rows.toMap(), revision, if (cached != null) cached.checkedAt else now())
                    while (snapshots.size > 8 || snapshots.values.sumOf { s -> s.rows.values.sumOf { it.length.toLong() * 2 } } > MAX_MEMORY) snapshots.remove(snapshots.keys.first())
                }
            }
        }
        if (cached != null) for (row in rows.values) { currentCoroutineContext().ensureActive(); emit(JSONObject(row)) }
        return LocalIndexResult(cached != null, refreshed, shared)
    }
    companion object {
        internal const val FALLBACK_MS = 900_000L
        private const val MAX_FILES = 100_000
        private const val MAX_MEMORY = 32L * 1024 * 1024
        internal const val MASK = FileObserver.CLOSE_WRITE or FileObserver.MOVED_FROM or FileObserver.MOVED_TO or FileObserver.CREATE or FileObserver.DELETE or FileObserver.DELETE_SELF or FileObserver.MOVE_SELF or FileObserver.ATTRIB or FileObserver.MODIFY or 0x4000 or 0x8000
        private val EXTENSIONS = setOf("mp3", "flac", "m4a", "mp4", "aac", "ogg", "oga", "opus", "wav", "mka", "amr", "cue", "lrc", "jpg", "jpeg", "png", "webp")
    }
}
