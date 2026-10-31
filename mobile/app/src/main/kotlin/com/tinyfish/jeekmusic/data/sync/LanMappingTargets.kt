package com.tinyfish.jeekmusic.data.sync

import org.json.JSONObject

/** Checks actual managed destination overlap; does not infer which other apps use storage. */
internal object LanMappingTargets {
    fun requireSeparate(mappings: List<JSONObject>, rootId: String, volume: String, relative: String) {
        for (other in mappings.filter { it.getString("rootId") != rootId }) {
            if (other.getString("volume") == volume) {
                val candidate = (relative + "JeekMusic-$rootId/").lowercase()
                val existing = other.getString("relative").lowercase()
                require(!candidate.startsWith(existing) && !existing.startsWith(candidate)) { "Overlapping managed destinations" }
            }
        }
    }
}
