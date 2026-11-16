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

data class LanStatus(val running: Boolean = false, val text: String = "", val done: Long = 0, val total: Long = 0, val problems: List<String> = emptyList(), val timeDetails: List<String> = emptyList(), val precisionNs: Long = 0,val fileActions: Map<String,Int> = emptyMap(),val fileConflicts: List<JSONObject> = emptyList(),val fileDeferredCount: Int=0,val fileProcessed: Int=0,val fileReusedBytes: Long=0,val fileReusedFiles: Int=0,val lastReusedBytes: Long=0,val lastReusedFiles: Int=0,val quiet: Boolean=false)

internal class LanMappingBusyException : IllegalStateException("Cancel sync before editing mappings")

@Singleton
class LanSyncService @Inject constructor(
    @param:ApplicationContext private val context: Context,
    val store: LanSyncStore,
    private val ledgerStore: LanSyncLedger,
    private val fileDatabase: LanFileDatabase,
    internal val localFileIndex: com.tinyfish.jeekmusic.data.scanner.LocalFileIndex,
    private val tree: DocumentTree,
    private val library: LibraryRepository,
    private val scanner: LibraryScanner,
    internal val db: JeekDatabase,
    private val playing: PlayingFiles,
    internal val sync: SyncService,
    val groups: GroupSyncService,
    internal val preferences: com.tinyfish.jeekmusic.data.preference.PreferenceService,
    val networkPolicy: SyncNetworkPolicy,
    private val mediaChanges: com.tinyfish.jeekmusic.data.scanner.SyncMediaChanges,
    @param:ApplicationScope private val scope: CoroutineScope,
) {
    private val gate = Mutex()
    @Volatile private var job: Job? = null
    private class StopState { @Volatile var reason = "" }
    @Volatile private var stopState = StopState()
    @Volatile private var activeFileRoot: String? = null
    @Volatile private var activeFilePeer: String? = null
    fun selectPeer(peerId: String) { store.selectPeer(peerId); report() }
    fun forgetPeer(peerId: String) {
        check(groups.status.value.optString("peerId")!=peerId) { "This PC is the active group upstream" }
        check(job?.isActive != true) { "Cancel sync first" }
        store.forgetPeer(peerId); report(); automation.configure()
    }
    private fun mappingPeer(mapping: JSONObject) = mapping.optString("peerId").ifEmpty { store.primaryPeerId() }
    internal fun peerReady(peerId: String): Boolean {
        val peer = store.peers().singleOrNull { it.getString("peerId") == peerId } ?: return false
        val schedule = peer.optJSONObject("schedule")
        return schedule?.optBoolean("cancelled") != true && (schedule?.optLong("backoffUntil") ?: 0) <= System.currentTimeMillis()
    }
    private fun resumePeer(peerId: String) = store.updatePeer(peerId) {
        val state=it.optJSONObject("schedule") ?: JSONObject().also { value -> it.put("schedule",value) }
        state.put("cancelled",false).put("backoffUntil",0)
    }
    private fun peerOutcome(peerId: String, error: Exception? = null) {
        store.updatePeer(peerId) { peer ->
            val schedule = peer.optJSONObject("schedule") ?: JSONObject().also { peer.put("schedule", it) }
            if (error == null) schedule.put("failures", 0).put("backoffUntil", 0).put("lastSuccess", System.currentTimeMillis()).put("state", "idle").remove("lastError")
            else {
                val failures = (schedule.optInt("failures") + 1).coerceAtMost(6)
                schedule.put("failures", failures).put("backoffUntil", System.currentTimeMillis() + minOf(900_000L, 30_000L * (1L shl (failures - 1))))
                    .put("state", "backoff").put("lastError", if (error is LanRemoteException) error.code else error.javaClass.simpleName)
            }
        }
        report()
    }
    private fun readyMappings(): List<JSONObject> {
        val state=store.read()
        val target=state.optString("manualPeerId").takeIf { state.optBoolean("manualPending") }.orEmpty()
        return store.mappings().filter {
            LanMappingTargets.isCurrent(it) && it.optBoolean("enabled", true) && peerReady(mappingPeer(it)) && (target.isEmpty() || mappingPeer(it)==target)
        }
    }
    private val fileSynchronizer by lazy { LanFileSynchronizer(context,fileDatabase,playing,localFileIndex) }
    private val automation by lazy { LanAutoSync(context,scope,this) }
    val schedule get()=automation.state
    fun initializeAutomation()=automation.initialize()
    internal fun isSyncRunning()=job?.isCompleted==false
    internal suspend fun awaitSync() { job?.join() }
    internal fun cancelScheduled(reason: String="cancelled") { stopState.reason=reason;job?.cancel();transport?.cancel() }
    internal suspend fun backgroundSync()=automation.background()
    /** A cheap preflight. No directory enumeration and no file job when both sides
     * still match their checkpoints. The mapping gate also serializes manual starts. */
    internal suspend fun checkScheduled(reason: String): Boolean {
        if(isSyncRunning() || store.read().optBoolean("autoCancelled"))return false
        var needed=store.read().optBoolean("manualPending")
        gate.withLock {
            if(isSyncRunning())return false
            for(mapping in readyMappings()) {
                if (!peerReady(mappingPeer(mapping))) continue
                try {
                mediaChanges.check(mapping)
                var mappingNeeded=mapping.optString("directoryIdentity").isEmpty() || mapping.optBoolean("syncPending",true) || fileDatabase.pendingIssueCount(mapping.getString("key"))>0
                if(!mappingNeeded && localChanges(mapping))mappingNeeded=true
                if(pollFileRevision(mapping)!=mapping.optString("remoteRevision"))mappingNeeded=true
                if(mappingNeeded)needed=true
                } catch (e: CancellationException) { throw e }
                catch (e: Exception) { peerOutcome(mappingPeer(mapping), e) }
            }
        }
        currentCoroutineContext().ensureActive()
        if(store.read().optBoolean("autoCancelled") || networkPolicy.waitingForWifi)return false
        if(needed)startScheduled(reason)
        else syncBackgroundMetadata()
        return needed
    }
    /** Refresh only known paths, then compare them to the acknowledged baseline.
     * Our own completed downloads and late MediaStore events normally match already. */
    private suspend fun localChanges(mapping: JSONObject): Boolean = LanLocalFiles.fromMapping(context,mapping,fileDatabase,false).use { io ->
        val key=io.indexKey
        val pending=localFileIndex.pendingPaths(key) ?: return@use true
        if(pending.isEmpty()) { localFileIndex.confirm(key,Long.MAX_VALUE);return@use false }
        val selection=mapping.getJSONArray("selection").let { a -> List(a.length()) { a.getString(it) } }
        val refreshed=io.indexedScan(localFileIndex,selection,{}, { throw LanLocalDeferred("local_snapshot_incomplete") })
        val compared=localFileIndex.pendingPaths(key,refreshed.revision) ?: return@use true
        val different=compared.any { path ->
            val row=localFileIndex.row(key,path)
            val baseline=fileDatabase.baseline(mapping.getString("key"),path)
            val old=baseline?.optString("localVersion")?.takeUnless { baseline.isNull("localVersion") }
            LanFileMetrics.version(row)!=old || baseline?.optString("migration").orEmpty().isNotEmpty()
        }
        if(!different)localFileIndex.confirm(key,refreshed.revision)
        different
    }
    /** Runtime checkpoints must not overwrite a pause or mapping edit made by the UI. */
    private fun saveMappingRuntime(mapping: JSONObject) {
        store.update { state ->
            val rows=state.optJSONArray("mappings") ?: return@update
            repeat(rows.length()) { i ->
                val current=rows.getJSONObject(i)
                if(current.optString("key")==mapping.getString("key"))for(field in listOf("directoryIdentity","syncPending","remoteRevision"))
                    if(mapping.has(field))current.put(field,mapping.get(field))
            }
        }
    }
    internal fun pollFileRevision(mapping: JSONObject): String {
        LanProtocol(store.connectionFor(mapping), networkPolicy).use { client ->
            val opened=client.call("file_open",JSONObject().put("fileProtocol",3).put("rootId",mapping.getString("rootId")).put("mapping",mapping.getString("key")).put("selection",mapping.getJSONArray("selection")))
            if(opened.optBoolean("deferred") || opened.optBoolean("conflict"))throw java.io.IOException("scope_unavailable")
            return client.call("file_revision",JSONObject().put("fileProtocol",3).put("scope",opened.getString("scope"))).getString("revision")
        }
    }
    internal fun reportFileSchedule(value: JSONObject) {
        for(mapping in store.mappings().filter { LanMappingTargets.isCurrent(it) })runCatching {
            val connection=store.connectionFor(mapping)
            if (!peerReady(mappingPeer(mapping))) return@runCatching
            LanProtocol(connection, networkPolicy).use { client ->
                val schedule=JSONObject(value.toString()).put("paused",!mapping.optBoolean("enabled",true))
                connection.optJSONObject("schedule")?.let { peer ->
                    schedule.put("backoffUntil",maxOf(value.optLong("backoffUntil"),peer.optLong("backoffUntil")))
                        .put("cancelled",value.optBoolean("cancelled") || peer.optBoolean("cancelled"))
                        .put("lastSuccess",peer.optLong("lastSuccess",value.optLong("lastSuccess")))
                }
                schedule.put("running",value.optBoolean("running") && activeFilePeer==mappingPeer(mapping))
                val capabilities=client.call("capabilities")
                if(capabilities.optInt("peerScheduleProtocol")>=1) schedule.put("peerId",mappingPeer(mapping)).put("pairedComputerCount",store.peers().size)
                    .put("metadataPeer",mappingPeer(mapping)==store.primaryPeerId())
                if(capabilities.optInt("localFileIndex")<1)for(field in listOf("lastLocalIndexHits","lastLocalIndexMisses","lastLocalIndexRefreshedFiles","lastLibraryMetadataReusedFiles"))schedule.remove(field)
                if(capabilities.optInt("fileAudioReuse")<1)for(field in listOf("lastAudioReusedBytes","lastAudioReusedFiles"))schedule.remove(field)
                if(capabilities.optInt("fileDeltaProtocol")<1)for(field in listOf("lastMetadataRows","lastIncremental"))schedule.remove(field)
                if(!capabilities.optBoolean("syncPerformanceDiagnostics"))for(field in listOf("lastPreparationMs","lastFirstTransferMs","lastProtocolRequests","lastManifestCacheHit"))schedule.remove(field)
                if(!capabilities.optBoolean("syncNetworkPolicy")) {
                    schedule.remove("wifiOnly");schedule.remove("waitingForWifi")
                }
                val opened=client.call("file_open",JSONObject().put("fileProtocol",3).put("rootId",mapping.getString("rootId")).put("mapping",mapping.getString("key")).put("selection",mapping.getJSONArray("selection")))
                client.call("file_schedule",JSONObject().put("fileProtocol",3).put("scope",opened.getString("scope")).put("schedule",schedule))
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
            val mappings=store.mappings().filter { LanMappingTargets.isCurrent(it) && it.optBoolean("enabled",true) }.associateBy { it.getString("key") }
            val enabled=mappings.keys
            if(enabled.isEmpty())return
            val rows=if(id!=null)listOfNotNull(fileDatabase.issue(id)) else fileDatabase.issuePage(conflictOffset).also { conflictOffset=if(it.size<100)0 else conflictOffset+100 }
            val current=rows.filter { it.optString("mapping") in enabled && it.optString("scope").isNotEmpty() }
            if(current.isEmpty())return
            for ((peerId,peerRows) in current.groupBy { mappingPeer(mappings.getValue(it.getString("mapping"))) }) {
              if (!peerReady(peerId)) continue
              try { LanProtocol(store.connection(peerId), networkPolicy).use { client ->
                if(client.call("capabilities").optInt("conflictStatusProtocol")<1)return@use
                for((scopeId,group) in peerRows.groupBy { it.getString("scope") }) {
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
              } } catch(e: CancellationException) { throw e } catch(_: Exception) { /* Other peers remain eligible. */ }
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
    private fun problem(e: Exception): String = if(networkPolicy.waitingForWifi || e is SyncWifiRequiredException) context.getString(R.string.sync_wifi_waiting)
        else if(e is LanRemoteException && e.code=="unauthorized") context.getString(R.string.lan_pairing_revoked)
        else if(e is LanRemoteException && e.code=="upgrade_required") context.getString(R.string.lan_paired_upgrade)
        else if (e is java.net.SocketException || e is java.net.SocketTimeoutException || e is javax.net.ssl.SSLException || e is java.io.EOFException) context.getString(R.string.lan_network_error)
        else e.message?.lineSequence()?.firstOrNull()?.take(240) ?: e.javaClass.simpleName
    private fun fileProblem(value: String): String {
        val separator=value.indexOf(": ");if(separator<0)return value
        val reason=value.substring(separator+2).substringBefore(':')
        val resource=when(reason) {
            "upload_restart_required"->R.string.sync_upload_restart_required
            "upload_offset_mismatch"->R.string.sync_upload_offset_mismatch
            "upload_resume_offset_invalid"->R.string.sync_upload_resume_offset_invalid
            else->return value
        }
        return value.take(separator)+": "+context.getString(resource)
    }
    private fun protocol(peerId: String = store.selectedPeerId(), tracked: Boolean = false) = LanProtocol(store.connection(peerId), networkPolicy).also { if(tracked)transport = it }

    private suspend fun exchangeUserData(includeFeatures: Boolean) {
        val group=groups.refresh()
        val peerId = if(group.optBoolean("joined"))group.getString("peerId") else store.primaryPeerId()
        if (peerId.isEmpty()) return
        val client = protocol(peerId, tracked=true)
        try {
            client.use {
                LanProtocol.requirePairedCapabilities(client.call("capabilities"))
                if(group.optBoolean("joined"))groups.synchronize(client)
                else sync.acceptLan(client.call("state", sync.exportLan(includeFeatures).put("includeFeatures", includeFeatures)))
            }
        } finally { if (transport === client) transport = null }
        if(group.optBoolean("joined") && !group.optBoolean("paused"))preferences.synchronize(includeUserData=false,captureFeedback=false,fetchResults=false)
    }

    /** No scan, audio hashing, inference, feedback capture or model download; relays existing group learning events. */
    internal suspend fun syncBackgroundMetadata() = withContext(Dispatchers.IO) {
        gate.withLock {
            if ((!store.read().has("connection") && !groups.refresh().optBoolean("joined")) || store.read().optBoolean("autoCancelled")) return@withLock
            networkPolicy.requireAllowed()
            exchangeUserData(includeFeatures = false)
        }
    }

    suspend fun pair(invitation: String) = withContext(Dispatchers.IO) {
        gate.withLock {
            require(job?.isActive != true) { "Cancel sync first" }
            val uri = LanInvitation.parse(invitation)
            val id = sync.lanDeviceId()
            val connection = JSONObject().put("host", uri.host).put("port", uri.port).put("pin", uri.pin).put("deviceId", id)
            val client = LanProtocol(connection, networkPolicy); transport = client
            val response = client.call("pair", JSONObject().put("invitation", uri.token).put("name", Build.MODEL))
            client.close()
            require(response.optInt("protocol") == 4) { "Upgrade the PC for LAN protocol 4" }
            connection.put("token", response.getString("token")).put("name", response.optString("name", uri.host))
            store.addPeer(connection); report()
            automation.configure()
            _status.value = LanStatus(text = context.getString(R.string.lan_paired_identity, connection.getString("name"), uri.endpoint))
        }
    }
    suspend fun joinGroup(peer: String=store.selectedPeerId()) {
        groups.join(peer);automation.configure();syncBackgroundMetadata();report()
    }
    suspend fun synchronizeGroup()=withContext(Dispatchers.IO) { gate.withLock { networkPolicy.requireAllowed();exchangeUserData(false);report() } }
    suspend fun roots(peerId: String = store.selectedPeerId()): List<JSONObject> = withContext(Dispatchers.IO) {
      try {
        protocol(peerId).use { client ->
            val a = client.call("roots").getJSONArray("roots"); List(a.length()) { a.getJSONObject(it).put("peerId", peerId) }.also { peerOutcome(peerId) }
        }
      } catch (e: CancellationException) { throw e }
      catch (e: Exception) { peerOutcome(peerId,e);throw e }
    }
    suspend fun directories(root: String, path: String, peerId: String = store.selectedPeerId()): List<String> = withContext(Dispatchers.IO) {
        LanProtocol.relative(path, true)
        val result = mutableListOf<String>()
        protocol(peerId).use { client ->
            do {
                val page = client.call("tree", JSONObject().put("rootId", root).put("path", path).put("offset", result.size))
                val a = page.getJSONArray("directories")
                repeat(a.length()) { result += a.getString(it).also { p -> LanProtocol.relative(p) } }
                require(result.size <= 100_000) { "Directory budget exceeded" }
            } while(a.length() == page.optInt("pageSize", Int.MAX_VALUE))
        }
        result
    }
    suspend fun estimate(root: String, selected: Set<String>, peerId: String = store.selectedPeerId()): JSONObject = withContext(Dispatchers.IO) {
        require(selected.isNotEmpty()) { "Choose at least one folder" }
        protocol(peerId).use { it.call("plan", JSONObject().put("fileProtocol",3).put("rootId", root).put("selection", JSONArray(selected.toList()))) }
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
            val peerId = root.optString("peerId").ifEmpty { store.selectedPeerId() }
            store.connection(peerId)
            context.contentResolver.takePersistableUriPermission(target, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            val id = DocumentsContract.getTreeDocumentId(target)
            val path = SafPaths.fromTreeDocumentId(id) ?: error("Unsupported local storage")
            val parent = LibraryFolderEntity(treeUri = target.toString(), treeDocumentId = id, volumeName = path.volumeName, relativePath = path.relativePath,
                displayName = root.getString("name"), addedAt = System.currentTimeMillis())
            LanMappingTargets.requireSeparate(store.mappings(), rootId, path.volumeName, path.relativePath, peerId)
            localBoundary(parent)
            library.registerSyncDestination(parent)
            val old = store.mappings().firstOrNull { LanMappingTargets.isCurrent(it) && mappingPeer(it) == peerId && it.getString("rootId") == rootId && it.optString("treeUri") == target.toString() && it.getJSONArray("selection").let { a -> List(a.length()) { a.getString(it) }.toSet()==selected } }
            val mapping = JSONObject().put("layoutVersion",LanMappingTargets.VERSION).put("rootId", rootId).put("name", root.getString("name")).put("selection", JSONArray(selected.toList().sorted()))
                .put("treeUri", target.toString()).put("documentId", id).put("volume", path.volumeName).put("relative", path.relativePath)
                .put("key", old?.getString("key") ?: java.util.UUID.randomUUID().toString()).put("enabled", true).put("peerId", peerId)
            old?.optString("directoryIdentity")?.takeIf { it.isNotEmpty() }?.let { mapping.put("directoryIdentity",it) }
            store.saveMapping(mapping); report()
            resumePeer(peerId)
            automation.resume()
        } finally { gate.unlock() }
    }
    fun setEnabled(root: String, enabled: Boolean, peerId: String = store.selectedPeerId()) {
        if(!enabled && activeFileRoot==root && activeFilePeer==peerId)cancelScheduled()
        val mapping = store.mappings().single { it.getString("rootId") == root && mappingPeer(it) == peerId }; if(enabled) LanMappingTargets.requireCurrent(mapping); mapping.put("enabled", enabled); store.saveMapping(mapping); report()
        if(enabled) { resumePeer(peerId);automation.resume() } else automation.configure()
    }
    fun forget(root: String, peerId: String = store.selectedPeerId()) {
        check(job?.isActive != true) { "Cancel sync first" }
        store.update { it.put("mappings", JSONArray(store.mappings().filterNot { m -> m.getString("rootId") == root && mappingPeer(m) == peerId })) }; report()
        automation.configure()
    }
    fun cancel() = cancel(null)
    fun cancel(peerId: String?) {
        if (peerId == null) { store.update { it.remove("manualPending");it.remove("manualPeerId") };automation.cancel();cancelScheduled() }
        else {
            store.updatePeer(peerId) { peer -> val state=peer.optJSONObject("schedule") ?: JSONObject().also { peer.put("schedule",it) };state.put("cancelled",true).put("state","cancelled") }
            if (activeFilePeer == peerId) cancelScheduled()
            store.update { if(it.optString("manualPeerId")==peerId) { it.remove("manualPending");it.remove("manualPeerId") } }
            report();automation.hint("peer_cancelled")
        }
        automation.report()
    }
    fun start() = start(null)
    fun start(peerId: String?) {
        if (peerId != null) store.connection(peerId)
        for (peer in store.peers().filter { peerId == null || it.getString("peerId") == peerId }) store.updatePeer(peer.getString("peerId")) {
            val schedule=it.optJSONObject("schedule") ?: JSONObject().also { value -> it.put("schedule",value) };schedule.put("cancelled",false).put("backoffUntil",0)
        }
        store.update { it.put("manualPending",true);if(peerId==null)it.remove("manualPeerId") else it.put("manualPeerId",peerId) }
        automation.hint("manual",manual=true);report()
    }
    fun refresh() = refresh(null)
    fun refresh(peerId: String?) { scope.launch(Dispatchers.IO) { localFileIndex.requestRefresh();start(peerId) } }
    @Synchronized internal fun startScheduled(reason: String) {
        if (job?.isCompleted == false) return
        if (networkPolicy.waitingForWifi) { _status.update { it.copy(running=false,text=context.getString(R.string.sync_wifi_waiting)) };return }
        val stop=StopState();stopState=stop
        val requestedPeer=store.read().optString("manualPeerId")
        val includeUserData=(reason=="manual" || store.read().optBoolean("manualPending")) && (requestedPeer.isEmpty() || requestedPeer==store.primaryPeerId())
        fun stoppedText()=context.getString(when(stop.reason) { "background"->R.string.lan_background_waiting;"cancelled"->R.string.lan_cancelled;else->R.string.lan_interrupted })
        automation.begin(reason)
        // Status reporting is advisory. An offline PC must not delay the actual sync round
        // or occupy the mapping-edit gate before synchronization has even begun.
        automation.report()
        job = scope.launch(Dispatchers.IO) {
            val metadataBefore=LanFileMetrics.metadataObservations.get();val hashBefore=LanFileMetrics.hashBytes.get();var completedBytes=0L;var fileRoundCompleted=false;var peerFailures=false
            val roundStarted=System.nanoTime();var preparationMs=0L;var firstTransferMs=-1L;var requests=0L;var cacheHit=false;var metadataRows=0L;var incremental=true;var reusedBytes=0L;var reusedFiles=0;var indexHits=0;var indexMisses=0;var indexRefreshed=0;var libraryReused=0
            _status.update { it.copy(running=true,text=context.getString(R.string.lan_verifying),quiet=reason!="manual") }
            try { gate.withLock {
                _status.value = LanStatus(true, context.getString(R.string.lan_verifying),fileConflicts=fileDatabase.issuePage(),lastReusedBytes=_status.value.lastReusedBytes,lastReusedFiles=_status.value.lastReusedFiles,quiet=reason!="manual")
                val problems = mutableListOf<String>(); val timeDetails = mutableListOf<String>(); var precisionNs = 0L; var done = 0L; var total = 0L
                try {
                    val obsolete=store.mappings().count { !LanMappingTargets.isCurrent(it) }
                    if(obsolete>0) problems += context.getString(R.string.lan_mapping_reselect)
                    loadLedger()
                    _status.update { it.copy(text=context.getString(R.string.sync_recovering)) }
                    recoverPending()
                    drainRefresh()
                    val actions=mutableMapOf<String,Int>();val conflicts=mutableListOf<JSONObject>();var deferredCount=0;var processed=0;var lastProgress=0L
                    val failedPeers=mutableSetOf<String>()
                    for (mapping in readyMappings()) {
                        ensureActive()
                        val peerId=mappingPeer(mapping)
                        if (peerId in failedPeers || !peerReady(peerId)) continue
                        if(store.mappings().none { it.optString("key")==mapping.getString("key") && it.optBoolean("enabled",true) })continue
                        try {
                        activeFileRoot=mapping.getString("rootId")
                        activeFilePeer=peerId
                        mapping.put("syncPending",true);saveMappingRuntime(mapping)
                        val remoteRevision=pollFileRevision(mapping)
                        val client=protocol(peerId, tracked=true)
                        try {
                            val result=fileSynchronizer.synchronize(mapping,client,{ saveMappingRuntime(mapping);report() },{ path,progress ->
                                if(progress.bytes>0 && firstTransferMs<0)firstTransferMs=(System.nanoTime()-roundStarted)/1_000_000
                                val now=android.os.SystemClock.elapsedRealtime()
                                if(now-lastProgress>=250) {
                                    lastProgress=now
                                    _status.update { it.copy(running=true,text=path,done=done+progress.bytes,total=0,problems=problems.toList(),fileActions=(actions.keys+progress.actions.keys).associateWith { (actions[it] ?: 0)+(progress.actions[it] ?: 0) },fileDeferredCount=deferredCount+progress.deferredCount,fileProcessed=processed+progress.processed,fileReusedBytes=reusedBytes+progress.reusedBytes,fileReusedFiles=reusedFiles+progress.reusedFiles) }
                                }
                            },{ path,entry,deleted -> queueRefresh(mapping,path,entry,deleted) },{},phase={ stage,count,total ->
                                automation.phase(stage)
                                if(stage=="transferring")_status.update { it.copy(quiet=false) }
                                val text=when(stage) {
                                    "reading_baseline"->context.getString(R.string.sync_reading_baseline,count,total)
                                    "pc_scan"->context.getString(R.string.sync_pc_scan)
                                    "reading_manifest"->context.getString(R.string.sync_reading_manifest,count,total)
                                    "reading_delta"->context.getString(R.string.sync_reading_delta,count,total)
                                    "phone_scan"->context.getString(R.string.sync_phone_scan,count)
                                    "reconciling"->context.getString(R.string.sync_reconciling)
                                    else->context.getString(R.string.sync_recovering)
                                }
                                _status.update { it.copy(text=text) }
                            })
                            val pendingLocal=localChanges(mapping)
                            mapping.put("remoteRevision",remoteRevision).put("syncPending",result.deferredCount>0 || pendingLocal)
                            saveMappingRuntime(mapping)
                            if(result.localIndexHit)indexHits++ else indexMisses++;indexRefreshed+=result.localIndexRefreshed;libraryReused+=result.libraryMetadataReused
                            preparationMs+=result.preparationMillis;requests+=result.protocolRequests;cacheHit=result.manifestCacheHit;metadataRows+=result.metadataRows;incremental=incremental && result.incremental
                            for((action,count) in result.actions)actions[action]=(actions[action] ?: 0)+count
                            conflicts.addAll(result.conflicts.take(100-conflicts.size));deferredCount+=result.deferredCount;processed+=result.processed;done+=result.bytes;reusedBytes+=result.reusedBytes;reusedFiles+=result.reusedFiles
                            if (result.deferredCount>0 || fileDatabase.pendingIssueCount(mapping.getString("key"))>0) {
                                failedPeers.add(peerId);peerFailures=true;peerOutcome(peerId,java.io.IOException("file_pending"))
                            }
                            for(reason in result.deferred.take((100-problems.size).coerceAtLeast(0)))problems+=fileProblem(reason)
                            _status.update { it.copy(fileActions=actions.toMap(),fileDeferredCount=deferredCount,fileProcessed=processed,fileReusedBytes=reusedBytes,fileReusedFiles=reusedFiles,lastReusedBytes=if(reusedFiles>0 && deferredCount==0)reusedBytes else it.lastReusedBytes,lastReusedFiles=if(reusedFiles>0 && deferredCount==0)reusedFiles else it.lastReusedFiles) }
                        } finally { client.close();activeFileRoot=null;activeFilePeer=null }
                        drainRefresh(mapping.getString("key")) {
                            automation.phase("library_refresh")
                            _status.update { it.copy(text=context.getString(R.string.sync_library_refresh)) }
                        }
                        if (peerId !in failedPeers) peerOutcome(peerId)
                        } catch (e: CancellationException) { throw e }
                        catch (e: Exception) {
                            peerFailures=true;failedPeers.add(peerId);deferredCount++
                            peerOutcome(peerId,e)
                            problems += store.connection(peerId).optString("name",peerId.take(8))+": "+problem(e)
                        } finally { activeFileRoot=null;activeFilePeer=null }
                    }
                    ensureActive()
                    fileRoundCompleted=true
                    val waitingPeers=store.mappings().filter { LanMappingTargets.isCurrent(it) && it.optBoolean("enabled",true) }
                        .map(::mappingPeer).distinct().filter { store.connection(it).optJSONObject("schedule")?.optString("state")=="backoff" }
                    if(waitingPeers.isNotEmpty()) {
                        peerFailures=true
                        for(peer in waitingPeers.filterNot { it in failedPeers }) problems+=store.connection(peer).optString("name",peer.take(8))+": "+context.getString(R.string.sync_retry_waiting)
                    }
                    store.update { it.remove("manualPeerId");if(!includeUserData)it.remove("manualPending") }
                    if(includeUserData) {
                        exchangeUserData(includeFeatures = true)
                        try { preferences.synchronize(includeUserData=false) } catch(e: CancellationException) { throw e } catch(e: Exception) { problems += if(e is LanRemoteException) problem(e) else "Personal preference delta pending: peer offline or older protocol" }
                        store.update { it.remove("manualPending");it.remove("manualPeerId") }
                    }
                    _status.value = _status.value.copy(running=false,text=context.getString(if(problems.isEmpty() && _status.value.fileConflicts.isEmpty()) R.string.lan_done else R.string.lan_issues),done=done,total=0,problems=problems,timeDetails=timeDetails,precisionNs=precisionNs,fileDeferredCount=deferredCount)
                } catch (e: CancellationException) {
                    _status.value = _status.value.copy(running=false,text=stoppedText(),done=done,total=total,problems=problems,timeDetails=timeDetails,precisionNs=precisionNs)
                } catch (e: Exception) {
                    android.util.Log.e("LanSyncService","File sync round failed",e)
                    // Transport exceptions can contain connection addresses, but never serialize the invitation/token.
                    _status.value = if (!currentCoroutineContext().isActive) _status.value.copy(running=false,text=stoppedText(),done=done,total=total,problems=problems,timeDetails=timeDetails,precisionNs=precisionNs)
                        else _status.value.copy(running=false,text=context.getString(R.string.lan_failed),done=done,total=total,problems=problems+problem(e),timeDetails=timeDetails,precisionNs=precisionNs)
                } finally { completedBytes=done;transport?.close(); transport = null; refreshConflicts(); startRecovery() }
            } } catch(e: CancellationException) {
                // Cancellation while waiting for the reconciliation gate never enters the
                // inner handler, but still needs the same truthful interruption status.
                _status.update { it.copy(running=false,text=stoppedText()) }
                throw e
            } finally {
                _status.update { it.copy(running=false) }
                // Choices made after their path was visited still need a round. An incomplete
                // choice is not a successful idle state; retry with the normal bounded backoff.
                val pending=store.mappings().any { LanMappingTargets.isCurrent(it) && it.optBoolean("enabled",true) && fileDatabase.pendingIssueCount(it.getString("key"))>0 }
                if(pending && currentCoroutineContext().isActive)automation.hint("conflict_pending")
                automation.finish(currentCoroutineContext().isActive && fileRoundCompleted && !peerFailures && _status.value.fileDeferredCount==0 && !pending,LanFileMetrics.metadataObservations.get()-metadataBefore,LanFileMetrics.hashBytes.get()-hashBefore,completedBytes,handoff=stop.reason=="background",preparationMs=preparationMs,firstTransferMs=firstTransferMs,requests=requests,cacheHit=cacheHit,metadataRows=metadataRows,incremental=incremental,reusedBytes=reusedBytes,reusedFiles=reusedFiles,indexHits=indexHits,indexMisses=indexMisses,indexRefreshed=indexRefreshed,libraryReused=libraryReused,peerFailures=peerFailures || (stop.reason=="cancelled" && !store.read().optBoolean("autoCancelled")))
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
    private suspend fun drainRefresh(mappingKey: String? = null,onBatch: ()->Unit={}) {
        // Legacy refresh entries are inert, just like legacy file transactions.
        if(ledger.has("refresh")) { ledger.remove("refresh");saveLedger() }
        for(mapping in store.mappings().filter { LanMappingTargets.isCurrent(it) && it.optString("directoryIdentity").isNotEmpty() && (mappingKey==null || it.getString("key")==mappingKey) }) {
            val key=mapping.getString("key")
            try {
            while(true) {
                val queued=fileDatabase.refreshPaths(key);if(queued.isEmpty())break
                onBatch()
                val affected=linkedSetOf<String>()
                suspend fun flush() {
                    if(affected.isEmpty())return
                    refresh(mapping,affected.toList());affected.clear()
                }
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
                        val paths=update.getJSONArray("paths")
                        for(index in 0 until paths.length()) {
                            affected+=paths.getString(index)
                            if(affected.size==200)flush()
                        }
                    }
                }
                // Keep every durable receipt until all affected paths have been indexed.
                // A cancellation/error safely repeats the batch on recovery.
                flush()
                fileDatabase.refreshed(key,queued)
            }
            } catch(e: CancellationException) { throw e }
            catch(e: Exception) {
                if(mappingKey!=null)throw e
                peerOutcome(mappingPeer(mapping),e)
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
