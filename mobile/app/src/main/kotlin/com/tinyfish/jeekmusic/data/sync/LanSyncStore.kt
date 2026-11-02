package com.tinyfish.jeekmusic.data.sync

import android.content.Context
import android.util.AtomicFile
import dagger.hilt.android.qualifiers.ApplicationContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
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
    private var data = runCatching { JSONObject(file.readFully().toString(Charsets.UTF_8)) }.getOrDefault(JSONObject())
    @Synchronized fun read(): JSONObject = JSONObject(data.toString())
    @Synchronized fun update(change: (JSONObject) -> Unit) {
        val next = JSONObject(data.toString()); change(next)
        val stream = file.startWrite()
        try { stream.write(next.toString().toByteArray()); file.finishWrite(stream); data = next }
        catch (e: Exception) { file.failWrite(stream); throw e }
    }
    @Synchronized fun reference(trackKey: String): String? {
        val mappings = data.optJSONArray("mappings") ?: return null
        for (i in 0 until mappings.length()) {
            val m = mappings.getJSONObject(i)
            if(!LanMappingTargets.isCurrent(m)) continue
            val prefix = m.optString("volume") + ":" + m.optString("relative")
            if (m.optString("relative").isNotEmpty() && trackKey.startsWith(prefix))
                return "root:${m.getString("rootId")}/" + trackKey.removePrefix(prefix)
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
        val list = mappings().filterNot { it.getString("rootId") == mapping.getString("rootId") } + mapping
        state.put("mappings", JSONArray(list))
    }
}
