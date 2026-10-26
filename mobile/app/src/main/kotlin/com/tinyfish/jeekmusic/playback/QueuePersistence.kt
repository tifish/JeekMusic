package com.tinyfish.jeekmusic.playback

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
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
    /** 手动添加的歌在 [tracks] 里的位置（需求 P-15）。 */
    val manual: Set<Int> = emptySet(),
    val context: QueueContext? = null,
)

/** 保存的队列条目：歌曲的 track_key 和是否手动添加的。 */
data class SavedQueueItem(val trackKey: String, val manual: Boolean)

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
        val MANUAL = stringPreferencesKey("manual")
        val SOURCE = stringPreferencesKey("source")
        val SOURCE_TITLE = stringPreferencesKey("source_title")
        val SOURCE_ANCHOR = longPreferencesKey("source_anchor")
        val SOURCE_PENDING = booleanPreferencesKey("source_pending")
    }

    suspend fun saveQueue(items: List<SavedQueueItem>) {
        db.withTransaction {
            queueDao.clear()
            queueDao.insert(items.mapIndexed { i, item -> QueueItemEntity(i, item.trackKey) })
        }
        context.playbackStore.edit { prefs ->
            prefs[Keys.MANUAL] = items.indices.filter { items[it].manual }.joinToString(",")
        }
    }

    /** 队列来自的列表（需求 P-15）；null 表示没有。 */
    suspend fun saveContext(value: QueueContext?) = context.playbackStore.edit { prefs ->
        if (value == null) {
            prefs.remove(Keys.SOURCE)
            prefs.remove(Keys.SOURCE_TITLE)
            prefs.remove(Keys.SOURCE_ANCHOR)
            prefs.remove(Keys.SOURCE_PENDING)
        } else {
            prefs[Keys.SOURCE] = value.source.encode()
            prefs[Keys.SOURCE_TITLE] = value.title
            if (value.anchorTrackId != null) prefs[Keys.SOURCE_ANCHOR] = value.anchorTrackId else prefs.remove(Keys.SOURCE_ANCHOR)
            prefs[Keys.SOURCE_PENDING] = value.pending
        }
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
        val savedManual = prefs[Keys.MANUAL].orEmpty().split(',').mapNotNull { it.toIntOrNull() }.toSet()
        val tracks = mutableListOf<TrackEntity>()
        val manual = mutableSetOf<Int>()
        var index = 0
        var positionMs = prefs[Keys.POSITION] ?: 0L
        keys.forEachIndexed { i, key ->
            val track = byKey[key] ?: return@forEachIndexed
            if (i == savedIndex) index = tracks.size
            if (i in savedManual) manual += tracks.size
            tracks += track
        }
        if (tracks.isEmpty()) return null
        if (byKey[keys.getOrNull(savedIndex)] == null) positionMs = 0
        val source = QueueSource.decode(prefs[Keys.SOURCE])
        return RestoredQueue(
            tracks, index.coerceIn(0, tracks.lastIndex), positionMs, prefs[Keys.SHUFFLE] ?: false, prefs[Keys.REPEAT] ?: 0,
            manual = manual,
            context = source?.let { QueueContext(it, prefs[Keys.SOURCE_TITLE].orEmpty(), prefs[Keys.SOURCE_ANCHOR], prefs[Keys.SOURCE_PENDING] ?: false) },
        )
    }
}
