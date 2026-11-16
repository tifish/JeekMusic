package com.tinyfish.jeekmusic.data.sync

import android.content.Context
import android.util.AtomicFile
import dagger.hilt.android.qualifiers.ApplicationContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileNotFoundException
import java.io.IOException
import javax.inject.Inject
import javax.inject.Singleton

internal object LanBindings {
    /** Replace one CUE's edges only; retain other sheets that still own an audio binding. */
    fun replace(bindings: JSONObject, cue: String, audio: List<String>): Set<String> {
        val affected = audio.toMutableSet()
        for (path in bindings.keys().asSequence().toList()) {
            val old = bindings.getJSONArray(path)
            val names = List(old.length()) { old.getString(it) }
            if (cue !in names) continue
            affected += path
            val kept = names.filterNot { it == cue }
            if (kept.isEmpty()) bindings.remove(path) else bindings.put(path, JSONArray(kept))
        }
        for (path in audio.distinct()) {
            val names = bindings.optJSONArray(path) ?: JSONArray().also { bindings.put(path, it) }
            names.put(cue)
        }
        return affected
    }
}

/** Device-local settings and credentials: never exported through user-data sync. */
@Singleton
class LanSyncStore @Inject constructor(@param:ApplicationContext context: Context, private val ledger: LanSyncLedger,private val fileDatabase: LanFileDatabase=LanFileDatabase(context)) {
    private val file = AtomicFile(File(context.filesDir, "lan-sync.json"))
    private var data: JSONObject
    init {
        val bytes = try { file.readFully() } catch (e: FileNotFoundException) {
            if (file.baseFile.exists() || File(file.baseFile.path + ".bak").exists() || File(file.baseFile.path + ".new").exists()) throw e
            null
        }
        val original = bytes?.let { JSONObject(it.toString(Charsets.UTF_8)) } ?: JSONObject()
        val migrated = LanPeerSettings.migrate(original)
        if (migrated.toString() != original.toString()) {
            if (bytes != null && original.optInt("schemaVersion") < LanPeerSettings.VERSION) {
                val backup = AtomicFile(File(context.filesDir, "lan-sync-v1.json"))
                if (!backup.baseFile.exists()) writeFile(backup, bytes)
            }
            writeFile(file, migrated.toString().toByteArray(Charsets.UTF_8))
        }
        data = migrated
    }
    private fun writeFile(target: AtomicFile, bytes: ByteArray) {
        val stream = target.startWrite()
        try {
            stream.write(bytes); stream.fd.sync(); target.finishWrite(stream)
            if (File(target.baseFile.path + ".new").exists() || !target.baseFile.readBytes().contentEquals(bytes))
                throw IOException("Sync settings commit verification failed")
        } catch (e: Exception) { target.failWrite(stream); throw e }
    }
    @Synchronized fun read(): JSONObject = JSONObject(data.toString())
    @Synchronized fun update(acceptLegacyConnection: Boolean = true, change: (JSONObject) -> Unit) {
        val next = JSONObject(data.toString())
        val before = next.optJSONObject("connection")?.toString()
        change(next)
        if (acceptLegacyConnection && before != next.optJSONObject("connection")?.toString()) LanPeerSettings.importLegacyConnection(next)
        LanPeerSettings.normalize(next)
        writeFile(file, next.toString().toByteArray(Charsets.UTF_8)); data = next
    }
    fun peers(): List<JSONObject> = LanPeerSettings.rows(read())
    fun primaryPeerId(): String = read().optString("primaryPeerId")
    fun selectedPeerId(): String = read().optString("selectedPeerId")
    fun connection(peerId: String = selectedPeerId()): JSONObject = peers().single { it.getString("peerId") == peerId }
    fun connectionFor(mapping: JSONObject): JSONObject = connection(mapping.optString("peerId").ifEmpty { primaryPeerId() })
    fun addPeer(connection: JSONObject): String {
        var id = ""
        update(false) { id = LanPeerSettings.add(it, connection) }
        return id
    }
    fun selectPeer(peerId: String) = update(false) { state ->
        require(LanPeerSettings.rows(state).any { it.getString("peerId") == peerId })
        state.put("selectedPeerId", peerId)
    }
    fun updatePeer(peerId: String, change: (JSONObject) -> Unit) = update(false) { state ->
        change(LanPeerSettings.rows(state).single { it.getString("peerId") == peerId })
    }
    fun forgetPeer(peerId: String) = update(false) { state ->
        state.put("peers", JSONArray(LanPeerSettings.rows(state).filterNot { it.getString("peerId") == peerId }))
        state.put("mappings", JSONArray(mappings().filterNot { it.optString("peerId") == peerId }))
        if (state.optString("primaryPeerId") == peerId) state.put("primaryPeerId", "")
    }
    @Synchronized fun reference(trackKey: String): String? {
        val mappings = data.optJSONArray("mappings") ?: return null
        for (i in 0 until mappings.length()) {
            val m = mappings.getJSONObject(i)
            if(!LanMappingTargets.isCurrent(m)) continue
            val prefix = m.optString("volume") + ":" + m.optString("relative")
            if (m.optString("relative").isNotEmpty() && trackKey.startsWith(prefix))
                return (if (m.optString("peerId") == data.optString("primaryPeerId")) "" else "peer:${m.optString("peerId")}/") +
                    "root:${m.getString("rootId")}/" + trackKey.removePrefix(prefix)
        }
        return null
    }
    fun mappings(): List<JSONObject> = read().optJSONArray("mappings")?.let { a -> List(a.length()) { a.getJSONObject(it) } }.orEmpty()
    /** A rename transaction may temporarily remove the public name. Never infer a deletion. */
    fun pendingFileKeys(): Set<String> {
        // read() releases the ledger monitor before mappings() takes the settings monitor.
        // An unreadable ledger aborts the scan rather than inferring a deletion.
        val state=ledger.read()
        val current=mappings().filter { LanMappingTargets.isCurrent(it) }.flatMap { mapping -> fileDatabase.pendingPaths(mapping.getString("key")).map { mapping.getString("volume")+":"+mapping.getString("relative")+LanProtocol.relative(it) } }
        return (current+listOfNotNull(state.optJSONObject("pending"),state.optJSONObject("obsoletePending")).map { pending ->
            val mapping=pending.optJSONObject("mapping") ?: mappings().single { it.getString("key")==pending.getString("key") }
            mapping.getString("volume")+":"+mapping.getString("relative")+LanProtocol.relative(pending.getString("path"))
        }).toSet()
    }
    @Synchronized fun cues(fileKey: String): List<String> {
        val mappings = data.optJSONArray("mappings") ?: return emptyList()
        for (i in 0 until mappings.length()) {
            val m = mappings.getJSONObject(i)
            if(!LanMappingTargets.isCurrent(m)) continue
            val prefix = m.getString("volume") + ":" + m.getString("relative")
            if (!fileKey.startsWith(prefix)) continue
            return fileDatabase.cues(m.getString("key"),fileKey.removePrefix(prefix)).map { m.getString("relative")+LanProtocol.relative(it) }
        }
        return emptyList()
    }
    fun saveMapping(mapping: JSONObject) = update { state ->
        if (!mapping.has("peerId") && state.optString("primaryPeerId").isNotEmpty()) mapping.put("peerId", state.getString("primaryPeerId"))
        val list = state.optJSONArray("mappings") ?: JSONArray()
        val index = (0 until list.length()).firstOrNull { LanPeerSettings.sameMapping(list.getJSONObject(it), mapping) }
        // Updating a switch or sync checkpoint must not move its folder under the user's finger.
        if (index == null) list.put(mapping) else list.put(index, mapping)
        state.put("mappings", list)
    }
}
