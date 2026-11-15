package com.tinyfish.jeekmusic.data.scanner

import android.content.Context
import android.os.FileObserver
import android.system.Os
import com.tinyfish.jeekmusic.data.sync.LanAtomicFiles
import com.tinyfish.jeekmusic.data.sync.LanLocalDeferred
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import org.json.JSONObject
import java.io.File
import javax.inject.Inject
import javax.inject.Singleton

internal data class LocalIndexResult(val hit: Boolean, val refreshed: Int = 0, val shared: Int = 0, val revision: Long = 0)

/** App-owned changes and delayed MediaStore hints update a durable inventory. External
 * sidecar edits are deliberately discovered only by an explicit refresh. */
@Singleton
class LocalFileIndex internal constructor(private val store: LocalIndexStore? = null) {
    @Inject constructor(@ApplicationContext context: Context): this(LocalIndexStore(context))
    private data class Observation(val revision: Long, val json: String)
    private val lock = Any()
    private var loaded = false
    private var revision = 0L
    private var epoch = 0L
    internal val refreshEpoch get()=synchronized(lock) { epoch }
    private val snapshots = linkedMapOf<String, LocalIndexSnapshot>()
    private val changes = linkedMapOf<String, Long>()
    private val observations = linkedMapOf<String, Observation>()
    private val _changed = MutableSharedFlow<Unit>(extraBufferCapacity=1)
    val changed = _changed.asSharedFlow()
    internal val fullScans=java.util.concurrent.atomic.AtomicLong()
    private fun load() {
        if(loaded) return
        changes.putAll(store?.dirty().orEmpty()); revision=store?.sequence() ?: 0; loaded=true
    }
    private fun snapshot(key: String): LocalIndexSnapshot? {
        load()
        return snapshots[key] ?: store?.load(key)?.also { snapshots[key]=it }
    }
    private fun selected(path: String, selection: List<String>) = selection.any { it.isEmpty() || path == it || path.startsWith("$it/") }
    private fun relative(root: String, path: String): String? = path.takeIf { it.startsWith(root + File.separator) }?.substring(root.length + 1)?.replace(File.separatorChar, '/')
    private fun supported(path: String) = path.replace(File.separatorChar,'/').split('/').none { it.startsWith('.') } && path.substringAfterLast('.').lowercase(java.util.Locale.ROOT) in EXTENSIONS
    internal fun hasSnapshot(key: String) = synchronized(lock) { snapshot(key)!=null }
    internal fun pendingPaths(key: String,through: Long=Long.MAX_VALUE): Set<String>? = synchronized(lock) {
        val cached=snapshot(key) ?: return@synchronized null
        changes.filterValues { it>cached.confirmed && it<=through }.keys.mapNotNull { relative(cached.root,it)?.takeIf { path -> selected(path,cached.selection) } }.toSet()
    }
    internal fun row(key: String,path: String): JSONObject? = synchronized(lock) { snapshot(key)?.rows?.get(path)?.let(::JSONObject) }
    internal fun confirm(key: String, ticket: Long) = synchronized(lock) {
        val old=snapshot(key) ?: return@synchronized
        val next=old.copy(confirmed=maxOf(old.confirmed,minOf(ticket,old.revision)))
        if(next.confirmed==old.confirmed)return@synchronized
        store?.save(key,next,old); snapshots[key]=next
        val consumed=store?.minimumConfirmed() ?: snapshots.values.minOf { it.confirmed }
        val retired=changes.filterValues { it<=consumed }.keys
        retired.forEach { changes.remove(it);observations.remove(it) }
    }
    internal fun retainMappings(keys: Set<String>)=synchronized(lock) {
        snapshots.keys.filter { it.substringBefore(':') !in keys }.forEach(snapshots::remove)
        store?.retainMappings(keys)
    }
    fun requestRefresh() = synchronized(lock) {
        load(); epoch++; snapshots.clear(); observations.clear(); store?.invalidate(); _changed.tryEmit(Unit)
    }
    internal fun changed(directory: File, name: String?, event: Int) = synchronized(lock) {
        if(event and MASK==0 || name==null || !supported(name)) return@synchronized
        load()
        val path=File(directory,name).absolutePath
        revision=store?.mark(path) ?: (revision+1)
        changes[path]=revision; observations.remove(path); _changed.tryEmit(Unit)
    }
    /** Shared fstat from the library's existing descriptor. Observing an unchanged file
     * never creates another sync hint or invalidates the entire inventory. */
    fun observeDescriptor(fd: Int): LongArray {
        val identity=LanAtomicFiles.identity(fd)
        val path=try { Os.readlink("/proc/self/fd/$fd") } catch(_: Exception) { return identity }
        synchronized(lock) {
            load()
            val sequence=changes[path] ?: return identity
            val ns=Math.addExact(Math.multiplyExact(identity[3],1_000_000_000L),identity[4])
            val row=JSONObject().put("known",true).put("exists",true).put("size",identity[2]).put("modifiedNs",ns).put("precisionNs",1L).put("metadataVersion","m2:${identity[2]}:$ns")
            observations[path]=Observation(sequence,row.toString())
        }
        return identity
    }
    internal fun mediaCheckpoint(key: String): JSONObject? = synchronized(lock) { store?.media(key) }
    internal fun mediaCheckpoint(key: String,value: JSONObject) = synchronized(lock) { store?.media(key,value) }

