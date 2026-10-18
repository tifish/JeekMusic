package com.tinyfish.jeekmusic.data.library

import com.tinyfish.jeekmusic.data.db.PlaylistEntry
import com.tinyfish.jeekmusic.data.db.SearchDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.filter.SongSorter
import com.tinyfish.jeekmusic.data.filter.TagFilterRepository
import com.tinyfish.jeekmusic.data.filter.filterByTags
import com.tinyfish.jeekmusic.data.playlist.PlaylistRepository
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource
import com.tinyfish.jeekmusic.data.settings.SongSort
import com.tinyfish.jeekmusic.playback.QueueSource
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.mapLatest
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 各列表里的歌（按当前筛选和排序），和列表页显示的一致。列表页和播放服务都用这里：
 * 播放服务据此在筛选、排序或曲库变化后重新生成队列（需求 P-15）。
 */
@OptIn(ExperimentalCoroutinesApi::class)
@Singleton
class QueueSourceRepository @Inject constructor(
    private val library: LibraryRepository,
    private val playlists: PlaylistRepository,
    private val searchDao: SearchDao,
    private val tagFilters: TagFilterRepository,
    private val sorter: SongSorter,
) {
    fun tracks(source: QueueSource): Flow<List<TrackEntity>> = when (source) {
        is QueueSource.Folder -> if (source.recursive) {
            combine(searchDao.trackChanges(), tagFilters.allowedKeys, sorter.sort) { _, _, _ -> }
                .mapLatest { library.tracksUnder(source.folderId, source.relativePath) }
        } else {
            library.directory(source.folderId, source.relativePath).map { it.tracks }
        }
        is QueueSource.Artist -> library.artistTracks(source.nameKey)
        is QueueSource.Album -> library.albumTracks(source.albumKey)
        is QueueSource.Playlist -> playlistEntries(source.playlistId).map { entries -> entries.mapNotNull { it.track } }
        is QueueSource.Search -> searchChanges().mapLatest { searchTracks(source.query) }
    }

    /** 播放列表的条目（包括文件不可用的），按筛选和排序；按文件名（默认）时保持手动排的顺序（需求 U-8、PL-3）。 */
    fun playlistEntries(playlistId: Long): Flow<List<PlaylistEntry>> =
        combine(playlists.entries(playlistId), tagFilters.allowedKeys, sorter.sort) { entries, allowed, sort ->
            val filtered = entries.filterByTags(allowed) { it.track?.trackKey }
            if (sort == SongSort.FILE_NAME) filtered else sorter.sorted(filtered, sort) { it.track }
        }.flowOn(Dispatchers.Default)

    /** 曲库、筛选、排序变化时发出，搜索结果据此重新查询。 */
    fun searchChanges(): Flow<Unit> = combine(searchDao.trackChanges(), tagFilters.allowedKeys, sorter.sort) { _, _, _ -> }

    /** 搜索到的歌曲（需求 S-1）：也按智能标签筛选（需求 M-6），按全局排序（需求 U-8）后取前 [MAX_SEARCH_TRACKS] 首。 */
    suspend fun searchTracks(query: String): List<TrackEntity> {
        val pattern = "%" + MediaStoreSource.escapeLike(query) + "%"
        val tracks = searchDao.tracks(pattern).filterByTags(tagFilters.allowedKeys.first()) { it.trackKey }
        // 先排序再截取，否则截到的不是排在最前面的那些
        return sorter.sorted(tracks, sorter.sort.first()) { it }.take(MAX_SEARCH_TRACKS)
    }

    companion object {
        const val MAX_SEARCH_TRACKS = 200
    }
}
