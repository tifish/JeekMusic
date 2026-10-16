package com.tinyfish.jeekmusic.data.playlist

import androidx.room.withTransaction
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.PlaylistDao
import com.tinyfish.jeekmusic.data.db.PlaylistEntity
import com.tinyfish.jeekmusic.data.db.PlaylistEntry
import com.tinyfish.jeekmusic.data.db.PlaylistItemEntity
import com.tinyfish.jeekmusic.data.db.PlaylistWithCount
import com.tinyfish.jeekmusic.data.db.TrackEntity
import kotlinx.coroutines.flow.Flow
import javax.inject.Inject
import javax.inject.Singleton

/** 播放列表（需求 PL-1 ~ PL-3）。条目引用 track_key，文件不可用时保留条目。 */
@Singleton
class PlaylistRepository @Inject constructor(
    private val db: JeekDatabase,
    private val dao: PlaylistDao,
) {
    val playlists: Flow<List<PlaylistWithCount>> = dao.observeAll()

    fun playlist(id: Long): Flow<PlaylistEntity?> = dao.observe(id)

    fun entries(id: Long): Flow<List<PlaylistEntry>> = dao.observeEntries(id)

    suspend fun all(): List<PlaylistEntity> = dao.all()

    suspend fun create(name: String): Long {
        val now = System.currentTimeMillis()
        return dao.insert(PlaylistEntity(name = name.trim(), createdAt = now, updatedAt = now))
    }

    suspend fun rename(id: Long, name: String) = dao.rename(id, name.trim(), System.currentTimeMillis())

    suspend fun delete(id: Long) = dao.delete(id)

    suspend fun add(id: Long, tracks: List<TrackEntity>) = addKeys(id, tracks.map { it.trackKey })

    suspend fun addKeys(id: Long, trackKeys: List<String>) = db.withTransaction {
        val start = dao.maxPosition(id) + 1
        dao.insertItems(trackKeys.mapIndexed { i, key -> PlaylistItemEntity(playlistId = id, position = start + i, trackKey = key) })
        dao.touch(id, System.currentTimeMillis())
    }

    suspend fun removeItem(id: Long, itemId: Long) = db.withTransaction {
        dao.deleteItem(itemId)
        dao.touch(id, System.currentTimeMillis())
    }

    /** 按新的顺序重写整个列表（拖动排序后调用）。 */
    suspend fun reorder(id: Long, itemIdsInOrder: List<Long>) = db.withTransaction {
        val items = dao.items(id).associateBy { it.id }
        val ordered = itemIdsInOrder.mapNotNull { items[it] }
        dao.clearItems(id)
        dao.insertItems(ordered.mapIndexed { i, item -> PlaylistItemEntity(playlistId = id, position = i, trackKey = item.trackKey) })
        dao.touch(id, System.currentTimeMillis())
    }
}