    internal suspend fun read(key: String, root: String, selection: List<String>,
        observe: suspend (String) -> JSONObject,
        fullScan: suspend (suspend (JSONObject) -> Unit, (String) -> Unit) -> Unit,
        emit: suspend (JSONObject) -> Unit, unavailable: (String) -> Unit): LocalIndexResult {
        val start=synchronized(lock) {
            val cached=snapshot(key)?.takeIf { it.root==root && it.selection==selection }
            Triple(revision,cached,changes.filterValues { it>(cached?.revision ?: -1) }.toMap()) to epoch
        }
        val cached=start.first.second
        val rows=linkedMapOf<String,String>()
        var complete=true; var refreshed=0; var shared=0
        if(cached!=null) {
            rows.putAll(cached.rows)
            for((absolute,sequence) in start.first.third) {
                currentCoroutineContext().ensureActive()
                val path=relative(root,absolute)?.takeIf { selected(it,selection) } ?: continue
                val common=synchronized(lock) { observations[absolute]?.takeIf { it.revision==sequence }?.json }
                val row=if(common!=null) { shared++; JSONObject(common) } else { refreshed++; observe(path) }
                if(!row.optBoolean("known",true)) complete=false
                else if(row.optBoolean("exists")) rows[path]=row.put("path",path).toString()
                else rows.remove(path)
            }
        } else {
            fullScans.incrementAndGet()
            fullScan({ row ->
                if(!row.optBoolean("known",true)) complete=false
                rows[row.getString("path")]=row.toString(); emit(row)
            }, { path -> complete=false; unavailable(path) })
        }
        if(!complete) throw LanLocalDeferred("local_snapshot_incomplete")
        currentCoroutineContext().ensureActive()
        if(rows.size>MAX_FILES) throw LanLocalDeferred("scan_file_budget")
        if(rows.values.sumOf { JSONObject(it).optLong("size") }>500L*1024*1024*1024) throw LanLocalDeferred("scan_byte_budget")
        synchronized(lock) {
            if(epoch==start.second) {
                val next=LocalIndexSnapshot(root,selection.toList(),rows.toMap(),start.first.first,cached?.confirmed ?: -1)
                store?.save(key,next,cached); snapshots[key]=next
                // Later hints survive a concurrent scan and refresh only those paths.
                while(snapshots.size>8) snapshots.remove(snapshots.keys.first())
            }
        }
        if(cached!=null) for(row in rows.values) { currentCoroutineContext().ensureActive(); emit(JSONObject(row)) }
        return LocalIndexResult(cached!=null,refreshed,shared,start.first.first)
    }
    companion object {
        private const val MAX_FILES=100_000
        internal const val MASK=FileObserver.CLOSE_WRITE or FileObserver.MOVED_FROM or FileObserver.MOVED_TO or FileObserver.CREATE or FileObserver.DELETE or FileObserver.ATTRIB or FileObserver.MODIFY
        private val EXTENSIONS=setOf("mp3","flac","m4a","mp4","aac","ogg","oga","opus","wav","mka","amr","cue","lrc","jpg","jpeg","png","webp")
    }
}
