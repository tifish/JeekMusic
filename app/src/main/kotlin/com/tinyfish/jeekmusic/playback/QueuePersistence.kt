package com.tinyfish.jeekmusic.playback

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import androidx.room.withTransaction
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.QueueDao
import com.tinyfish.jeekmusic.data.db.QueueItemEntity
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.first
import javax.inject.Inject
import javax.inject.Singleton

data class RestoredQueue(
    val tracks: List<TrackEntity>,
    val index: Int,
    val positionMs: Long,
    val shuffle: Boolean,
    val repeatMode: Int,
)

private val Context.playbackStore: DataStore<Preferences> by preferencesDataStore(name = "playback")

/**
 * 断点续播（需求 P-7，docs/design.md §7.5）：队列存 track_key（Room），当前索引、进度和模式存 DataStore。
 */
@Singleton
class QueuePersistence @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val db: JeekDatabase,
    private val queueDao: QueueDao,
    private val trackDao: TrackDao,
) {
    private object Keys {
        val INDEX = intPreferencesKey("index")
        val POSITION = longPreferencesKey("position")
        val SHUFFLE = booleanPreferencesKey("shuffle")
        val REPEAT = intPreferencesKey("repeat")
    }

    suspend fun saveQueue(trackKeys: List<String>) = db.withTransaction {
        queueDao.clear()
        queueDao.insert(trackKeys.mapIndexed { i, key -> QueueItemEntity(i, key) })
    }

    suspend fun saveState(index: Int, positionMs: Long, shuffle: Boolean, repeatMode: Int) = context.playbackStore.edit {
        it[Keys.INDEX] = index
        it[Keys.POSITION] = positionMs
        it[Keys.SHUFFLE] = shuffle
        it[Keys.REPEAT] = repeatMode
    }

    /** 恢复队列；找不到的歌曲（已被删除）跳过，当前索引随之调整。整个队列都失效时返回 null。 */
    suspend fun restore(): RestoredQueue? {
        val keys = queueDao.trackKeys()
        if (keys.isEmpty()) return null
        val byKey = keys.chunked(500).flatMap { trackDao.byTrackKeys(it) }.associateBy { it.trackKey }
        val prefs = context.playbackStore.data.first()
        val savedIndex = prefs[Keys.INDEX] ?: 0
        val tracks = mutableListOf<TrackEntity>()
        var index = 0
        var positionMs = prefs[Keys.POSITION] ?: 0L
        keys.forEachIndexed { i, key ->
            val track = byKey[key] ?: return@forEachIndexed
            if (i == savedIndex) index = tracks.size
            tracks += track
        }
        if (tracks.isEmpty()) return null
        if (byKey[keys.getOrNull(savedIndex)] == null) positionMs = 0
        return RestoredQueue(tracks, index.coerceIn(0, tracks.lastIndex), positionMs, prefs[Keys.SHUFFLE] ?: false, prefs[Keys.REPEAT] ?: 0)
    }
}
