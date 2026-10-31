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
                db.invalidationTracker.createFlow("playlist", "playlist_item", "pinned_tab", "tag_override", "lyrics_offset", "track").map { Unit },
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
        val actual = db.tagDao().overrides().sortedBy { !it.trackKey.startsWith("foreign:") }.associate { (canonical(reference(it.trackKey, roots)) to it.dimension) to when {
            it.value != null -> it.value
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

    private suspend fun shareFeatures(folders: List<LibraryFolderEntity>, tracks: List<TrackEntity>, roots: List<BackupRefs.Root>, match: (String) -> String?, incoming: JSONObject? = null, export: Boolean = false): JSONObject {
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
        if (imported) tags.recomputeAll()
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
            val folders = db.folderDao().all()
            val state = capture(folders) { it }
            val result = JSONObject().put("state", SyncFormat.toJson(SyncFile(device, System.currentTimeMillis(), state)))
            val roots=folders.map { BackupRefs.Root(it.volumeName,it.relativePath) }
            if(includeFeatures) result.put("features",shareFeatures(emptyList(),db.trackDao().all(),roots,{ null },export=true))
            saveJournal()
            result
        }
    }
    suspend fun acceptLan(payload: JSONObject) = withContext(Dispatchers.IO) {
        val state=SyncFormat.parse(payload.getJSONObject("state").toString()) ?: error("Invalid LAN state")
        mutex.withLock { ensureLoaded(); metadata.put("initialized",true); saveJournal() }
        syncNow(directoryRequested=false,incoming=state,incomingFeatures=payload.optJSONObject("features"))
    }
    private val adapterSettings=context.getSharedPreferences("sync-adapter",Context.MODE_PRIVATE)
    val directoryEnabled get()=adapterSettings.getBoolean("legacy_directory",true)
    fun setDirectoryEnabled(enabled: Boolean) { adapterSettings.edit().putBoolean("legacy_directory",enabled).apply() }
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
