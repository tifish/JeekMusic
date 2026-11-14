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
import kotlinx.coroutines.flow.update
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

internal class LanMappingBusyException : IllegalStateException("Cancel sync before editing mappings")

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
    @Volatile private var activeFileRoot: String? = null
    private val fileSynchronizer by lazy { LanFileSynchronizer(context,fileDatabase,playing) }
    private val automation by lazy { LanAutoSync(context,scope,this) }
    val schedule get()=automation.state
    fun initializeAutomation()=automation.initialize()
    internal fun isSyncRunning()=job?.isActive==true
    internal suspend fun awaitSync() { job?.join() }
    internal fun cancelScheduled() { job?.cancel();transport?.cancel() }
    internal suspend fun backgroundSync()=automation.background()
    internal suspend fun localWatchDirectories(mapping: JSONObject): List<File> = LanLocalFiles.fromMapping(context,mapping,fileDatabase,false).use { io ->
        // Only synchronization may persist a new directory pin. Watching never initializes one.
        io.watchDirectories(mapping.getJSONArray("selection").let { a -> List(a.length()) { a.getString(it) } })
    }
    internal fun pollFileRevision(mapping: JSONObject): String {
        LanProtocol(store.read().getJSONObject("connection")).use { client ->
            val opened=client.call("file_open",JSONObject().put("fileProtocol",3).put("rootId",mapping.getString("rootId")).put("mapping",mapping.getString("key")).put("selection",mapping.getJSONArray("selection")))
            if(opened.optBoolean("deferred") || opened.optBoolean("conflict"))throw java.io.IOException("scope_unavailable")
            return client.call("file_revision",JSONObject().put("fileProtocol",3).put("scope",opened.getString("scope"))).getString("revision")
        }
    }
    internal fun reportFileSchedule(value: JSONObject) {
        val connection=store.read().optJSONObject("connection") ?: return
        for(mapping in store.mappings().filter { LanMappingTargets.isCurrent(it) })runCatching {
            LanProtocol(connection).use { client ->
                val opened=client.call("file_open",JSONObject().put("fileProtocol",3).put("rootId",mapping.getString("rootId")).put("mapping",mapping.getString("key")).put("selection",mapping.getJSONArray("selection")))
                client.call("file_schedule",JSONObject().put("fileProtocol",3).put("scope",opened.getString("scope")).put("schedule",JSONObject(value.toString()).put("paused",!mapping.optBoolean("enabled",true))))
            }
        }
    }
    @Volatile private var transport: LanProtocol? = null
    private val _status = MutableStateFlow(LanStatus())
    val status = _status.asStateFlow()
    private val _conflictCount = MutableStateFlow(0)
    val conflictCount = _conflictCount.asStateFlow()
    private val _pendingConflictCount = MutableStateFlow(0)
    val pendingConflictCount = _pendingConflictCount.asStateFlow()
    private val _conflictError = MutableStateFlow(false)
    val conflictError = _conflictError.asStateFlow()
    private fun refreshConflicts() { _pendingConflictCount.value=fileDatabase.pendingIssueCount();_conflictCount.value=fileDatabase.issueCount();val rows=fileDatabase.issuePage();_status.update { it.copy(fileConflicts=rows) };report() }
    suspend fun conflictPage(offset: Int=0): List<JSONObject> = withContext(Dispatchers.IO) { fileDatabase.issuePage(offset) }
    private val conflictExchange=Mutex()
    private var conflictOffset=0
    private val missingConflictHints=mutableSetOf<String>()
    /** Independent, bounded control connection: never closes an active file transfer. */
    internal suspend fun exchangeConflictStatus(id: String?=null) {
        if(!conflictExchange.tryLock())return
        try {
            val connection=store.read().optJSONObject("connection") ?: return
            val enabled=store.mappings().filter { LanMappingTargets.isCurrent(it) && it.optBoolean("enabled",true) }.map { it.getString("key") }.toSet()
            if(enabled.isEmpty())return
            val rows=if(id!=null)listOfNotNull(fileDatabase.issue(id)) else fileDatabase.issuePage(conflictOffset).also { conflictOffset=if(it.size<100)0 else conflictOffset+100 }
            val current=rows.filter { it.optString("mapping") in enabled && it.optString("scope").isNotEmpty() }
            if(current.isEmpty())return
            LanProtocol(connection).use { client ->
                if(client.call("capabilities").optInt("conflictStatusProtocol")<1)return
                for((scopeId,group) in current.groupBy { it.getString("scope") }) {
                    fun call(op: String,args: JSONObject)=client.call(op,args.put("fileProtocol",3).put("scope",scopeId))
                    val snapshot=call("file_conflict_status",JSONObject().put("ids",JSONArray(group.map { it.getString("id") })))
                    val shared=snapshot.getJSONArray("rows")
                    repeat(shared.length()) { index ->
                        var row=shared.getJSONObject(index)
                        val key=row.getString("id")
                        val local=fileDatabase.issue(key) ?: return@repeat
                        val before=local.optString("choice").takeIf { !local.has("queuedChoice") }
                        val queued=local.optString("queuedChoice")
                        if(row.optString("choice") !in setOf("local","remote") && queued in setOf("local","remote")) {
                            val chosen=call("file_conflict_choose",JSONObject().put("id",key).put("choice",queued))
                            // Another device may win while this request is in flight. Read again next tick.
                            if(chosen.optBoolean("conflict") || chosen.optBoolean("deferred"))return@repeat
                            row=chosen
                        }
                        for(field in listOf("local","remote","choice","localChange","remoteChange","reportedAt"))local.put(field,row.opt(field) ?: JSONObject.NULL)
                        fileDatabase.updateIssue(local)
                        if(row.optString("choice") in setOf("local","remote") && before!=row.optString("choice"))automation.hint("conflict_choice")
                    }
                    val missing=snapshot.getJSONArray("missing")
                    repeat(missing.length()) { index ->
                        // Absence alone is not proof of file convergence. The reconciler must check it.
                        if(missingConflictHints.add(missing.getString(index)))automation.hint("conflict_changed")
                    }
                }
            }
        } finally { conflictExchange.unlock() }
    }
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
    private fun report() { _revision.update { it+1 } }
    init { fileDatabase.onIssuesChanged=::refreshConflicts;scope.launch(Dispatchers.IO) { refreshConflicts() } }
    private fun problem(e: Exception): String = if(e is LanRemoteException && e.code=="unauthorized") context.getString(R.string.lan_pairing_revoked)
        else if(e is LanRemoteException && e.code=="upgrade_required") context.getString(R.string.lan_paired_upgrade)
        else if (e is java.net.SocketException || e is java.net.SocketTimeoutException || e is javax.net.ssl.SSLException || e is java.io.EOFException) context.getString(R.string.lan_network_error)
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
            automation.configure()
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
        protocol().call("plan", JSONObject().put("fileProtocol",3).put("rootId", root).put("selection", JSONArray(selected.toList())))
    }
    /** SAF selection is mandatory, even though the current library supports local ExternalStorageProvider only. */
    suspend fun saveMapping(root: JSONObject, selected: Set<String>, target: Uri) = withContext(Dispatchers.IO) {
        // A sync round can hold this gate while waiting for the network. Never queue a UI save
        // behind it: the user must be able to cancel synchronization before retrying the edit.
        if (!gate.tryLock()) throw LanMappingBusyException()
        try {
            if (job?.isActive == true) throw LanMappingBusyException()
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
            automation.resume()
        } finally { gate.unlock() }
    }
    fun setEnabled(root: String, enabled: Boolean) {
        if(!enabled && activeFileRoot==root)cancelScheduled()
        val mapping = store.mappings().single { it.getString("rootId") == root }; if(enabled) LanMappingTargets.requireCurrent(mapping); mapping.put("enabled", enabled); store.saveMapping(mapping); report()
        if(enabled)automation.resume() else automation.configure()
    }
    fun forget(root: String) {
        check(job?.isActive != true) { "Cancel sync first" }
        store.update { it.put("mappings", JSONArray(store.mappings().filterNot { m -> m.getString("rootId") == root })) }; report()
        automation.configure()
    }
    fun cancel() { automation.cancel();cancelScheduled();scope.launch(Dispatchers.IO) { reportFileSchedule(automation.state.value.json()) } }
    fun start() { automation.hint("manual",manual=true) }
    @Synchronized internal fun startScheduled(reason: String) {
        if (job?.isActive == true) return
        automation.begin(reason)
        // Status reporting is advisory. An offline PC must not delay the actual sync round
        // or occupy the mapping-edit gate before synchronization has even begun.
        scope.launch(Dispatchers.IO) { reportFileSchedule(automation.state.value.json()) }
        job = scope.launch(Dispatchers.IO) {
            val metadataBefore=LanFileMetrics.metadataObservations.get();val hashBefore=LanFileMetrics.hashBytes.get();var completedBytes=0L;var fileRoundCompleted=false
            _status.update { it.copy(running=true,text=context.getString(R.string.lan_verifying)) }
            try { gate.withLock {
                _status.value = LanStatus(true, context.getString(R.string.lan_verifying),fileConflicts=fileDatabase.issuePage())
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
                        if(store.mappings().none { it.optString("key")==mapping.getString("key") && it.optBoolean("enabled",true) })continue
                        activeFileRoot=mapping.getString("rootId")
                        val client=protocol()
                        try {
                            val result=fileSynchronizer.synchronize(mapping,client,{ store.saveMapping(mapping);report() },{ path,progress ->
                                val now=android.os.SystemClock.elapsedRealtime()
                                if(now-lastProgress>=250) {
                                    lastProgress=now
                                    _status.update { it.copy(running=true,text=path,done=done+progress.bytes,total=0,problems=problems.toList(),fileActions=(actions.keys+progress.actions.keys).associateWith { (actions[it] ?: 0)+(progress.actions[it] ?: 0) },fileDeferredCount=deferredCount+progress.deferredCount,fileProcessed=processed+progress.processed) }
                                }
                            },{ path,entry,deleted -> queueRefresh(mapping,path,entry,deleted) },{ drainRefresh() })
                            for((action,count) in result.actions)actions[action]=(actions[action] ?: 0)+count
                            conflicts.addAll(result.conflicts.take(100-conflicts.size));deferredCount+=result.deferredCount;processed+=result.processed;done+=result.bytes
                            for(reason in result.deferred.take((100-problems.size).coerceAtLeast(0)))problems+=reason
                            _status.update { it.copy(fileActions=actions.toMap(),fileDeferredCount=deferredCount,fileProcessed=processed) }
                        } finally { client.close();activeFileRoot=null }
                        drainRefresh()
                    }
                    ensureActive()
                    fileRoundCompleted=true
                    if(reason=="manual") {
                        val dataClient = protocol()
                        LanProtocol.requirePairedCapabilities(dataClient.call("capabilities"))
                        dataClient.use {
                            val remote = dataClient.call("state", sync.exportLan())
                            sync.acceptLan(remote)
                            try { preferences.synchronize(includeUserData=false) } catch(e: CancellationException) { throw e } catch(e: Exception) { problems += if(e is LanRemoteException) problem(e) else "Personal preference delta pending: peer offline or older protocol" }
                        }
                    }
                    _status.value = _status.value.copy(running=false,text=context.getString(if(problems.isEmpty() && _status.value.fileConflicts.isEmpty()) R.string.lan_done else R.string.lan_issues),done=done,total=0,problems=problems,timeDetails=timeDetails,precisionNs=precisionNs)
                } catch (e: CancellationException) {
                    _status.value = _status.value.copy(running=false,text=context.getString(R.string.lan_cancelled),done=done,total=total,problems=problems,timeDetails=timeDetails,precisionNs=precisionNs)
                } catch (e: Exception) {
                    // Transport exceptions can contain connection addresses, but never serialize the invitation/token.
                    _status.value = if (!currentCoroutineContext().isActive) _status.value.copy(running=false,text=context.getString(R.string.lan_cancelled),done=done,total=total,problems=problems,timeDetails=timeDetails,precisionNs=precisionNs)
                        else _status.value.copy(running=false,text=context.getString(R.string.lan_failed),done=done,total=total,problems=problems+problem(e),timeDetails=timeDetails,precisionNs=precisionNs)
                } finally { completedBytes=done;transport?.close(); transport = null; refreshConflicts(); startRecovery() }
            } }finally {
                _status.update { it.copy(running=false) }
                // Choices made after their path was visited still need a round. An incomplete
                // choice is not a successful idle state; retry with the normal bounded backoff.
                val pending=store.mappings().any { LanMappingTargets.isCurrent(it) && it.optBoolean("enabled",true) && fileDatabase.pendingIssueCount(it.getString("key"))>0 }
                if(pending && currentCoroutineContext().isActive)automation.hint("conflict_pending")
                automation.finish(currentCoroutineContext().isActive && fileRoundCompleted && _status.value.fileDeferredCount==0 && !pending,LanFileMetrics.metadataObservations.get()-metadataBefore,LanFileMetrics.hashBytes.get()-hashBefore,completedBytes)
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
        fileDatabase.queueRefresh(mapping.getString("key"),path,JSONObject().put("stamp",entry?.optString("stamp")).put("deleted",deleted).put("audio",JSONArray(audio.toList())).put("paths",JSONArray(paths.toList())))
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
                            val actual=local.observe(path,true);val missing=!actual.optBoolean("exists")
                            if(update.optBoolean("deleted") && missing)fileDatabase.bindCue(key,path,emptyList())
                            else if(!missing && actual.getString("stamp")==update.optString("stamp")) {
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
        scope.launch(Dispatchers.IO) {
            try {
                _conflictError.value=false
                // Persist first, then promptly exchange intent independently of file I/O.
                fileDatabase.queueChoice(id,choice)
                refreshConflicts()
            }catch(e: CancellationException) { throw e }
            catch(_: Exception) { _conflictError.value=true;return@launch }
            start()
            try { exchangeConflictStatus(id) }catch(e: CancellationException) { throw e }catch(_: Exception) { /* Durable choice retries on the next status tick or reconciliation. */ }
        }
    }
    suspend fun backups(): List<JSONObject> = withContext(Dispatchers.IO) { gate.withLock { store.mappings().filter { LanMappingTargets.isCurrent(it) }.flatMap { fileDatabase.backupRows(it.getString("key")) }.take(100) } }
    suspend fun maintainBackup(operation: String,purge: Boolean)=withContext(Dispatchers.IO) { gate.withLock {
        check(job?.isActive!=true) { "Cancel sync first" }
        val row=fileDatabase.receipt(operation) ?: error("Backup receipt missing")
        val mapping=store.mappings().single { it.optString("key")==row.getString("mapping") }
        val part=File(context.cacheDir,"lan-restore-${java.util.UUID.randomUUID()}.part")
        try { LanLocalFiles.fromMapping(context,mapping,fileDatabase,false).use { it.maintainBackup(row,purge,part) };if(!purge) { queueRefresh(mapping,row.getString("path"),JSONObject().put("stamp",row.getString("expectedStamp")));drainRefresh() };report() }finally { part.delete() }
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
