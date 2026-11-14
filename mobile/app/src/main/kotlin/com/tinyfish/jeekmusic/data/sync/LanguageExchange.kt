package com.tinyfish.jeekmusic.data.sync

import androidx.room.withTransaction
import com.tinyfish.jeekmusic.data.db.*
import org.json.JSONArray
import org.json.JSONObject
import kotlin.math.abs

/** Optional LAN metadata extension. No executable model, PCM, transcript or file hashing. */
class LanguageExchange(private val db: JeekDatabase) {
    suspend fun export(reference: (String) -> String): JSONObject {
        val tracks = db.trackDao().all().associateBy { it.trackKey }
        val items = JSONObject()
        for (row in db.languageDao().all()) {
            val track = tracks[row.trackKey] ?: continue
            if (row.signature != track.languageSignature()) continue
            val ref = reference(row.trackKey)
            val values = items.optJSONArray(ref) ?: JSONArray().also { items.put(ref, it) }
            values.put(JSONObject().put("source", row.source).put("state", row.state).put("languages", JSONArray(row.result().languages))
                .put("confidence", row.confidence ?: JSONObject.NULL).put("updated_at", row.updatedAt)
                .put("size", track.size).put("duration_ms", track.durationMs).put("start_ms", track.startMs ?: 0))
        }
        return JSONObject().put("version", 1).put("items", items)
    }
    suspend fun accept(input: JSONObject?, match: (String) -> String?) {
        if (input == null) return
        require(input.getInt("version") == 1) { "Unsupported vocal-language exchange" }
        val items = input.getJSONObject("items")
        require(items.length() <= 100_000)
        db.withTransaction {
            val tracks = db.trackDao().all().associateBy { it.trackKey }
            for (ref in items.keys()) {
                val track = match(ref)?.let(tracks::get) ?: continue
                val rows = items.getJSONArray(ref)
                require(rows.length() <= 2)
                val old = db.languageDao().forTrack(track.trackKey).associateBy { it.source }
                val seen = mutableSetOf<String>()
                for (i in 0 until rows.length()) {
                    val row = rows.getJSONObject(i)
                    val source = row.getString("source"); val state = row.getString("state")
                    require(seen.add(source)) { "Duplicate vocal-language source" }
                    val languages = row.getJSONArray("languages").let { a -> List(a.length()) { a.getString(it) } }
                    val at = row.getLong("updated_at")
                    val confidence = if (row.isNull("confidence")) null else row.getDouble("confidence")
                    require(source in listOf("AUTO", "MANUAL") && state in listOf("known", "unknown", "instrumental", "auto"))
                    require(state != "auto" || source == "MANUAL")
                    require(languages.size <= 16 && languages.distinct().size == languages.size && languages.all { it.matches(Regex("[a-z]{2,3}|other")) })
                    require((state == "known") == languages.isNotEmpty() && at >= 0 && (confidence == null || confidence.isFinite() && confidence in 0.0..1.0))
                    if (row.getLong("size") != track.size || abs(row.getLong("duration_ms") - track.durationMs) > 1000 || row.getLong("start_ms") != (track.startMs ?: 0L)) continue
                    val prior = old[source]
                    if (prior != null && prior.signature == track.languageSignature()) {
                        if (prior.updatedAt > at) continue
                        if (prior.updatedAt == at) {
                            val previousKey = prior.state + ":" + prior.result().languages.sorted().joinToString(",")
                            val incomingKey = state + ":" + languages.sorted().joinToString(",")
                            if (previousKey > incomingKey || previousKey == incomingKey && (prior.confidence ?: -1.0) >= (confidence ?: -1.0)) continue
                        }
                    }
                    db.languageDao().save(TrackLanguageEntity(track.trackKey, source, track.languageSignature(), state, languages.joinToString(","), confidence, at))
                }
            }
        }
    }
}
