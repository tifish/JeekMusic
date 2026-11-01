package com.tinyfish.jeekmusic.data.preference

import org.json.JSONObject

/** A page is acknowledged only after its data is saved; batches resume across foreground exchanges. */
internal class DesktopResultCursor(private val store: PreferenceStore) {
    private val generation=store.generation
    var offset=0
        private set
    var version=""
        private set
    init {
        runCatching { store.local(KEY)?.let(::JSONObject) }.getOrNull()?.let {
            if(it.optString("generation")==generation && it.optString("version").matches(Regex("[0-9a-f]{64}")) && it.optInt("offset")>0) {
                offset=it.getInt("offset"); version=it.getString("version")
            }
        }
    }
    fun commit(page: JSONObject) {
        require(store.generation==generation && page.getString("generation")==generation) { "Learning changed during result sync" }
        val next=page.getInt("next"); val nextVersion=page.getString("version"); val more=page.getBoolean("hasMore")
        require(nextVersion.matches(Regex("[0-9a-f]{64}")) && (offset==0 || version==nextVersion) && (!more || next>offset)) { "Result page changed or did not advance" }
        store.setLocalForGeneration(KEY,JSONObject().put("generation",generation).put("offset",if(more) next else 0).put("version",if(more) nextVersion else "").toString(),generation)
        offset=if(more) next else 0; version=if(more) nextVersion else ""
    }
    fun reset() { store.removeLocal(KEY); offset=0; version="" }
    companion object { const val KEY="desktop-result-cursor" }
}
