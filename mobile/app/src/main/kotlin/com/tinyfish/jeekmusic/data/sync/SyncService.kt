package com.tinyfish.jeekmusic.data.sync

import com.tinyfish.jeekmusic.analysis.complete
import com.tinyfish.jeekmusic.analysis.AnalysisEvidence

import android.content.Context
import android.os.Build
import android.util.AtomicFile
import android.util.Log
import androidx.room.withTransaction
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.BuildConfig
import com.tinyfish.jeekmusic.analysis.TagRepository
import com.tinyfish.jeekmusic.data.backup.BackupRefs
import com.tinyfish.jeekmusic.data.db.*
import com.tinyfish.jeekmusic.data.library.*
import com.tinyfish.jeekmusic.data.scanner.ArtistSplitConfig
import com.tinyfish.jeekmusic.data.scanner.FileNameFormat
import com.tinyfish.jeekmusic.data.scanner.LibraryScanner
import com.tinyfish.jeekmusic.data.settings.ScanConfig
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.di.ApplicationScope
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.nio.charset.Charset
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

/** 同步时间戳、设备标识和历史与用户数据在同一个 Room 事务中提交。 */
@Singleton
class SyncService @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val db: JeekDatabase,
    private val settings: SettingsRepository,
    private val scanner: LibraryScanner,
    private val tree: DocumentTree,
    private val tags: TagRepository,
    private val lan: LanSyncStore,
    @param:ApplicationScope private val scope: CoroutineScope,
) {
    private val mutex = Mutex()
    internal val groupReplica = GroupReplica(GroupSqlRows(db)) { System.currentTimeMillis() }
    private var groupLibrarySignatures: List<String>? = null
    @Volatile var groupActive = false
        private set
    private val journal = AtomicFile(File(context.filesDir, "sync-state.json"))
    private var metadata = JSONObject()
    private var loaded = false
    private var previous = metadata.optJSONObject("state")?.let(SyncFormat::stateFromJson) ?: SyncState()
    private var ids = metadata.optJSONObject("playlist_ids") ?: JSONObject()
    private var refs = metadata.optJSONObject("references") ?: JSONObject()
    private var history = metadata.optJSONArray("history") ?: JSONArray()
    private var device = SyncDevice(metadata.optString("id").ifEmpty { UUID.randomUUID().toString() },
        metadata.optString("name").ifEmpty { Build.MODEL }, "android", BuildConfig.VERSION_NAME)
    private var started = false
    private val lastWritten = mutableMapOf<Long, String>()
    private val lastFeatures = mutableMapOf<Long, String>()
    private var restoreDismissed = false
    private val _devices = MutableStateFlow<List<Pair<String, Long>>>(emptyList())
    val devices = _devices.asStateFlow()
    private val _status = MutableStateFlow("")
    val status: StateFlow<String> = _status.asStateFlow()
    private val _offer = MutableStateFlow(false)
    val offer: StateFlow<Boolean> = _offer.asStateFlow()
    val deviceName get() = device.name

    @OptIn(FlowPreview::class)
    fun start() {
        if (started) return
        started = true
        scope.launch(Dispatchers.IO) {
            merge(
                db.invalidationTracker.createFlow("playlist", "playlist_item", "pinned_tab", "tag_override", "lyrics_offset", "track", "track_language").map { Unit },
                settings.settings.map { it.scan }.distinctUntilChanged().map { Unit },
            ).debounce(2_000).collect { syncNow() }
        }
        scope.launch(Dispatchers.IO) {
            db.invalidationTracker.createFlow("track_features").debounce(30_000).collect { syncNow() }
        }
    }

    fun onForeground() { if (started) scope.launch(Dispatchers.IO) { syncNow() } }
    fun acceptRestore() {
        scope.launch(Dispatchers.IO) {
            mutex.withLock { ensureLoaded(); metadata.put("initialized", true); restoreDismissed = false; saveJournal(); _offer.value = false }
            syncNow()
        }
    }
    fun dismissRestore() { restoreDismissed = true; _offer.value = false }

    private suspend fun ensureLoaded() {
        if (loaded) return
        val stored = db.syncJournalDao().read()
        if(!adapterSettings.contains("legacy_directory")) adapterSettings.edit().putBoolean("legacy_directory",stored!=null || journal.baseFile.exists()).apply()
        metadata = stored?.let(::JSONObject) ?: runCatching { JSONObject(journal.readFully().toString(Charsets.UTF_8)) }.getOrDefault(JSONObject())
        previous = metadata.optJSONObject("state")?.let(SyncFormat::stateFromJson) ?: SyncState()
        ids = metadata.optJSONObject("playlist_ids") ?: JSONObject()
        refs = metadata.optJSONObject("references") ?: JSONObject()
        history = metadata.optJSONArray("history") ?: JSONArray()
        device = device.copy(id = metadata.optString("id").ifEmpty { device.id }, name = metadata.optString("name").ifEmpty { Build.MODEL })
        loaded = true
        groupActive = groupReplica.active
        if (stored == null) saveJournal()
    }

    private suspend fun saveJournal() {
        metadata.put("id", device.id).put("name", device.name).put("state", SyncFormat.stateToJson(previous))
            .put("playlist_ids", ids).put("references", refs).put("history", history)
        db.syncJournalDao().write(SyncJournalEntity(content = metadata.toString()))
    }

    private suspend fun applyPendingRules() {
        if (!metadata.optBoolean("pending_rules")) return
        previous.rules?.value?.let { v ->
            val config = ScanConfig(ArtistSplitConfig(v.artistSeparators.toSet(), v.artistExceptions), v.inferFromFileName,
                runCatching { FileNameFormat.valueOf(v.fileNameFormat) }.getOrDefault(FileNameFormat.ARTIST_TITLE),
                v.legacyCharset?.let { runCatching { Charset.forName(it) }.getOrNull() })
            if (settings.current().scan != config) { settings.setScanConfig(config); scanner.rebuildArtists() }
        }
        metadata.remove("pending_rules")
        saveJournal()
    }

    private fun reference(key: String, roots: List<BackupRefs.Root>): String {
        if (key.startsWith("foreign:")) return key.removePrefix("foreign:")
        val found = lan.reference(key) ?: BackupRefs.refOf(key, roots)
        if (found != null) refs.put(key, found)
        return found ?: refs.optString(key, key)
    }

    private suspend fun capture(folders: List<LibraryFolderEntity>, canonical: (String) -> String): SyncState {
        val now = System.currentTimeMillis()
        val roots = folders.map { BackupRefs.Root(it.volumeName, it.relativePath) }
        val currentLists = db.playlistDao().all().associate { p ->
            val key = p.id.toString()
            val uid = ids.optString(key).ifEmpty {
                (if (!metadata.optBoolean("initialized")) SyncFormat.legacyUid(p.name) else UUID.randomUUID().toString()).also { ids.put(key, it) }
            }
            uid to SyncPlaylist(uid, p.name, p.updatedAt, db.playlistDao().items(p.id).map { canonical(reference(it.trackKey, roots)) })
        }
        val deleted = previous.deletedPlaylists.toMutableMap()
        for (uid in previous.playlists.keys - currentLists.keys) deleted[uid] = now
        val scan = settings.current().scan
        val rules = SyncRules(scan.split.separators.sorted(), scan.split.exceptions, scan.inferFromFileName, scan.fileNameFormat.name, scan.legacyCharset?.name())
        val nav = SyncFormat.normalizeNavigation(db.tabDao().all().map { NavRef(it.type, it.ref.takeIf(String::isNotEmpty)) })
        val overrides = previous.tagOverrides.mapValues { it.value.toMutableMap() }.toMutableMap()
        val groupNull=groupReplica.active
        val actual = db.tagDao().overrides().sortedBy { !it.trackKey.startsWith("foreign:") }.associate { (canonical(reference(it.trackKey, roots)) to it.dimension) to when {
            it.value != null -> it.value
            groupNull -> null
            it.dimension == "MOOD" -> "NON_SPORT"
            it.dimension == "GENRE" -> "NON_CLASSICAL"
            else -> null
        } }
        val keys = actual.keys + overrides.flatMap { (ref, dims) -> dims.keys.map { ref to it } }
        for ((ref, dim) in keys) {
            val value = actual[ref to dim]
            val old = previous.tagOverrides[ref]?.get(dim)
            overrides.getOrPut(ref) { mutableMapOf() }[dim] = if (old != null && old.value == value) old else Stamped(value, maxOf(now, (old?.updatedAt ?: 0) + 1))
        }
        val offsets = previous.lyricsOffsets.toMutableMap()
        val actualOffsets = db.lyricsDao().allOffsets().sortedBy { !it.trackKey.startsWith("foreign:") }.associate { canonical(reference(it.trackKey, roots)) to it.offsetMs }
        for (ref in actualOffsets.keys + offsets.keys) {
            val value = actualOffsets[ref] ?: 0L
            val old = previous.lyricsOffsets[ref]
            offsets[ref] = if (old != null && old.value == value) old else Stamped(value, now)
        }
        return SyncState(
            if (previous.rules?.value == rules) previous.rules else Stamped(rules, if (previous.rules == null) 0 else now),
            if (previous.nav?.value == nav) previous.nav else Stamped(nav, if (previous.nav == null) 0 else now),
            currentLists, deleted, overrides, offsets,
        )
    }

    suspend fun syncNow(forceWrite: Boolean = false, directoryRequested: Boolean = true, incoming: SyncFile? = null, incomingFeatures: JSONObject? = null): Int = withContext(Dispatchers.IO) {
        mutex.withLock {
            try {
                ensureLoaded()
                if(groupActive) {
                    if(incoming!=null)throw GroupSyncException("upgrade_required")
                    groupFrameUnlocked { true }
                    return@withLock 0
                }
                val directory = directoryRequested && directoryEnabled
                applyPendingRules()
                if (scanner.busy.value) { if(incoming!=null) error("Library scan busy; retry user-data exchange"); return@withLock 0 }
                val folders = db.folderDao().all()
                if (folders.isEmpty() && incoming==null && directory) return@withLock 0
                val roots = folders.map { BackupRefs.Root(it.volumeName, it.relativePath) }
                val tracks = db.trackDao().all()
                val refToKey = tracks.map { reference(it.trackKey, roots).lowercase() to it.trackKey }.groupBy { it.first }
                    .filterValues { it.size == 1 }.mapValues { it.value.single().second }
                val matcher = BackupRefs.Matcher(refToKey)
                fun match(ref: String) = matcher.match(ref.replace('\\', '/').lowercase())
                fun canonical(ref: String) = match(ref)?.let { reference(it, roots) } ?: ref
                val remotes = mutableListOf<SyncFile>()
                incoming?.let { remotes += it.copy(state = SyncMerge.normalizeReferences(it.state, ::canonical)) }
                if(directory) File(context.filesDir, "lan-inbox.json").takeIf { it.exists() }?.let { file ->
                    SyncFormat.parse(file.readText())?.let { f -> remotes += f.copy(state = SyncMerge.normalizeReferences(f.state, ::canonical)) }
                }
                for (folder in if(directory) folders else emptyList()) {
                    val files = tree.list(folder, DocumentTree.appPath(folder, "sync")).filter { it.name.endsWith(".json") && !it.name.endsWith(".features.json") }
                    for (file in files) {
                        val text = tree.readLimited(folder, file.documentId, 32_000_000)?.toString(Charsets.UTF_8) ?: continue
                        SyncFormat.parse(text)?.let { f -> remotes += f.copy(state = SyncMerge.normalizeReferences(f.state, ::canonical)) }
                    }
                    if (files.isEmpty()) {
                        val appDir = tree.findChild(folder, folder.treeDocumentId, DocumentTree.APP_DIR)
                        val old = appDir?.let { tree.findChild(folder, it.documentId, "backup.json") }
                        old?.let { tree.readLimited(folder, it.documentId, 32_000_000)?.toString(Charsets.UTF_8)?.let(SyncFormat::parseLegacy)?.let(remotes::add) }
                    }
                }
                _devices.value = SyncMerge.latestPerDevice(remotes, device.id).map { it.device.name to it.writtenAt }
                if (!metadata.optBoolean("initialized") && previous.playlists.isEmpty() && db.playlistDao().all().isEmpty() && remotes.any { it.device.id != device.id }) {
                    if (!restoreDismissed) _offer.value = true
                    return@withLock 0
                }
                // Capture and apply share Room's transaction so UI edits cannot be lost while reading remote files.
                val result = db.withTransaction {
                    previous = SyncMerge.normalizeReferences(previous, ::canonical)
                    val local = capture(folders, ::canonical)
                    val merged = SyncMerge.merge(local, remotes, device.id, System.currentTimeMillis())
                    apply(merged, ::match)
                    if (merged.state.rules?.value != local.rules?.value) metadata.put("pending_rules", true)
                    previous = merged.state
                    metadata.put("initialized", true)
                    saveJournal()
                    merged
                }
                applyPendingRules()
                val stateText = SyncFormat.stateToJson(previous).toString()
                val bytes = SyncFormat.toJson(SyncFile(device, System.currentTimeMillis(), previous)).toString(2).toByteArray()
                var written = 0
                for (folder in if(directory) folders else emptyList()) {
                    val exists = tree.list(folder, DocumentTree.appPath(folder, "sync")).any { it.name == SyncFormat.fileName(device.id) }
                    if (forceWrite || stateText != lastWritten[folder.id] || !exists) if (write(folder, SyncFormat.fileName(device.id), bytes)) { lastWritten[folder.id] = stateText; written++ }
                }
                shareFeatures(if(directory) folders else emptyList(), tracks, roots, ::match, incomingFeatures)
                if (result.changes.isNotEmpty()) tags.recomputeAll()
                _status.value = if (result.changes.isEmpty()) context.getString(R.string.sync_done) else result.changes.groupBy { it.deviceName }.entries.joinToString("; ") { context.getString(R.string.sync_changes, it.key, it.value.size) }
                written
            } catch (e: CancellationException) { loaded = false; throw e }
            catch (e: Exception) { loaded = false; Log.w("SyncService", "同步失败", e); _status.value = context.getString(R.string.sync_failed, e.message.orEmpty()); if(incoming!=null) throw e; 0 }
        }
    }

    private suspend fun apply(result: MergeResult, match: (String) -> String?) {
        val now = System.currentTimeMillis()
        for (old in result.replaced) history.put(JSONObject().put("at", now).put("from", old.deviceName)
            .put("playlist", SyncFormat.stateToJson(SyncState(playlists = mapOf(old.old.uid to old.old))).getJSONArray("playlists").getJSONObject(0)))
        for (i in history.length() - 1 downTo 0) if (now - history.getJSONObject(i).getLong("at") > 30L * 86400000) history.remove(i)
        val existing = db.playlistDao().all().associateBy { ids.optString(it.id.toString()) }
        for ((uid, p) in result.state.playlists) {
            val mine = existing[uid]
            val id = mine?.id ?: db.playlistDao().insert(PlaylistEntity(name = p.name, createdAt = now, updatedAt = p.updatedAt)).also { ids.put(it.toString(), uid) }
            val keys = p.items.map { match(it) ?: "foreign:$it" }
            if (mine == null || mine.updatedAt != p.updatedAt || db.playlistDao().items(id).map { it.trackKey } != keys) {
                db.playlistDao().rename(id, p.name, p.updatedAt)
                db.playlistDao().clearItems(id)
                db.playlistDao().insertItems(keys.mapIndexed { i, key -> PlaylistItemEntity(playlistId = id, position = i, trackKey = key) })
            }
        }
        for ((uid, at) in result.state.deletedPlaylists) existing[uid]?.takeIf { it.updatedAt <= at }?.let { db.playlistDao().delete(it.id) }
        result.state.nav?.let { n ->
            val current = db.tabDao().all()
            val normalized = SyncFormat.normalizeNavigation(n.value)
            if (current.map { NavRef(it.type, it.ref.takeIf(String::isNotEmpty)) } != normalized) {
                current.map { it.type }.distinct().forEach { db.tabDao().deleteType(it) }
                db.tabDao().insert(normalized.mapIndexed { i, v -> PinnedTabEntity(type = v.type, ref = v.ref.orEmpty(), position = i) })
            }
        }
        val existingOverrides = db.tagDao().overrides().associateBy { it.trackKey to it.dimension }
        for ((ref, dims) in result.state.tagOverrides) for ((dim, stamp) in dims) {
            val key = match(ref) ?: "foreign:$ref"
            val old = existingOverrides[key to dim]
            if (stamp.value == null) { if (old != null) db.tagDao().clearOverride(key, dim) }
            else if (old?.value != stamp.value) db.tagDao().setOverride(TagOverrideEntity(key, dim, stamp.value))
            if (key != "foreign:$ref" && existingOverrides.containsKey("foreign:$ref" to dim)) db.tagDao().clearOverride("foreign:$ref", dim)
        }
        for (o in existingOverrides.values) if (o.trackKey.startsWith("foreign:") && match(o.trackKey.removePrefix("foreign:")) != null)
            db.tagDao().clearOverride(o.trackKey, o.dimension)
        val existingOffsets = db.lyricsDao().allOffsets().associateBy { it.trackKey }
        for ((ref, stamp) in result.state.lyricsOffsets) {
            val key = match(ref) ?: "foreign:$ref"
            if (existingOffsets[key]?.offsetMs != stamp.value) db.lyricsDao().setOffset(LyricsOffsetEntity(key, stamp.value))
            if (key != "foreign:$ref" && existingOffsets.containsKey("foreign:$ref")) db.lyricsDao().clearOffset("foreign:$ref")
        }
        for (o in existingOffsets.values) if (o.trackKey.startsWith("foreign:") && match(o.trackKey.removePrefix("foreign:")) != null)
            db.lyricsDao().clearOffset(o.trackKey)
    }

    private fun write(folder: LibraryFolderEntity, name: String, bytes: ByteArray): Boolean {
        val dir = tree.appDirectory(folder, "sync") ?: return false
        val tmpName = "$name.tmp"
        tree.findChild(folder, dir, tmpName)?.let { tree.delete(folder, it.documentId) }
        val tmp = tree.create(folder, dir, "application/octet-stream", tmpName) ?: return false
        if (tmp.name != tmpName || !tree.write(folder, tmp.documentId, bytes)) return false
        val current = tree.findChild(folder, dir, name)
        if (current != null) {
            tree.findChild(folder, dir, "$name.prev")?.let { tree.delete(folder, it.documentId) }
            if (!tree.rename(folder, current.documentId, "$name.prev")) { tree.delete(folder, tmp.documentId); return false }
        }
        if (tree.rename(folder, tmp.documentId, name)) return true
        // Restore the original name if a provider fails the second rename.
        tree.findChild(folder, dir, "$name.prev")?.let { tree.rename(folder, it.documentId, name) }
        return false
    }

    private suspend fun shareFeatures(folders: List<LibraryFolderEntity>, tracks: List<TrackEntity>, roots: List<BackupRefs.Root>, match: (String) -> String?, incoming: JSONObject? = null, export: Boolean = false, notify: Boolean = true): JSONObject {
        if(folders.isEmpty() && incoming==null && !export) return JSONObject()
        val byKey = tracks.associateBy { it.trackKey }
        var imported = false
        val featureInputs = folders.flatMap { folder -> tree.list(folder, DocumentTree.appPath(folder, "sync")).filter { it.name.endsWith(".features.json") && it.name != SyncFormat.featuresFileName(device.id) }
            .mapNotNull { file -> runCatching { JSONObject(tree.readLimited(folder, file.documentId, 32_000_000)?.toString(Charsets.UTF_8) ?: "") }.getOrNull() } }.toMutableList()
        incoming?.let(featureInputs::add)
        if(incoming==null) File(context.filesDir, "lan-inbox.features.json").takeIf { it.exists() }?.let { file -> runCatching { JSONObject(file.readText()) }.getOrNull()?.let(featureInputs::add) }
        for (json in featureInputs) {
            if (json.optInt("format") != 2 || json.optInt("analyzer_version") != TagRepository.ANALYZER_VERSION) continue
            val items = json.optJSONObject("items") ?: continue
            for (ref in items.keys()) {
                val t = match(ref)?.let(byKey::get) ?: continue
                val item = items.optJSONObject(ref) ?: continue
                if (item.optLong("size") != t.size || kotlin.math.abs(item.optLong("duration_ms") - t.durationMs) > 1000) continue
                val old = db.tagDao().features(t.trackKey)
                if (old != null && old.complete() && old.analyzerVersion == TagRepository.ANALYZER_VERSION && old.signature == "${t.dateModified}:${t.size}") continue
                val f = item.optJSONObject("features") ?: continue
                val evidence = f.optString("analysis_evidence").takeIf { AnalysisEvidence.parse(it)?.complete == true } ?: continue
                if (listOf("vocal_score", "classical_score", "calm_level", "energy_level", "orchestra_level")
                    .any { f.isNull(it) || !f.optDouble(it).isFinite() }) continue
                fun number(key: String): Double? = if (!f.has(key) || f.isNull(key)) null else f.optDouble(key).takeIf(Double::isFinite)
                db.tagDao().saveFeatures(TrackFeaturesEntity(t.trackKey, "${t.dateModified}:${t.size}", TagRepository.ANALYZER_VERSION,
                    number("bpm"), number("bpm_confidence") ?: 0.0, number("loudness") ?: -70.0, number("loudness_range") ?: 0.0,
                    number("onset_rate") ?: 0.0, number("centroid_hz") ?: 0.0, number("low_ratio") ?: 0.0, System.currentTimeMillis(), false,
                    number("vocal_score"), number("classical_score"), number("calm_level"), number("energy_level"), number("orchestra_level"), evidence, 1))
                imported = true
            }
        }
        if (imported && notify) tags.recomputeAll()
        if(folders.isEmpty() && !export) return JSONObject()
        val items = JSONObject()
        for (f in db.tagDao().allFeatures().filter { it.complete() && it.analyzerVersion == TagRepository.ANALYZER_VERSION }) {
            val t = byKey[f.trackKey] ?: continue
            if (f.signature != "${t.dateModified}:${t.size}") continue
            val data = JSONObject().put("bpm", f.bpm ?: JSONObject.NULL).put("bpm_confidence", f.bpmConfidence).put("loudness", f.loudness)
                .put("loudness_range", f.loudnessRange).put("onset_rate", f.onsetRate).put("centroid_hz", f.centroidHz).put("low_ratio", f.lowRatio)
                .put("vocal_score", f.vocalScore ?: JSONObject.NULL).put("classical_score", f.classicalScore ?: JSONObject.NULL)
                .put("calm_level", f.calmLevel ?: JSONObject.NULL).put("energy_level", f.energyLevel ?: JSONObject.NULL).put("orchestra_level", f.orchestraLevel ?: JSONObject.NULL)
                .put("analysis_evidence", f.analysisEvidence).put("analysis_attempts", f.analysisAttempts)
            items.put(reference(t.trackKey, roots), JSONObject().put("size", t.size).put("duration_ms", t.durationMs).put("features", data))
        }
        val text = JSONObject().put("format", 2).put("analyzer_version", TagRepository.ANALYZER_VERSION).put("items", items).toString()
        for (folder in folders) {
            val name = SyncFormat.featuresFileName(device.id)
            if (lastFeatures[folder.id] != text || tree.list(folder, DocumentTree.appPath(folder, "sync")).none { it.name == name })
                if (write(folder, name, text.toByteArray())) lastFeatures[folder.id] = text
        }
        return JSONObject(text)
    }

    suspend fun renameDevice(name: String) = withContext(Dispatchers.IO) { mutex.withLock { ensureLoaded(); device = device.copy(name = name.trim().ifEmpty { Build.MODEL }); lastWritten.clear(); saveJournal() } }
    suspend fun lanDeviceId(): String = mutex.withLock { ensureLoaded(); device.id }
    suspend fun exportLan(includeFeatures: Boolean = true): JSONObject = withContext(Dispatchers.IO) {
        mutex.withLock {
            ensureLoaded()
            if(groupActive)throw GroupSyncException("upgrade_required")
            val folders = db.folderDao().all()
            val state = capture(folders) { it }
            val result = JSONObject().put("state", SyncFormat.toJson(SyncFile(device, System.currentTimeMillis(), state)))
            val roots=folders.map { BackupRefs.Root(it.volumeName,it.relativePath) }
            result.getJSONObject("state").put("vocal_languages", LanguageExchange(db).export { reference(it, roots) })
            if(includeFeatures) result.put("features",shareFeatures(emptyList(),db.trackDao().all(),roots,{ null },export=true))
            saveJournal()
            result
        }
    }
    suspend fun acceptLan(payload: JSONObject) = withContext(Dispatchers.IO) {
        val state=SyncFormat.parse(payload.getJSONObject("state").toString()) ?: error("Invalid LAN state")
        mutex.withLock { ensureLoaded(); metadata.put("initialized",true); saveJournal() }
        syncNow(directoryRequested=false,incoming=state,incomingFeatures=payload.optJSONObject("features"))
        mutex.withLock {
            val roots = db.folderDao().all().map { BackupRefs.Root(it.volumeName,it.relativePath) }
            val references = db.trackDao().all().groupBy { reference(it.trackKey,roots).lowercase() }
                .filterValues { it.size == 1 }.mapValues { it.value.single().trackKey }
            val matcher = BackupRefs.Matcher(references)
            LanguageExchange(db).accept(payload.getJSONObject("state").optJSONObject("vocal_languages")) { matcher.match(it.lowercase()) }
        }
    }
    private val adapterSettings=context.getSharedPreferences("sync-adapter",Context.MODE_PRIVATE)
    val directoryEnabled get()=!groupActive && adapterSettings.getBoolean("legacy_directory",true)
    fun setDirectoryEnabled(enabled: Boolean) { check(!enabled || !groupActive) { "Group sync requires format 3" };adapterSettings.edit().putBoolean("legacy_directory",enabled).apply() }

    internal suspend fun <T> groupFrame(operation: suspend (GroupReplica)->T): T = withContext(Dispatchers.IO) {
        mutex.withLock { ensureLoaded();groupFrameUnlocked(operation=operation) }
    }
    internal suspend fun joinGroup(server: JSONObject,actor: String,incarnation: String,peer: String)=withContext(Dispatchers.IO) { mutex.withLock {
        ensureLoaded();acceptGroupDefinitions(server)
        groupFrameUnlocked(peerHint=peer) { it.join(server,actor,incarnation,peer) }
    } }
    internal suspend fun updateGroupAliases(server: JSONObject)=withContext(Dispatchers.IO) { mutex.withLock {
        ensureLoaded();acceptGroupDefinitions(server);saveJournal()
    } }
    suspend fun groupStatus(): JSONObject = withContext(Dispatchers.IO) { mutex.withLock {
        ensureLoaded()
        (if(groupReplica.active)groupReplica.info().put("joined",true) else JSONObject().put("joined",false))
            .put("paused",metadata.optBoolean("group_paused"))
            .put("collections",metadata.optJSONArray("group_collections") ?: JSONArray())
    } }
    internal suspend fun groupJoinIncarnation(peer: String): String = withContext(Dispatchers.IO) { mutex.withLock {
        ensureLoaded();check(!groupReplica.active) { "Already joined a sync group" }
        val pending=metadata.optJSONObject("group_join")
        if(pending!=null && pending.optString("peerId")==peer)pending.getString("incarnation")
        else UUID.randomUUID().toString().also { metadata.put("group_join",JSONObject().put("peerId",peer).put("incarnation",it));saveJournal() }
    } }
    suspend fun pauseGroup(value: Boolean) = withContext(Dispatchers.IO) { mutex.withLock {
        ensureLoaded();metadata.put("group_paused",value);saveJournal()
    } }
    internal suspend fun groupRecoveryIncarnation(): String = withContext(Dispatchers.IO) { mutex.withLock {
        ensureLoaded();check(groupReplica.active)
        metadata.optString("group_recovery_incarnation").ifEmpty { UUID.randomUUID().toString().also {
            metadata.put("group_recovery_incarnation",it);saveJournal()
        } }
    } }
    internal suspend fun recoverGroup(server: JSONObject,incarnation: String)=groupFrame {
        it.recover(server,incarnation);metadata.remove("group_recovery_incarnation")
    }
    private fun acceptGroupDefinitions(server: JSONObject) {
        val definitions=server.optJSONArray("collections") ?: JSONArray()
        if(definitions.length()>128)throw GroupSyncException("collection_limit")
        val ids=mutableSetOf<String>()
        repeat(definitions.length()) { index ->
            val definition=definitions.getJSONObject(index)
            if(!definition.getString("collectionId").matches(Regex("[0-9a-f]{32}")) || !definition.getString("rootId").matches(Regex("[0-9a-f]{32}")) || definition.getString("name").length !in 1..200 || !ids.add(definition.getString("collectionId")))throw GroupSyncException("invalid_collection")
        }
        metadata.put("group_root_bindings",server.optJSONObject("rootAliases") ?: JSONObject()).put("group_collections",definitions)
    }
    private fun canonicalGroupReference(value: String,peer: String,bindings: JSONObject): String {
        var reference=value;val prefix="peer:$peer/"
        if(peer.isNotEmpty() && reference.startsWith(prefix))reference=reference.removePrefix(prefix)
        if(reference.startsWith("root:") && peer.isNotEmpty() && (value.startsWith(prefix) || lan.primaryPeerId()==peer)) {
            val collection=bindings.optString(reference.substringAfter("root:").substringBefore('/'))
            return if(collection.isNotEmpty())"collection:$collection/"+reference.substringAfter('/') else "peer:$peer/$reference"
        }
        if(reference.startsWith("root:"))return "peer:${lan.primaryPeerId()}/$reference"
        return if(BackupRefs.isManagedReference(reference))reference else "device:${device.id}/$reference"
    }
    internal suspend fun bindGroupMapping(mapping: JSONObject)=withContext(Dispatchers.IO) { mutex.withLock {
        ensureLoaded()
        if(!groupReplica.active) { lan.saveMapping(mapping);return@withLock }
        groupFrameUnlocked { true }
        val peer=groupReplica.info().getString("peerId");val bindings=metadata.optJSONObject("group_root_bindings") ?: JSONObject()
        val roots=db.folderDao().all().map { BackupRefs.Root(it.volumeName,it.relativePath) }
        val rows=GroupSqlRows(db)
        db.withTransaction {
            for(track in db.trackDao().all()) {
                val raw=reference(track.trackKey,roots)
                val old=if(BackupRefs.isManagedReference(raw))canonicalGroupReference(raw,peer,bindings) else "device:${device.id}/local:${track.trackKey}"
                rows.put("binding-references",track.trackKey,JSONObject().put("reference",old).toString())
            }
            metadata.put("group_pending_binding",mapping);saveJournal()
        }
        // The mapping JSON and Room DB are separate durable stores. The intent above repairs
        // an interruption here before the next group projection can capture a false fresh edit.
        lan.saveMapping(mapping);groupFrameUnlocked { true }
    } }
    private suspend fun <T> groupFrameUnlocked(peerHint: String?=null,operation: suspend (GroupReplica)->T): T {
        applyPendingRules()
        val pendingBinding=metadata.optJSONObject("group_pending_binding")
        if(pendingBinding!=null)lan.saveMapping(pendingBinding)
        val folders=db.folderDao().all()
        val roots=folders.map { BackupRefs.Root(it.volumeName,it.relativePath) }
        val peer=peerHint ?: if(groupReplica.active)groupReplica.info().getString("peerId") else ""
        val bindings=metadata.optJSONObject("group_root_bindings") ?: JSONObject()
        var historicalMap=emptyMap<String,String>()
        fun canonical(value: String): String = canonicalGroupReference(value,peer,bindings).let { historicalMap[it] ?: historicalMap[value] ?: it }
        val tracks=db.trackDao().all()
        val librarySignatures=tracks.map { "${it.trackKey}:${it.size}:${it.dateModified}:${it.durationMs}:${it.startMs}" }
        val references=tracks.associate { track ->
            val reference=reference(track.trackKey,roots)
            track.trackKey to if(BackupRefs.isManagedReference(reference))canonical(reference) else "device:${device.id}/local:${track.trackKey}"
        }
        val index=references.entries.groupBy { it.value }.filterValues { it.size==1 }.mapValues { it.value.single().key }
        val bindingRows=GroupSqlRows(db)
        if(pendingBinding!=null) {
            val moves=mutableMapOf<String,String>();var after=""
            while(true) {
                val page=bindingRows.scan("binding-references",after)
                for(row in page)references[row.key]?.let { next -> val old=JSONObject(row.value).getString("reference");if(old!=next)moves[old]=next }
                if(page.isEmpty())break;after=page.last().key
            }
            historicalMap=moves
        }
        fun match(value: String)=index[value] ?: index[canonical(value)]
        suspend fun projection(): SyncState {
            val captured=capture(folders,::canonical)
            return if(metadata.optBoolean("pending_rules"))captured.copy(rules=previous.rules) else captured
        }
        suspend fun seeds(): Map<String,GroupSeed> {
            val state=SyncFormat.stateToJson(projection())
            state.put("vocal_languages",LanguageExchange(db).export { references[it] ?: canonical(reference(it,roots)) })
            val caches=JSONArray();val byKey=tracks.associateBy { it.trackKey }
            for(row in db.tagDao().allFeatures().filter { it.complete() && it.analyzerVersion==TagRepository.ANALYZER_VERSION }) {
                val track=byKey[row.trackKey] ?: continue
                if(row.signature!="${track.dateModified}:${track.size}")continue
                caches.put(JSONObject().put("reference",references.getValue(track.trackKey)).put("analyzer_version",row.analyzerVersion)
                    .put("size",track.size).put("duration_ms",track.durationMs).put("start_ms",track.startMs ?: 0L).put("updated_at",row.analyzedAt)
                    .put("features",featureJson(row)))
            }
            state.put("analysis_cache",caches)
            return GroupUserData.flatten(state)
        }
        var changed=false
        try {
            val result=db.withTransaction {
                val existed=groupReplica.active
                previous=SyncMerge.normalizeReferences(previous,::canonical)
                if(existed && (groupLibrarySignatures!=librarySignatures || pendingBinding!=null)) { apply(MergeResult(projection(),emptyList(),emptyList()),::match);changed=true }
                if(existed && historicalMap.isNotEmpty())groupReplica.rebindReferences({ historicalMap[it] ?: it },seeds())
                if(existed)groupReplica.capture(seeds())
                val result=operation(groupReplica)
                if(groupReplica.active) {
                    if(!existed)groupReplica.capture(seeds())
                    if(groupLibrarySignatures!=librarySignatures || pendingBinding!=null)groupReplica.reapplyCaches()
                    while(true) {
                        val mutations=groupReplica.pendingApply();if(mutations.isEmpty())break
                        val local=projection()
                        val newest=GroupUserData.flatten(SyncFormat.stateToJson(local)).values.maxOfOrNull { it.editedAtMs } ?: 0
                        if(newest>=GroupSyncContract.MAX_INTEGER)throw GroupSyncException("clock_exhausted")
                        val stamp=maxOf(System.currentTimeMillis(),newest+1)
                        val changes=SyncFormat.stateFromJson(GroupUserData.materialize(mutations.filterNot { GroupUserData.isCache(it.key) },stamp))
                        for(mutation in mutations.filter { GroupUserData.isCache(it.key) }) {
                            val cache=GroupUserData.materialize(listOf(mutation),stamp)
                            LanguageExchange(db).accept(cache.optJSONObject("vocal_languages"),::match)
                            val entries=cache.optJSONArray("analysis_cache")
                            if(entries!=null)repeat(entries.length()) { index ->
                                val entry=entries.getJSONObject(index)
                                val track=match(entry.getString("reference"))?.let { key -> tracks.singleOrNull { it.trackKey==key } }
                                if(track!=null && entry.getLong("start_ms")== (track.startMs ?: 0L)) {
                                    val items=JSONObject().put(entry.getString("reference"),entry)
                                    shareFeatures(emptyList(),tracks,roots,::match,JSONObject().put("format",2).put("analyzer_version",entry.getInt("analyzer_version")).put("items",items),notify=false)
                                }
                            }
                        }
                        val merged=SyncMerge.merge(local,listOf(SyncFile(SyncDevice("group-projection","同步组","group","3"),stamp,changes)),device.id,stamp,retainTombstones=true)
                        apply(merged,::match)
                        if(changes.rules!=null)metadata.put("pending_rules",true)
                        previous=merged.state
                        groupReplica.projected(seeds(),mutations)
                        changed=true
                    }
                    metadata.put("initialized",true)
                    if(pendingBinding!=null) {
                        while(true) { val page=bindingRows.scan("binding-references");if(page.isEmpty())break;for(row in page)bindingRows.delete("binding-references",row.key) }
                        metadata.remove("group_pending_binding")
                    }
                    saveJournal()
                }
                result
            }
            groupActive=groupReplica.active
            groupLibrarySignatures=librarySignatures
            if(groupActive)adapterSettings.edit().putBoolean("legacy_directory",false).apply()
            applyPendingRules()
            if(changed)tags.recomputeAll()
            return result
        } catch(exception: Exception) { loaded=false;throw exception }
    }
    private fun featureJson(row: TrackFeaturesEntity)=JSONObject()
        .put("bpm",row.bpm ?: JSONObject.NULL).put("bpm_confidence",row.bpmConfidence).put("loudness",row.loudness)
        .put("loudness_range",row.loudnessRange).put("onset_rate",row.onsetRate).put("centroid_hz",row.centroidHz).put("low_ratio",row.lowRatio)
        .put("vocal_score",row.vocalScore ?: JSONObject.NULL).put("classical_score",row.classicalScore ?: JSONObject.NULL)
        .put("calm_level",row.calmLevel ?: JSONObject.NULL).put("energy_level",row.energyLevel ?: JSONObject.NULL).put("orchestra_level",row.orchestraLevel ?: JSONObject.NULL)
        .put("analysis_evidence",row.analysisEvidence).put("analysis_attempts",row.analysisAttempts)
    suspend fun history(): List<Pair<Int, String>> = withContext(Dispatchers.IO) { mutex.withLock { ensureLoaded(); List(history.length()) { it to history.getJSONObject(it).getJSONObject("playlist").getString("name") } } }
    suspend fun restoreHistory(index: Int) = withContext(Dispatchers.IO) {
        mutex.withLock {
            ensureLoaded()
            val item = history.optJSONObject(index)?.optJSONObject("playlist") ?: return@withLock
            val p = SyncFormat.stateFromJson(JSONObject().put("playlists", JSONArray().put(item))).playlists.values.single()
            val restored = p.copy(updatedAt = System.currentTimeMillis())
            previous = previous.copy(playlists = previous.playlists + (p.uid to restored))
            val roots = db.folderDao().all().map { BackupRefs.Root(it.volumeName, it.relativePath) }
            val matcher = BackupRefs.Matcher(db.trackDao().all().associate { reference(it.trackKey, roots).lowercase() to it.trackKey })
            db.withTransaction {
                apply(MergeResult(previous, emptyList(), emptyList())) { matcher.match(it.lowercase()) }
                saveJournal()
            }
        }
        syncNow()
    }
}
