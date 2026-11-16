package com.tinyfish.jeekmusic.data.sync

import org.json.JSONArray
import org.json.JSONObject

/** Pure settings migration. connection remains an alias for the ONE legacy metadata/learning peer. */
internal object LanPeerSettings {
    const val VERSION = 2
    const val MAX_PEERS = 32
    fun rows(state: JSONObject): List<JSONObject> = state.optJSONArray("peers")?.let { a -> List(a.length()) { a.getJSONObject(it) } }.orEmpty()
    private fun legacyId(connection: JSONObject) = connection.optString("pin").takeIf { it.matches(Regex("[0-9a-f]{64}")) } ?: "legacy"

    fun migrate(original: JSONObject): JSONObject {
        val state = JSONObject(original.toString())
        require(state.optInt("schemaVersion") <= VERSION) { "Newer sync settings require a newer client" }
        if (state.optInt("schemaVersion") == VERSION) require(state.opt("peers") is JSONArray) { "Peer settings are incomplete" }
        if (state.optInt("schemaVersion") < VERSION) {
            val connection = state.optJSONObject("connection")
            val peers = JSONArray()
            if (connection != null) {
                val id = legacyId(connection)
                peers.put(JSONObject(connection.toString()).put("peerId", id))
                state.put("primaryPeerId", id).put("selectedPeerId", id)
            } else {
                val mappings=state.optJSONArray("mappings")
                if(mappings!=null) repeat(mappings.length()) { index ->
                    val mapping=mappings.getJSONObject(index)
                    if(!mapping.has("peerId"))mapping.put("peerId","unbound")
                }
            }
            state.put("peers", peers).put("schemaVersion", VERSION)
        }
        normalize(state)
        return state
    }

    fun normalize(state: JSONObject) {
        val peers = rows(state)
        require(peers.size <= MAX_PEERS && peers.map { it.getString("peerId") }.toSet().size == peers.size) { "Invalid peer list" }
        require(peers.all { it.getString("peerId").matches(Regex("[a-zA-Z0-9_-]{1,64}")) }) { "Invalid peer identity" }
        val primary = state.optString("primaryPeerId")
        val connection = peers.singleOrNull { it.getString("peerId") == primary }
        if (connection != null) state.put("connection", JSONObject(connection.toString())) else state.remove("connection")
        if (peers.none { it.getString("peerId") == state.optString("selectedPeerId") })
            state.put("selectedPeerId", connection?.getString("peerId") ?: peers.firstOrNull()?.getString("peerId").orEmpty())
        val mappings = state.optJSONArray("mappings")
        if (connection != null && mappings != null) repeat(mappings.length()) { index ->
            val mapping = mappings.getJSONObject(index)
            if (!mapping.has("peerId")) mapping.put("peerId", primary)
        }
    }

    /** Compatibility for existing internal callers that update the legacy connection explicitly. */
    fun importLegacyConnection(state: JSONObject) {
        val connection = state.optJSONObject("connection")
        if (connection == null) {
            state.put("primaryPeerId", "")
            return
        }
        val id = connection.optString("peerId").ifEmpty { legacyId(connection) }
        val primaryChanged = state.optString("primaryPeerId") != id
        val peers = rows(state).toMutableList()
        val index = peers.indexOfFirst { it.getString("peerId") == id }
        val copy = JSONObject(connection.toString()).put("peerId", id)
        if (index >= 0) peers[index] = copy else peers.add(copy)
        state.put("peers", JSONArray(peers)).put("primaryPeerId", id)
        if (primaryChanged) state.put("selectedPeerId", id)
    }

    fun add(state: JSONObject, connection: JSONObject): String {
        val id = legacyId(connection)
        require(id != "legacy") { "Missing authenticated PC fingerprint" }
        val peers = rows(state).toMutableList()
        val index = peers.indexOfFirst { it.getString("peerId") == id }
        require(index >= 0 || peers.size < MAX_PEERS) { "Too many paired computers" }
        val peer = JSONObject(connection.toString()).put("peerId", id)
        if (index >= 0) {
            peers[index].optJSONObject("schedule")?.let { peer.put("schedule", JSONObject(it.toString())) }
            peers[index] = peer
        } else peers.add(peer)
        if (peers.size == 1 && state.optString("primaryPeerId").isEmpty()) state.put("primaryPeerId", id)
        state.put("peers", JSONArray(peers)).put("selectedPeerId", id)
        normalize(state)
        return id
    }

    fun sameMapping(left: JSONObject, right: JSONObject) =
        left.optString("peerId") == right.optString("peerId") && left.getString("rootId") == right.getString("rootId")
}
