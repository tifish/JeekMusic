package com.tinyfish.jeekmusic.data.filter

import androidx.room.withTransaction
import com.tinyfish.jeekmusic.analysis.SingingLanguage
import com.tinyfish.jeekmusic.data.db.*
import kotlinx.coroutines.flow.combine
import javax.inject.Inject
import javax.inject.Singleton

@Singleton
class LanguageRepository @Inject constructor(private val db: JeekDatabase) {
    val results = combine(db.trackDao().observeAll(), db.languageDao().observeAll()) { tracks, entries ->
        val signatures = tracks.associate { it.trackKey to it.languageSignature() }
        entries.filter { signatures[it.trackKey] == it.signature && it.state != "auto" }.groupBy { it.trackKey }.mapValues { (_, values) ->
            (values.firstOrNull { it.source == "MANUAL" } ?: values.first()).result()
        }
    }
    suspend fun result(track: TrackEntity): SingingLanguage? = db.languageDao().forTrack(track.trackKey)
        .filter { it.signature == track.languageSignature() && it.state != "auto" }.let { values ->
            (values.firstOrNull { it.source == "MANUAL" } ?: values.firstOrNull())?.result()
        }
    suspend fun save(track: TrackEntity, result: SingingLanguage) = db.withTransaction {
        validate(track)
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
