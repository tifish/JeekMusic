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

data class LanStatus(val running: Boolean = false, val text: String = "", val done: Long = 0, val total: Long = 0, val problems: List<String> = emptyList(), val timeDetails: List<String> = emptyList(), val precisionNs: Long = 0)

@Singleton
class LanSyncService @Inject constructor(
    @param:ApplicationContext private val context: Context,
    val store: LanSyncStore,
    private val ledgerStore: LanSyncLedger,
    private val tree: DocumentTree,
    private val library: LibraryRepository,
    private val scanner: LibraryScanner,
    private val db: JeekDatabase,
    private val playing: PlayingFiles,
    private val sync: SyncService,
    @param:ApplicationScope private val scope: CoroutineScope,
) {
    private val gate = Mutex()
    private var job: Job? = null
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
            val uri = Uri.parse(invitation.trim())
            require(uri.scheme == "jeeklan" && !uri.host.isNullOrEmpty() && uri.port in 1..65535 && uri.pathSegments.size == 2) { "Invalid PC invitation" }
            require(store.mappings().isEmpty() || store.read().optJSONObject("connection")?.optString("pin") == uri.pathSegments[0]) { "Remove existing mappings before pairing another PC" }
            val id = sync.lanDeviceId()
            val connection = JSONObject().put("host", uri.host).put("port", uri.port).put("pin", uri.pathSegments[0]).put("deviceId", id)
            val client = LanProtocol(connection); transport = client
            val response = client.call("pair", JSONObject().put("invitation", uri.pathSegments[1]).put("name", Build.MODEL))
            client.close()
            connection.put("token", response.getString("token"))
            store.update { it.put("connection", connection) }; report()
            _status.value = LanStatus(text = context.getString(R.string.lan_paired))
        }
    }
    suspend fun roots(): List<JSONObject> = withContext(Dispatchers.IO) {
        val a = protocol().call("roots").getJSONArray("roots"); List(a.length()) { a.getJSONObject(it) }
    }
    suspend fun directories(root: String, path: String): List<String> = withContext(Dispatchers.IO) {
        LanProtocol.relative(path, true)
        val a = protocol().call("tree", JSONObject().put("rootId", root).put("path", path)).getJSONArray("directories")
        List(a.length()) { a.getString(it).also { p -> LanProtocol.relative(p) } }
    }
    suspend fun estimate(root: String, selected: Set<String>): JSONObject = withContext(Dispatchers.IO) {
        require(selected.isNotEmpty()) { "Choose at least one folder" }
        protocol().call("plan", JSONObject().put("rootId", root).put("selection", JSONArray(selected.toList())))
    }
    /** SAF selection is mandatory, even though the current library supports local ExternalStorageProvider only. */
    suspend fun saveMapping(root: JSONObject, selected: Set<String>, target: Uri, confirmedExclusive: Boolean) = withContext(Dispatchers.IO) {
        gate.withLock {
            check(job?.isActive != true) { "Cancel sync before editing mappings" }
            require(confirmedExclusive) { "Confirm this destination is not managed by Syncthing" }
            require(target.authority == "com.android.externalstorage.documents") { "Only local storage / SD card SAF folders are supported" }
            require(selected.isNotEmpty()) { "Choose at least one folder" }
            selected.forEach { LanProtocol.relative(it, true) }
            val rootId = root.getString("rootId"); require(rootId.matches(Regex("[0-9a-f]{32}")))
            context.contentResolver.takePersistableUriPermission(target, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            val id = DocumentsContract.getTreeDocumentId(target)
            val path = SafPaths.fromTreeDocumentId(id) ?: error("Unsupported local storage")
            val parent = LibraryFolderEntity(treeUri = target.toString(), treeDocumentId = id, volumeName = path.volumeName, relativePath = path.relativePath,
                displayName = root.getString("name"), addedAt = System.currentTimeMillis())
            val namespace = "JeekMusic-$rootId"
            // Never nest managed roots or put a namespace inside another managed namespace.
            for (other in store.mappings().filter { it.getString("rootId") != rootId }) {
                if (other.getString("volume") == path.volumeName) {
                    val a = (path.relativePath + namespace + "/").lowercase(); val b = other.getString("relative").lowercase()
                    require(!a.startsWith(b) && !b.startsWith(a)) { "Overlapping managed destinations" }
                }
            }
            val child = child(parent, id, namespace) ?: tree.create(parent, id, DocumentsContract.Document.MIME_TYPE_DIR, namespace) ?: error("Cannot create destination")
            require(child.isDirectory && child.name == namespace) { "Destination name collision" }
            val actual = DocumentsContract.buildDocumentUriUsingTree(target, child.documentId)
            // Reuse the parent's durable grant; addFolder takes the tree grant only when needed below.
            val folder = parent.copy(treeUri = actual.toString(), treeDocumentId = child.documentId, relativePath = path.relativePath + namespace + "/")
            library.registerSyncDestination(folder)
            val old = store.mappings().firstOrNull { it.getString("rootId") == rootId && it.optString("treeUri") == target.toString() }
            val mapping = JSONObject().put("rootId", rootId).put("name", root.getString("name")).put("selection", JSONArray(selected.toList().sorted()))
                .put("treeUri", target.toString()).put("documentId", child.documentId).put("volume", path.volumeName).put("relative", folder.relativePath)
                .put("key", old?.getString("key") ?: java.util.UUID.randomUUID().toString()).put("enabled", true)
            old?.optJSONObject("cueBindings")?.let { mapping.put("cueBindings", it) }
            store.saveMapping(mapping); report()
        }
    }
    fun setEnabled(root: String, enabled: Boolean) {
        check(job?.isActive != true) { "Cancel sync first" }
        val mapping = store.mappings().single { it.getString("rootId") == root }; mapping.put("enabled", enabled); store.saveMapping(mapping); report()
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
                    loadLedger()
                    recoverPending()
                    drainRefresh()
                    for (mapping in store.mappings().filter { it.optBoolean("enabled", true) }) {
                        ensureActive()
                        val client = protocol(); val root = mapping.getString("rootId")
                        val selection = mapping.getJSONArray("selection")
                        val plan = client.call("plan", JSONObject().put("rootId", root).put("selection", selection))
                        total += plan.getLong("bytes")
                        val entries = mutableListOf<JSONObject>()
                        var offset = 0
                        while (offset < plan.getInt("total")) {
                            ensureActive()
                            val page = client.call("manifest", JSONObject().put("rootId", root).put("plan", plan.getString("plan")).put("offset", offset)).getJSONArray("files")
                            require(page.length() > 0) { "Incomplete manifest" }
                            for (i in 0 until page.length()) entries += page.getJSONObject(i)
                            offset += page.length()
                        }
                        val portable = entries.map { LanProtocol.relative(it.getString("path")) }
                        require(portable.map { java.text.Normalizer.normalize(it, java.text.Normalizer.Form.NFC).lowercase(java.util.Locale.ROOT) }.distinct().size == portable.size) { "Case or Unicode collision" }
                        val folder = folder(mapping)
                        recover(mapping, folder)
                        // Install dependencies before CUE files. Parsing is notified only after this batch.
                        val failedPaths = mutableSetOf<String>()
                        for (entry in entries.sortedBy { it.getString("path").endsWith(".cue", true) }) {
                            ensureActive()
                            val path = entry.getString("path")
                            _status.value = LanStatus(true, path, done, total, problems.toList(), timeDetails.toList(), precisionNs)
                            try {
                                val dependencies = entry.optJSONArray("dependencies")?.let { a -> List(a.length()) { LanProtocol.relative(a.getString(it)) } }
                                require(dependencies?.all { it in portable && it !in failedPaths } ?: (!path.endsWith(".cue", true) || failedPaths.isEmpty())) { context.getString(R.string.lan_cue_held) }
                                transfer(mapping, folder, client, plan.getString("plan"), entry, { warning, precision ->
                                    if (precision > 0) { timeDetails += "$path: $warning"; precisionNs = maxOf(precisionNs, precision) } else problems += "$path: $warning"
                                }) { progress ->
                                    _status.value = LanStatus(true, path, done + progress, total, problems.toList(), timeDetails.toList(), precisionNs)
                                }
                            } catch (e: CancellationException) { throw e }
                            catch (e: Exception) {
                                if (e is LanLedgerException || ledger.has("pending")) throw e // Never continue after a journal failure.
                                failedPaths += path; problems += "$path: ${problem(e)}"
                            }
                            done += entry.getLong("size")
                        }
                        drainRefresh()
                    }
                    ensureActive()
                    val payload = sync.exportLan()
                    val remote = protocol().call("state", payload)
                    sync.acceptLan(remote)
                    _status.value = LanStatus(false, context.getString(if (problems.isEmpty()) R.string.lan_done else R.string.lan_issues), done, total, problems, timeDetails, precisionNs)
                } catch (e: CancellationException) {
                    _status.value = LanStatus(false, context.getString(R.string.lan_cancelled), done, total, problems, timeDetails, precisionNs)
                } catch (e: Exception) {
                    // Transport exceptions can contain connection addresses, but never serialize the invitation/token.
                    _status.value = if (!currentCoroutineContext().isActive) LanStatus(false, context.getString(R.string.lan_cancelled), done, total, problems, timeDetails, precisionNs)
                        else LanStatus(false, context.getString(R.string.lan_failed), done, total, problems + problem(e), timeDetails, precisionNs)
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
        val pending = ledger.optJSONObject("pending") ?: return
        val mapping = pending.optJSONObject("mapping") ?: store.mappings().singleOrNull { it.getString("key") == pending.getString("key") }
            ?: error("An unfinished file transaction requires its original destination")
        recover(mapping, folder(mapping))
    }
    private fun audioDependencies(entry: JSONObject): Set<String> = entry.optJSONArray("dependencies")?.let { a ->
        List(a.length()) { LanProtocol.relative(a.getString(it)) }.filter { it.substringAfterLast('.').lowercase() in setOf("wav", "flac") }.toSet()
    }.orEmpty()
    private fun cueAudio(mapping: JSONObject, cue: String): Set<String> {
        val bindings = mapping.optJSONObject("cueBindings") ?: return emptySet()
        return bindings.keys().asSequence().filter { key -> bindings.optJSONArray(key)?.let { a -> (0 until a.length()).any { a.getString(it) == cue } } == true }.toSet()
    }
    private fun queueRefresh(mapping: JSONObject, path: String, entry: JSONObject? = null) {
        val queue = ledger.optJSONObject("refresh") ?: JSONObject().also { ledger.put("refresh", it) }
        val key = mapping.getString("key")
        val task = queue.optJSONObject(key) ?: JSONObject().put("mapping", mapping).put("paths", JSONArray()).put("cues", JSONObject()).also { queue.put(key, it) }
        val paths = task.getJSONArray("paths")
        fun add(value: String) { if ((0 until paths.length()).none { paths.getString(it) == value }) paths.put(LanProtocol.relative(value)) }
        add(path)
        if (entry != null && path.endsWith(".cue", true)) {
            val latest = store.mappings().firstOrNull { it.getString("key") == key } ?: mapping
            cueAudio(latest, path).forEach(::add); audioDependencies(entry).forEach(::add)
            task.getJSONObject("cues").put(path, JSONObject().put("hash", entry.getString("hash")).put("audio", JSONArray(audioDependencies(entry).toList())))
        }
        saveLedger() // Durable BEFORE the file/mtime commit, not after the next network request.
    }
    private suspend fun drainRefresh() {
        check(!ledger.has("pending")) { "Resolve the unfinished file transaction before refreshing" }
        val queue = ledger.optJSONObject("refresh") ?: return
        for (key in queue.keys().asSequence().toList()) {
            val task = queue.getJSONObject(key)
            val mapping = store.mappings().firstOrNull { it.getString("key") == key } ?: task.getJSONObject("mapping")
            val folder = folder(mapping)
            val bindings = JSONObject(mapping.optJSONObject("cueBindings")?.toString() ?: "{}")
            val updates = task.getJSONObject("cues")
            for (cue in updates.keys()) {
                val update = updates.getJSONObject(cue)
                val target = child(folder, directory(folder, cue), cue.substringAfterLast('/'))
                if (target == null || hash(folder, target.documentId) != update.getString("hash")) continue // An aborted commit retains its old binding.
                val audio = update.getJSONArray("audio")
                LanBindings.replace(bindings, cue, List(audio.length()) { audio.getString(it) })

            }
            mapping.put("cueBindings", bindings)
            if (store.mappings().any { it.getString("key") == key }) store.saveMapping(mapping)
            val paths = task.getJSONArray("paths").let { a -> List(a.length()) { a.getString(it) } }
            refresh(mapping, paths)
            queue.remove(key); saveLedger() // Only acknowledge after the normal scanner completes.
        }
    }
    private fun folder(m: JSONObject) = LibraryFolderEntity(treeUri = m.getString("treeUri"), treeDocumentId = m.getString("documentId"), volumeName = m.getString("volume"),
        relativePath = m.getString("relative"), displayName = m.getString("name"), addedAt = 0)
    private fun owned(m: JSONObject): JSONObject = ledger.optJSONObject(m.getString("key")) ?: JSONObject().also { ledger.put(m.getString("key"), it) }
    private fun child(folder: LibraryFolderEntity, parent: String, name: String): DocumentTree.Child? {
        fun portable(s: String) = java.text.Normalizer.normalize(s, java.text.Normalizer.Form.NFC).lowercase(java.util.Locale.ROOT)
        val uri = DocumentsContract.buildChildDocumentsUriUsingTree(Uri.parse(folder.treeUri), parent)
        val children = context.contentResolver.query(uri, arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME, DocumentsContract.Document.COLUMN_MIME_TYPE), null, null, null)
            ?.use { c -> buildList<DocumentTree.Child> { while (c.moveToNext()) add(DocumentTree.Child(c.getString(0), c.getString(1), c.getString(2) == DocumentsContract.Document.MIME_TYPE_DIR)) } } ?: error("Destination directory unavailable")
        val candidates = children.filter { portable(it.name) == portable(name) }
        require(candidates.size <= 1 && candidates.none { it.name != name }) { "Case or Unicode destination collision" }
        return candidates.singleOrNull()
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
    private fun directory(folder: LibraryFolderEntity, path: String): String {
        localBoundary(folder, path)
        context.contentResolver.query(tree.documentUri(folder, folder.treeDocumentId), arrayOf(DocumentsContract.Document.COLUMN_MIME_TYPE), null, null, null)?.use {
            require(it.moveToFirst() && it.getString(0) == DocumentsContract.Document.MIME_TYPE_DIR) { "Destination root offline" }
        } ?: error("Destination root offline")
        var id = folder.treeDocumentId
        for (part in path.split('/').dropLast(1)) {
            val child = child(folder, id, part) ?: tree.create(folder, id, DocumentsContract.Document.MIME_TYPE_DIR, part) ?: error("Cannot create directory")
            require(child.isDirectory && child.name == part) { "Directory collision" }; id = child.documentId
        }
        return id
    }
    private fun hash(folder: LibraryFolderEntity, id: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
        context.contentResolver.openInputStream(tree.documentUri(folder, id))!!.use { input ->
            val buffer = ByteArray(128 * 1024)
            while (true) { val n = input.read(buffer); if (n < 0) break; digest.update(buffer, 0, n) }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }
    private fun modified(folder: LibraryFolderEntity, id: String): Long = context.contentResolver.openFileDescriptor(tree.documentUri(folder, id), "r")!!.use {
        Os.fstat(it.fileDescriptor).st_mtim.let { time -> time.tv_sec * 1_000_000_000L + time.tv_nsec }
    }
    private fun timestamp(folder: LibraryFolderEntity, id: String, requested: Long): Long {
        require(android.os.Environment.isExternalStorageManager()) { context.getString(R.string.lan_mtime_permission) }
        context.contentResolver.openFileDescriptor(tree.documentUri(folder, id), "rw")!!.use { pfd ->
            val path = SafPaths.fromTreeDocumentId(id) ?: error("Unsupported timestamp provider")
            val volume = context.getSystemService(StorageManager::class.java).storageVolumes.singleOrNull { it.mediaStoreVolumeName == path.volumeName }?.directory ?: error("Storage offline")
            val file = File(volume, path.relativePath.trimEnd('/'))
            require(file.canonicalPath == file.absolutePath) { "Linked destination rejected" }
            val descriptor = Os.fstat(pfd.fileDescriptor); val local = Os.stat(file.path)
            require(descriptor.st_dev == local.st_dev && descriptor.st_ino == local.st_ino) { "SAF descriptor/path mismatch" }
            Files.setLastModifiedTime(file.toPath(), FileTime.from(requested, TimeUnit.NANOSECONDS))
        }
        return modified(folder, id)
    }
    private fun checkBusy(folder: LibraryFolderEntity, path: String) {
        require(!playing.isBusy("${folder.volumeName}:${folder.relativePath}$path")) { context.getString(R.string.lan_busy_file) }
    }
    private fun timePrecision(requested: Long, actual: Long): Long = when (kotlin.math.abs(actual - requested)) {
        in 0..999 -> 1_000; in 1000..999_999 -> 1_000_000; in 1_000_000..999_999_999 -> 1_000_000_000; else -> 2_000_000_000
    }
    private fun applyTime(folder: LibraryFolderEntity, id: String, requested: Long): Triple<Long, String?, Boolean> {
        val actual = try { timestamp(folder, id, requested) } catch (e: Exception) { return Triple(modified(folder, id), e.message?.lineSequence()?.firstOrNull()?.take(240) ?: "mtime refused", false) }
        return Triple(actual, if (actual == requested) null else context.getString(R.string.lan_time_precision, requested, actual), actual != requested)
    }
    private suspend fun transfer(m: JSONObject, folder: LibraryFolderEntity, client: LanProtocol, plan: String, entry: JSONObject, warn: (String, Long) -> Unit, progress: (Long) -> Unit): Boolean {
        val path = LanProtocol.relative(entry.getString("path")); val expected = entry.getString("hash")
        require(expected.matches(Regex("[0-9a-f]{64}")) && entry.getLong("size") >= 0 && entry.getLong("modifiedNs") > 0)
        val parent = directory(folder, path); val name = path.substringAfterLast('/')
        val records = owned(m); val old = records.optJSONObject(path)
        val current = child(folder, parent, name)
        checkBusy(folder, path)
        if (current != null) {
            require(!current.isDirectory && old != null && hash(folder, current.documentId) == old.getString("hash") && modified(folder, current.documentId) == old.getLong("actualNs")) { context.getString(R.string.lan_conflict) }
            if (old.getString("hash") == expected) {
                if (path.endsWith(".cue", true)) {
                    val latestMapping = store.mappings().firstOrNull { it.getString("key") == m.getString("key") } ?: m
                    if (cueAudio(latestMapping, path) != audioDependencies(entry)) queueRefresh(m, path, entry)
                }
                if (old.getLong("remoteNs") == entry.getLong("modifiedNs") && (old.getLong("actualNs") == entry.getLong("modifiedNs") || old.optBoolean("timeRounded"))) {
                    old.optString("timeWarning").takeIf { it.isNotEmpty() }?.let {
                        if (old.optBoolean("timeRounded")) warn(context.getString(R.string.lan_time_precision, old.getLong("remoteNs"), old.getLong("actualNs")), timePrecision(old.getLong("remoteNs"), old.getLong("actualNs"))) else warn(it, 0)
                    }; return false
                }
                queueRefresh(m, path, entry)
                val (actual, warning, rounded) = applyTime(folder, current.documentId, entry.getLong("modifiedNs"))
                old.put("actualNs", actual).put("remoteNs", entry.getLong("modifiedNs")).put("timeWarning", warning).put("timeRounded", rounded); saveLedger()
                warning?.let { warn(it, if (old.optBoolean("timeRounded")) timePrecision(entry.getLong("modifiedNs"), actual) else 0) }
                return true
            }
        }
        val key = LanProtocol.sha((m.getString("key") + "/" + path + "/" + expected).toByteArray())
        val partial = File(partials, "$key.part")
        if (partial.length() > entry.getLong("size")) require(partial.delete())
        require(partials.usableSpace > entry.getLong("size") - partial.length() + 4 * 1024 * 1024) { "Insufficient staging space" }
        FileOutputStream(partial, true).use { out ->
            var offset = partial.length()
            while (offset < entry.getLong("size")) {
                currentCoroutineContext().ensureActive()
                val result = client.call("read", JSONObject().put("rootId", m.getString("rootId")).put("plan", plan).put("path", path).put("offset", offset))
                val bytes = Base64.decode(result.getString("data"), Base64.NO_WRAP)
                require(bytes.isNotEmpty() && bytes.size <= 256 * 1024 && offset + bytes.size <= entry.getLong("size")) { "Invalid file range" }
                out.write(bytes); offset += bytes.size; progress(offset)
            }
            out.fd.sync()
        }
        if (com.tinyfish.jeekmusic.data.tags.SafeTagWriter.sha256(partial) != expected) {
            partial.delete(); error("Content hash mismatch; partial discarded, retry with a fresh plan")
        }
        currentCoroutineContext().ensureActive(); checkBusy(folder, path)
        val suffix = java.util.UUID.randomUUID().toString(); val stageName = ".jeek-lan-stage-$suffix"; val backupName = ".jeek-lan-backup-$suffix"
        // Only a task-owned staging name is touched; a pre-existing stage is checked by its final hash.
        val stage = child(folder, parent, stageName) ?: tree.create(folder, parent, "application/octet-stream", stageName) ?: error("Cannot create temporary file")
        require(stage.name == stageName)
        var journaled = false
        try {
        android.os.ParcelFileDescriptor.AutoCloseOutputStream(context.contentResolver.openFileDescriptor(tree.documentUri(folder, stage.documentId), "w")!!).use { output ->
            partial.inputStream().use { input ->
                val buffer = ByteArray(128 * 1024)
                while (true) { currentCoroutineContext().ensureActive(); val n = input.read(buffer); if (n < 0) break; output.write(buffer, 0, n) }
            }; output.fd.sync()
        }
        require(hash(folder, stage.documentId) == expected) { "Destination hash mismatch" }
        val actual = applyTime(folder, stage.documentId, entry.getLong("modifiedNs")).first
        currentCoroutineContext().ensureActive(); checkBusy(folder, path)
        val latest = child(folder, parent, name)
        if (current != null) require(latest != null && hash(folder, latest.documentId) == old!!.getString("hash") && modified(folder, latest.documentId) == old.getLong("actualNs")) { "Local conflict before commit" }
        else require(latest == null) { "Local file appeared before commit" }
        val record = JSONObject().put("hash", expected).put("actualNs", actual).put("remoteNs", entry.getLong("modifiedNs"))
        val pending = JSONObject().put("key", m.getString("key")).put("path", path).put("parent", parent).put("stage", stageName).put("backup", backupName)
            .put("record", record).put("oldHash", old?.getString("hash"))
        check(!ledger.has("pending")) { "An unfinished file transaction must be recovered first" }
        pending.put("mapping", m).put("old", old?.let { JSONObject(it.toString()) })
        queueRefresh(m, path, entry)
        ledger.put("pending", pending); saveLedger()
        journaled = true
        if (current != null) require(tree.rename(folder, current.documentId, backupName)) { "Cannot preserve previous file" }
        require(tree.rename(folder, stage.documentId, name)) { "Commit rename failed; previous file retained for recovery" }
        val committed = child(folder, parent, name) ?: error("Committed file unavailable")
        require(hash(folder, committed.documentId) == expected) { "Committed content changed; previous file retained" }
        val (committedTime, committedWarning, committedRounded) = applyTime(folder, committed.documentId, entry.getLong("modifiedNs"))
        record.put("actualNs", committedTime).put("timeWarning", committedWarning).put("timeRounded", committedRounded)
        records.put(path, record); saveLedger()
        child(folder, parent, backupName)?.let { require(tree.delete(folder, it.documentId)) { "Cannot remove transaction backup" } }
        ledger.remove("pending"); saveLedger(); partial.delete()
        committedWarning?.let { warn(it, if (record.optBoolean("timeRounded")) timePrecision(entry.getLong("modifiedNs"), committedTime) else 0) }
        return true
        } finally { if (!journaled) tree.delete(folder, stage.documentId) }
    }
    private fun recover(m: JSONObject, folder: LibraryFolderEntity) {
        val p = ledger.optJSONObject("pending") ?: return
        require(p.getString("key") == m.getString("key")) { "Another destination has an unfinished transaction; sync it first" }
        val parent = p.getString("parent"); val name = p.getString("path").substringAfterLast('/')
        val target = child(folder, parent, name); val backup = child(folder, parent, p.getString("backup"))
        val old = p.optJSONObject("old") ?: owned(m).optJSONObject(p.getString("path"))
        fun isOld(file: DocumentTree.Child) = old != null && hash(folder, file.documentId) == p.getString("oldHash") && modified(folder, file.documentId) == old.getLong("actualNs")
        if (target != null && hash(folder, target.documentId) == p.getJSONObject("record").getString("hash")) {
            require(modified(folder, target.documentId) == p.getJSONObject("record").getLong("actualNs")) { "Transaction timestamp conflict; preserved all copies" }
            owned(m).put(p.getString("path"), p.getJSONObject("record")); saveLedger()
            if (backup != null) { require(isOld(backup)) { "Backup conflict; preserved all copies" }; require(tree.delete(folder, backup.documentId)) }
        } else if (target == null && backup != null) {
            require(isOld(backup)) { "Backup conflict; preserved all copies" }; require(tree.rename(folder, backup.documentId, name))
        } else {
            require(backup == null && (target == null && old == null || target != null && isOld(target))) { "Transaction conflict; preserved all copies" }
        }
        child(folder, parent, p.getString("stage"))?.let { stage ->
            require(hash(folder, stage.documentId) == p.getJSONObject("record").getString("hash")) { "Staging conflict; preserved all copies" }
            require(tree.delete(folder, stage.documentId))
        }
        ledger.remove("pending"); saveLedger()
    }
    suspend fun clean(root: String) = withContext(Dispatchers.IO) {
        gate.withLock {
            check(job?.isActive != true) { "Cancel sync first" }
            loadLedger()
            val m = store.mappings().single { it.getString("rootId") == root }; val folder = folder(m); recover(m, folder)
            val records = owned(m); val issues = mutableListOf<String>()
            for (path in records.keys().asSequence().toList()) try {
                LanProtocol.relative(path); checkBusy(folder, path)
                val id = directory(folder, path); val target = child(folder, id, path.substringAfterLast('/'))
                val r = records.getJSONObject(path)
                if (target != null) {
                    require(hash(folder, target.documentId) == r.getString("hash") && modified(folder, target.documentId) == r.getLong("actualNs")) { "Local conflict; kept" }
                    require(tree.delete(folder, target.documentId)) { "Delete refused" }
                }
                records.remove(path); saveLedger()
            } catch (e: Exception) { if (e is LanLedgerException) throw e; issues += "$path: ${e.message}" }
            m.put("enabled", false); store.saveMapping(m); report()
            _status.value = LanStatus(text = context.getString(R.string.lan_cleaned), problems = issues)
        }
    }
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
