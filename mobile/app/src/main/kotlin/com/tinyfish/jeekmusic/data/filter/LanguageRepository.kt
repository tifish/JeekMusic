package com.tinyfish.jeekmusic.data.filter

import androidx.room.withTransaction
import com.tinyfish.jeekmusic.analysis.SingingLanguage
import com.tinyfish.jeekmusic.data.db.*
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.map
import javax.inject.Inject
import javax.inject.Singleton

@Singleton
class LanguageRepository @Inject constructor(private val db: JeekDatabase) {
    private val library = combine(db.trackDao().observeAll(), db.languageDao().observeAll()) { tracks, entries ->
        val signatures = tracks.associate { it.trackKey to it.languageSignature() }
        val results = entries.filter { signatures[it.trackKey] == it.signature && it.state != "auto" }.groupBy { it.trackKey }.mapValues { (_, values) ->
            (values.firstOrNull { it.source == "MANUAL" } ?: values.first()).result()
        }
        tracks.size to results
    }
    val results = library.map { it.second }
    val coverage = library.map { (total, results) -> LanguageCoverage(total, results.size,
        results.values.count { it.state == "known" }, results.values.count { it.state == "instrumental" }, results.values.count { it.state == "unknown" }) }
    suspend fun result(track: TrackEntity): SingingLanguage? = db.languageDao().forTrack(track.trackKey)
        .filter { it.signature == track.languageSignature() && it.state != "auto" }.let { values ->
            (values.firstOrNull { it.source == "MANUAL" } ?: values.firstOrNull())?.result()
        }
    suspend fun save(track: TrackEntity, result: SingingLanguage, onlyIfMissing: Boolean = false) = db.withTransaction {
        validate(track)
        if (onlyIfMissing && result(track) != null) return@withTransaction
        val at = maxOf(System.currentTimeMillis(), (db.languageDao().forTrack(track.trackKey).firstOrNull { it.source == result.source }?.updatedAt ?: 0) + 1)
        db.languageDao().save(TrackLanguageEntity(track.trackKey, result.source, track.languageSignature(), result.state, result.languages.joinToString(","), result.confidence, at))
    }
    suspend fun setManual(tracks: List<TrackEntity>, value: String) = db.withTransaction {
        val targets = tracks.distinctBy { it.trackKey }
        require(targets.size in 1..1000)
        val result = if (value == "AUTO") SingingLanguage("auto", emptyList(), "MANUAL") else SingingLanguage.manual(value)
        targets.forEach { validate(it) }
        targets.forEach { save(it, result) }
    }
    private suspend fun validate(target: TrackEntity) {
        val current = db.trackDao().byId(target.id)
        check(current?.trackKey == target.trackKey && current.languageSignature() == target.languageSignature()) { "Recording changed; rescan before correcting language" }
    }
}

/** Committed library coverage, independent of local recognition task progress. */
data class LanguageCoverage(val total: Int, val cached: Int, val known: Int, val instrumental: Int, val unknown: Int) {
    val pending get() = total - cached
}
