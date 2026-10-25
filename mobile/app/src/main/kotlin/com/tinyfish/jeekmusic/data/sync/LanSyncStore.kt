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
class LanSyncStore @Inject constructor(@param:ApplicationContext context: Context) {
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
            val prefix = m.optString("volume") + ":" + m.optString("relative")
            if (m.optString("relative").isNotEmpty() && trackKey.startsWith(prefix))
                return "root:${m.getString("rootId")}/" + trackKey.removePrefix(prefix)
        }
        return null
    }
    fun mappings(): List<JSONObject> = read().optJSONArray("mappings")?.let { a -> List(a.length()) { a.getJSONObject(it) } }.orEmpty()
    /** A rename transaction may temporarily remove the public name. Never infer a deletion. */
    fun pendingFileKeys(): Set<String> = runCatching {
        val pending = JSONObject(AtomicFile(File(file.baseFile.parentFile, "lan-owned.json")).readFully().toString(Charsets.UTF_8))
            .optJSONObject("pending") ?: return@runCatching emptySet<String>()
        val mapping = pending.optJSONObject("mapping") ?: mappings().single { it.getString("key") == pending.getString("key") }
        setOf(mapping.getString("volume") + ":" + mapping.getString("relative") + LanProtocol.relative(pending.getString("path")))
    }.getOrDefault(emptySet())
    @Synchronized fun cues(fileKey: String): List<String> {
        val mappings = data.optJSONArray("mappings") ?: return emptyList()
        for (i in 0 until mappings.length()) {
            val m = mappings.getJSONObject(i)
            val prefix = m.getString("volume") + ":" + m.getString("relative")
            if (!fileKey.startsWith(prefix)) continue
            val a = m.optJSONObject("cueBindings")?.optJSONArray(fileKey.removePrefix(prefix)) ?: return emptyList()
            return List(a.length()) { m.getString("relative") + LanProtocol.relative(a.getString(it)) }
        }
        return emptyList()
    }
    fun saveMapping(mapping: JSONObject) = update { state ->
        val list = mappings().filterNot { it.getString("rootId") == mapping.getString("rootId") } + mapping
        state.put("mappings", JSONArray(list))
    }
}
