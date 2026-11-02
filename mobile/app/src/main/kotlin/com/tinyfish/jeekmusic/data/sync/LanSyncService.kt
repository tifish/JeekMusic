package com.tinyfish.jeekmusic.data.sync

import android.content.Context
import android.content.Intent
import android.media.MediaScannerConnection
import android.net.Uri
import android.os.Build
import android.os.storage.StorageManager
import android.provider.DocumentsContract
import android.system.Os
import android.util.Base64
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.library.*
import com.tinyfish.jeekmusic.data.scanner.LibraryScanner
import com.tinyfish.jeekmusic.data.scanner.SafPaths
import com.tinyfish.jeekmusic.di.ApplicationScope
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.security.MessageDigest
import java.nio.file.Files
import java.nio.file.attribute.FileTime
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import javax.inject.Inject
import javax.inject.Singleton

data class LanStatus(val running: Boolean = false, val text: String = "", val done: Long = 0, val total: Long = 0, val problems: List<String> = emptyList(), val timeDetails: List<String> = emptyList(), val precisionNs: Long = 0,val fileActions: Map<String,Int> = emptyMap(),val fileConflicts: List<JSONObject> = emptyList(),val fileDeferredCount: Int=0,val fileProcessed: Int=0)

@Singleton
class LanSyncService @Inject constructor(
    @param:ApplicationContext private val context: Context,
    val store: LanSyncStore,
    private val ledgerStore: LanSyncLedger,
    private val fileDatabase: LanFileDatabase,
    private val tree: DocumentTree,
    private val library: LibraryRepository,
    private val scanner: LibraryScanner,
    private val db: JeekDatabase,
    private val playing: PlayingFiles,
    private val sync: SyncService,
    private val preferences: com.tinyfish.jeekmusic.data.preference.PreferenceService,
    @param:ApplicationScope private val scope: CoroutineScope,
) {
    private val gate = Mutex()
    @Volatile private var job: Job? = null
    private val fileSynchronizer by lazy { LanFileSynchronizer(context,fileDatabase,playing) }
    @Volatile private var transport: LanProtocol? = null
    private val _status = MutableStateFlow(LanStatus())
    val status = _status.asStateFlow()
    private val _revision = MutableStateFlow(0)
    val revision = _revision.asStateFlow()
    private val partials = File(context.filesDir, "lan-partials").apply { mkdirs() }
    // Loaded only inside gate on IO, never during UI/Hilt construction. This service is the
    // only ledger writer; its gate serializes all mutations, including local recovery/cleanup.
    private var ledger = JSONObject()
    private var ledgerLoaded = false
    private fun loadLedger() { if (!ledgerLoaded) { ledger = ledgerStore.read(); ledgerLoaded = true } }
    private fun saveLedger() {
        try { ledgerStore.write(ledger) }
        catch (e: LanLedgerException) { ledgerLoaded = false; throw e } // Recovery reloads the confirmed disk state.
    }
    private fun report() { _revision.value++ }
    private fun problem(e: Exception): String = if (e is java.net.SocketException || e is java.net.SocketTimeoutException || e is javax.net.ssl.SSLException || e is java.io.EOFException) context.getString(R.string.lan_network_error)
        else e.message?.lineSequence()?.firstOrNull()?.take(240) ?: e.javaClass.simpleName
    private fun protocol() = LanProtocol(store.read().getJSONObject("connection")).also { transport?.close(); transport = it }

    suspend fun pair(invitation: String) = withContext(Dispatchers.IO) {
        gate.withLock {
            require(job?.isActive != true) { "Cancel sync first" }
            val uri = LanInvitation.parse(invitation)
            require(store.mappings().isEmpty() || store.read().optJSONObject("connection")?.optString("pin") == uri.pin) { "Remove existing mappings before pairing another PC" }
            val id = sync.lanDeviceId()
            val connection = JSONObject().put("host", uri.host).put("port", uri.port).put("pin", uri.pin).put("deviceId", id)
            val client = LanProtocol(connection); transport = client
            val response = client.call("pair", JSONObject().put("invitation", uri.token).put("name", Build.MODEL))
            client.close()
            require(response.optInt("protocol") == 4) { "Upgrade the PC for LAN protocol 4" }
            connection.put("token", response.getString("token")).put("name", response.optString("name", uri.host))
            store.update { it.put("connection", connection) }; report()
            _status.value = LanStatus(text = context.getString(R.string.lan_paired_identity, connection.getString("name"), uri.endpoint))
        }
    }
    suspend fun roots(): List<JSONObject> = withContext(Dispatchers.IO) {
        val a = protocol().call("roots").getJSONArray("roots"); List(a.length()) { a.getJSONObject(it) }
    }
    suspend fun directories(root: String, path: String): List<String> = withContext(Dispatchers.IO) {
        LanProtocol.relative(path, true)
        val result = mutableListOf<String>()
        protocol().use { client ->
            do {
                val page = client.call("tree", JSONObject().put("rootId", root).put("path", path).put("offset", result.size))
                val a = page.getJSONArray("directories")
                repeat(a.length()) { result += a.getString(it).also { p -> LanProtocol.relative(p) } }
                require(result.size <= 100_000) { "Directory budget exceeded" }
            } while(a.length() == page.optInt("pageSize", Int.MAX_VALUE))
        }
        result
    }
    suspend fun estimate(root: String, selected: Set<String>): JSONObject = withContext(Dispatchers.IO) {
        require(selected.isNotEmpty()) { "Choose at least one folder" }
        protocol().call("plan", JSONObject().put("rootId", root).put("selection", JSONArray(selected.toList())))
    }
    /** SAF selection is mandatory, even though the current library supports local ExternalStorageProvider only. */
    suspend fun saveMapping(root: JSONObject, selected: Set<String>, target: Uri) = withContext(Dispatchers.IO) {
        gate.withLock {
            check(job?.isActive != true) { "Cancel sync before editing mappings" }
            require(target.authority == "com.android.externalstorage.documents") { "Only local storage / SD card SAF folders are supported" }
            require(selected.isNotEmpty()) { "Choose at least one folder" }
            selected.forEach { LanProtocol.relative(it, true) }
            val rootId = root.getString("rootId"); require(rootId.matches(Regex("[0-9a-f]{32}")))
            context.contentResolver.takePersistableUriPermission(target, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            val id = DocumentsContract.getTreeDocumentId(target)
            val path = SafPaths.fromTreeDocumentId(id) ?: error("Unsupported local storage")
            val parent = LibraryFolderEntity(treeUri = target.toString(), treeDocumentId = id, volumeName = path.volumeName, relativePath = path.relativePath,
                displayName = root.getString("name"), addedAt = System.currentTimeMillis())
            LanMappingTargets.requireSeparate(store.mappings(), rootId, path.volumeName, path.relativePath)
            localBoundary(parent)
            library.registerSyncDestination(parent)
            val old = store.mappings().firstOrNull { LanMappingTargets.isCurrent(it) && it.getString("rootId") == rootId && it.optString("treeUri") == target.toString() && it.getJSONArray("selection").let { a -> List(a.length()) { a.getString(it) }.toSet()==selected } }
            val mapping = JSONObject().put("layoutVersion",LanMappingTargets.VERSION).put("rootId", rootId).put("name", root.getString("name")).put("selection", JSONArray(selected.toList().sorted()))
                .put("treeUri", target.toString()).put("documentId", id).put("volume", path.volumeName).put("relative", path.relativePath)
                .put("key", old?.getString("key") ?: java.util.UUID.randomUUID().toString()).put("enabled", true)
            old?.optString("directoryIdentity")?.takeIf { it.isNotEmpty() }?.let { mapping.put("directoryIdentity",it) }
            store.saveMapping(mapping); report()
        }
    }
    fun setEnabled(root: String, enabled: Boolean) {
        check(job?.isActive != true) { "Cancel sync first" }
        val mapping = store.mappings().single { it.getString("rootId") == root }; if(enabled) LanMappingTargets.requireCurrent(mapping); mapping.put("enabled", enabled); store.saveMapping(mapping); report()
    }
    fun forget(root: String) {
        check(job?.isActive != true) { "Cancel sync first" }
        store.update { it.put("mappings", JSONArray(store.mappings().filterNot { m -> m.getString("rootId") == root })) }; report()
    }
    fun cancel() { job?.cancel(); transport?.cancel() }
    fun start() {
        if (job?.isActive == true) return
        job = scope.launch(Dispatchers.IO) {
            gate.withLock {
                _status.value = LanStatus(true, context.getString(R.string.lan_verifying))
                val problems = mutableListOf<String>(); val timeDetails = mutableListOf<String>(); var precisionNs = 0L; var done = 0L; var total = 0L
                try {
                    val obsolete=store.mappings().count { !LanMappingTargets.isCurrent(it) }
                    if(obsolete>0) problems += context.getString(R.string.lan_mapping_reselect)
                    loadLedger()
                    recoverPending()
                    drainRefresh()
                    val actions=mutableMapOf<String,Int>();val conflicts=mutableListOf<JSONObject>();var deferredCount=0;var processed=0;var lastProgress=0L
                    for (mapping in store.mappings().filter { LanMappingTargets.isCurrent(it) && it.optBoolean("enabled", true) }) {
                        ensureActive()
                        val client=protocol()
                        try {
                            val result=fileSynchronizer.synchronize(mapping,client,{ store.saveMapping(mapping);report() },{ path,progress ->
                                val now=android.os.SystemClock.elapsedRealtime()
                                if(now-lastProgress>=250) {
                                    lastProgress=now
                                    _status.value=LanStatus(true,path,done+progress.bytes,0,problems.toList(),fileActions=(actions.keys+progress.actions.keys).associateWith { (actions[it] ?: 0)+(progress.actions[it] ?: 0) },fileConflicts=(conflicts+progress.conflicts).take(100).map { JSONObject(it.toString()) },fileDeferredCount=deferredCount+progress.deferredCount,fileProcessed=processed+progress.processed)
                                }
                            },{ path,entry,deleted -> queueRefresh(mapping,path,entry,deleted) },{ if(fileDatabase.refreshPaths(mapping.getString("key")).size>=200)drainRefresh() })
                            for((action,count) in result.actions)actions[action]=(actions[action] ?: 0)+count
                            conflicts.addAll(result.conflicts.take(100-conflicts.size));deferredCount+=result.deferredCount;processed+=result.processed;done+=result.bytes
                            for(reason in result.deferred.take((100-problems.size).coerceAtLeast(0)))problems+=reason
                            _status.value=_status.value.copy(fileActions=actions.toMap(),fileConflicts=conflicts.map { JSONObject(it.toString()) },fileDeferredCount=deferredCount,fileProcessed=processed)
                        } finally { client.close() }
                        drainRefresh()
                    }
                    ensureActive()
                    val dataClient = protocol()
                    val writable = dataClient.call("capabilities").getJSONObject("permissions").optBoolean("writeSync")
                    if (writable) {
                        val remote = dataClient.call("state", sync.exportLan())
                        sync.acceptLan(remote)
                        try { preferences.synchronize(includeUserData=false) } catch(e: CancellationException) { throw e } catch(_: Exception) { problems += "Personal preference delta pending: peer offline or older protocol" }
                    }
                    _status.value = _status.value.copy(running=false,text=context.getString(if(problems.isEmpty() && _status.value.fileConflicts.isEmpty()) R.string.lan_done else R.string.lan_issues),done=done,total=0,problems=problems,timeDetails=timeDetails,precisionNs=precisionNs)
                } catch (e: CancellationException) {
                    _status.value = _status.value.copy(running=false,text=context.getString(R.string.lan_cancelled),done=done,total=total,problems=problems,timeDetails=timeDetails,precisionNs=precisionNs)
                } catch (e: Exception) {
                    // Transport exceptions can contain connection addresses, but never serialize the invitation/token.
                    _status.value = if (!currentCoroutineContext().isActive) _status.value.copy(running=false,text=context.getString(R.string.lan_cancelled),done=done,total=total,problems=problems,timeDetails=timeDetails,precisionNs=precisionNs)
                        else _status.value.copy(running=false,text=context.getString(R.string.lan_failed),done=done,total=total,problems=problems+problem(e),timeDetails=timeDetails,precisionNs=precisionNs)
                } finally { transport?.close(); transport = null; report(); startRecovery() }
            }
        }
    }
    /** Local recovery only; this never connects to a PC or starts a new download. */
    fun startRecovery() {
        scope.launch(Dispatchers.IO) { gate.withLock {
            try { loadLedger(); recoverPending(); drainRefresh() }
            catch (e: Exception) { _status.value = _status.value.copy(running = false, problems = (_status.value.problems + problem(e)).distinct()) }
        } }
    }
    private fun recoverPending() {
        val pending=ledger.optJSONObject("pending") ?: return
        ledger.put("obsoletePending",pending);ledger.remove("pending");saveLedger()
    }
    private fun audioDependencies(entry: JSONObject): Set<String> = entry.optJSONArray("dependencies")?.let { a ->
        List(a.length()) { LanProtocol.relative(a.getString(it)) }.filter { it.substringAfterLast('.').lowercase() in setOf("wav", "flac") }.toSet()
    }.orEmpty()
    private fun queueRefresh(mapping: JSONObject,path: String,entry: JSONObject?=null,deleted: Boolean=false) {
        val paths=linkedSetOf(LanProtocol.relative(path));val audio=entry?.let(::audioDependencies).orEmpty()
        if(path.endsWith(".cue",true)) { paths.addAll(fileDatabase.cueAudio(mapping.getString("key"),path));paths.addAll(audio) }
        fileDatabase.queueRefresh(mapping.getString("key"),path,JSONObject().put("hash",entry?.optString("hash")).put("deleted",deleted).put("audio",JSONArray(audio.toList())).put("paths",JSONArray(paths.toList())))
    }
    private suspend fun drainRefresh() {
        // Legacy refresh entries are inert, just like legacy file transactions.
        if(ledger.has("refresh")) { ledger.remove("refresh");saveLedger() }
        for(mapping in store.mappings().filter { LanMappingTargets.isCurrent(it) && it.optString("directoryIdentity").isNotEmpty() }) {
            val key=mapping.getString("key")
            while(true) {
                val queued=fileDatabase.refreshPaths(key);if(queued.isEmpty())break
                LanLocalFiles.fromMapping(context,mapping,fileDatabase,false).use { local ->
                    for(path in queued) {
                        val update=fileDatabase.refresh(key,path) ?: continue
                        if(path.endsWith(".cue",true)) {
                            val actual=local.observe(path);val missing=actual.isNull("hash")
                            if(update.optBoolean("deleted") && missing)fileDatabase.bindCue(key,path,emptyList())
                            else if(!missing && actual.getString("hash")==update.optString("hash")) {
                                val audio=update.getJSONArray("audio");fileDatabase.bindCue(key,path,List(audio.length()) { audio.getString(it) })
                            }
                        }
                        val paths=update.getJSONArray("paths");for(offset in 0 until paths.length() step 200)refresh(mapping,List(minOf(200,paths.length()-offset)) { paths.getString(offset+it) })
                        fileDatabase.refreshed(key,path)
                    }
                }
            }
        }
    }
    private fun folder(m: JSONObject): LibraryFolderEntity {
        LanMappingTargets.requireCurrent(m)
        require(DocumentsContract.getTreeDocumentId(Uri.parse(m.getString("treeUri")))==m.getString("documentId")) { "Mapping must use the selected directory itself" }
        return LibraryFolderEntity(treeUri=m.getString("treeUri"),treeDocumentId=m.getString("documentId"),volumeName=m.getString("volume"),relativePath=m.getString("relative"),displayName=m.getString("name"),addedAt=0)
    }
    private fun localBoundary(folder: LibraryFolderEntity, path: String = "") {
        val volume = context.getSystemService(StorageManager::class.java).storageVolumes.singleOrNull { it.mediaStoreVolumeName == folder.volumeName }?.directory ?: error("Storage offline")
        val root = File(volume, folder.relativePath.trimEnd('/'))
        require(root.canonicalPath == root.absolutePath) { "Linked destination root rejected" }
        var target = root
        for (part in path.split('/').filter { it.isNotEmpty() }) {
            target = File(target, part)
            require(target.canonicalPath == target.absolutePath) { "Linked destination rejected" }
        }
    }
    @Deprecated("Bidirectional sync has no local-only cleanup")
    suspend fun clean(root: String): Unit=throw IllegalStateException("Remove the mapping to keep files. Ordinary tracked deletions propagate during sync.")
    fun resolveConflict(id: String,choice: String) {
        check(job?.isActive!=true) { "Cancel sync first" }
        scope.launch(Dispatchers.IO) {
            if(!gate.tryLock())return@launch
            try { fileSynchronizer.choose(id,choice) }finally { gate.unlock() }
            start()
        }
    }
    suspend fun backups(): List<JSONObject> = withContext(Dispatchers.IO) { gate.withLock { store.mappings().filter { LanMappingTargets.isCurrent(it) }.flatMap { fileDatabase.backupRows(it.getString("key")) }.take(100) } }
    suspend fun maintainBackup(operation: String,purge: Boolean)=withContext(Dispatchers.IO) { gate.withLock {
        check(job?.isActive!=true) { "Cancel sync first" }
        val row=fileDatabase.receipt(operation) ?: error("Backup receipt missing")
        val mapping=store.mappings().single { it.optString("key")==row.getString("mapping") }
        val part=File(context.cacheDir,"lan-restore-${java.util.UUID.randomUUID()}.part")
        try { LanLocalFiles.fromMapping(context,mapping,fileDatabase,false).use { it.maintainBackup(row,purge,part) };if(!purge) { queueRefresh(mapping,row.getString("path"),JSONObject().put("hash",row.getString("expectedHash")));drainRefresh() };report() }finally { part.delete() }
    } }
    private suspend fun refresh(m: JSONObject, paths: List<String>) {
        if (paths.isEmpty()) return
        val volume = context.getSystemService(StorageManager::class.java).storageVolumes.singleOrNull { it.mediaStoreVolumeName == m.getString("volume") }?.directory ?: error("Storage offline")
        val absolute = paths.map { File(volume, m.getString("relative") + it).path }
        withTimeout(60_000) {
            suspendCancellableCoroutine { continuation ->
                val remaining = java.util.concurrent.atomic.AtomicInteger(absolute.size)
                MediaScannerConnection.scanFile(context, absolute.toTypedArray(), null) { _, _ -> if (remaining.decrementAndGet() == 0 && continuation.isActive) continuation.resume(Unit) }
            }
        }
        scanner.refreshFiles(paths.map { m.getString("volume") + ":" + m.getString("relative") + it }.toSet())
    }
}
