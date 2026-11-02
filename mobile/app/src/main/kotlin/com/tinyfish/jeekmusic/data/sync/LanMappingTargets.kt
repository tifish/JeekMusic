package com.tinyfish.jeekmusic.data.sync

import org.json.JSONObject

/** Checks actual managed destination overlap; does not infer which other apps use storage. */
internal object LanMappingTargets {
    const val VERSION=2
    fun isCurrent(mapping: JSONObject)=mapping.optInt("layoutVersion")==VERSION
    fun requireCurrent(mapping: JSONObject) { require(isCurrent(mapping)) { "Reselect destination: old mapping layout is no longer executable; existing files are retained" } }
    fun requireSeparate(mappings: List<JSONObject>, rootId: String, volume: String, relative: String) {
        for (other in mappings.filter { isCurrent(it) && it.getString("rootId") != rootId }) {
            if (other.getString("volume") == volume) {
                val candidate = relative.lowercase(java.util.Locale.ROOT)
                val existing = other.getString("relative").lowercase(java.util.Locale.ROOT)
                require(!candidate.startsWith(existing) && !existing.startsWith(candidate)) { "Overlapping managed destinations" }
            }
        }
    }
}
