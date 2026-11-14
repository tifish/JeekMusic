package com.tinyfish.jeekmusic.data.db

import androidx.room.*
import com.tinyfish.jeekmusic.analysis.SingingLanguage
import kotlinx.coroutines.flow.Flow

@Entity(tableName = "track_language", primaryKeys = ["track_key", "source"], foreignKeys = [
    ForeignKey(entity = TrackEntity::class, parentColumns = ["track_key"], childColumns = ["track_key"], onDelete = ForeignKey.CASCADE, onUpdate = ForeignKey.CASCADE)
])
data class TrackLanguageEntity(
    @ColumnInfo(name = "track_key") val trackKey: String,
    val source: String,
    val signature: String,
    val state: String,
    val languages: String,
    val confidence: Double?,
    @ColumnInfo(name = "updated_at") val updatedAt: Long = System.currentTimeMillis(),
) {
    fun result() = SingingLanguage(state, languages.split(',').filter { it.isNotEmpty() }, source, confidence)
}

fun TrackEntity.languageSignature() = "$dateModified:$modifiedNanos:$size:$startMs:$endMs:$durationMs"

@Dao
interface LanguageDao {
    @Query("SELECT * FROM track_language") fun observeAll(): Flow<List<TrackLanguageEntity>>
    @Query("SELECT * FROM track_language") suspend fun all(): List<TrackLanguageEntity>
    @Query("SELECT * FROM track_language WHERE track_key = :key") suspend fun forTrack(key: String): List<TrackLanguageEntity>
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun save(value: TrackLanguageEntity)
}
