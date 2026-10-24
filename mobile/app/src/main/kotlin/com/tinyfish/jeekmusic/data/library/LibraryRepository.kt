package com.tinyfish.jeekmusic.data.library

import com.tinyfish.jeekmusic.data.settings.SongSort
import com.tinyfish.jeekmusic.data.filter.SongSorter
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.storage.StorageManager
import android.provider.DocumentsContract
import androidx.room.withTransaction
import com.tinyfish.jeekmusic.data.db.ArtistDao
import com.tinyfish.jeekmusic.data.db.ArtistEntity
import com.tinyfish.jeekmusic.data.db.FolderDao
import com.tinyfish.jeekmusic.data.db.FolderWithCount
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.scanner.LibraryScanner
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource
import com.tinyfish.jeekmusic.data.scanner.SafPaths
import com.tinyfish.jeekmusic.data.scanner.VolumePath
import com.tinyfish.jeekmusic.util.NaturalOrderComparator
import dagger.hilt.android.qualifiers.ApplicationContext
import com.tinyfish.jeekmusic.data.filter.TagFilterRepository
import com.tinyfish.jeekmusic.data.filter.filterByTags
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.flowOn
import javax.inject.Inject
import javax.inject.Singleton

sealed interface AddFolderResult {
    data class Added(val folder: LibraryFolderEntity) : AddFolderResult
    /** 已经被某个音乐文件夹包含。 */
    data class AlreadyCovered(val by: LibraryFolderEntity) : AddFolderResult
    /** 新文件夹包含了已有的这些文件夹，需要用户确认是否替换（需求 L-3）。 */
    data class WouldReplace(val children: List<LibraryFolderEntity>) : AddFolderResult
    /** 不是本地存储上的文件夹（比如网盘），无法映射到 MediaStore。 */
    data object Unsupported : AddFolderResult
}

data class DirectoryContent(val folders: List<FolderNode>, val tracks: List<TrackEntity>)

@OptIn(ExperimentalCoroutinesApi::class)
@Singleton
class LibraryRepository @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val db: JeekDatabase,
    private val folderDao: FolderDao,
    private val trackDao: TrackDao,
    private val artistDao: ArtistDao,
    private val scanner: LibraryScanner,
    private val tagFilter: TagFilterRepository,
    private val sorter: SongSorter,
) {
    /** 根级音乐文件夹；歌曲数只算符合筛选的（需求 M-6），文件夹本身总是显示。 */
    val folders: Flow<List<FolderWithCount>> = tagFilter.filter.flatMapLatest { folderDao.observeWithCounts(it.mood?.name, it.voice?.name, it.genre?.name) }

    /** 艺术家列表；筛选时只保留有这类歌的艺术家，歌曲数也只算这类歌（需求 M-6）。 */
    val artists: Flow<List<ArtistEntity>> = tagFilter.filter.flatMapLatest { filter ->
        if (!filter.isActive) return@flatMapLatest artistDao.observeAll()
        combine(artistDao.observeAll(), artistDao.observeCounts(filter.mood?.name, filter.voice?.name, filter.genre?.name)) { artists, counts ->
            val byId = counts.associate { it.artistId to it.count }
            artists.mapNotNull { artist -> byId[artist.id]?.let { artist.copy(trackCount = it) } }
        }
    }

    val trackCount: Flow<Int> = trackDao.observeCount()

    suspend fun addFolder(treeUri: Uri, replaceChildren: Boolean = false): AddFolderResult {
        val documentId = DocumentsContract.getTreeDocumentId(treeUri)
        val path = SafPaths.fromTreeDocumentId(documentId) ?: return AddFolderResult.Unsupported
        val existing = folderDao.all().filter { it.volumeName == path.volumeName }

        existing.firstOrNull { path.relativePath.startsWith(it.relativePath) }?.let {
            return AddFolderResult.AlreadyCovered(it)
        }
        val children = existing.filter { it.relativePath.startsWith(path.relativePath) }
        if (children.isNotEmpty() && !replaceChildren) return AddFolderResult.WouldReplace(children)

        context.contentResolver.takePersistableUriPermission(
            treeUri,
            Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
        )
        val folder = LibraryFolderEntity(
            treeUri = treeUri.toString(),
            treeDocumentId = documentId,
            volumeName = path.volumeName,
            relativePath = path.relativePath,
            displayName = displayName(path),
            addedAt = System.currentTimeMillis(),
        )
        val id = db.withTransaction {
            if (children.isNotEmpty()) folderDao.delete(children.map { it.id })
            folderDao.insert(folder)
        }
        children.forEach { releasePermission(it) }
        scanner.requestScan()
        return AddFolderResult.Added(folder.copy(id = id))
    }

    suspend fun removeFolder(folder: LibraryFolderEntity) {
        db.withTransaction {
            folderDao.delete(listOf(folder.id))
            artistDao.deleteOrphans()
            artistDao.updateTrackCounts()
        }
        releasePermission(folder)
    }

    fun directory(folderId: Long, path: String): Flow<DirectoryContent> {
        val prefix = MediaStoreSource.escapeLike(path)
        // 筛选时没有符合歌曲的子文件夹不显示（计数查询里已经过滤）
        return tagFilter.filter.flatMapLatest { filter ->
            combine(
                trackDao.observeDirectoryCounts(folderId, prefix, filter.mood?.name, filter.voice?.name, filter.genre?.name),
                trackDao.observeInDirectory(folderId, path),
                tagFilter.allowedKeys,
            ) { counts, tracks, allowed -> Triple(counts, tracks.filterByTags(allowed) { it.trackKey }, allowed) }
        }.combine(sorter.sort) { (counts, tracks, _), sort ->
            DirectoryContent(
                folders = FolderTree.children(path, counts, sorter::key),
                // 按文件名时 CUE 虚拟歌曲排在同目录普通歌曲的后面，按音轨号排序
                tracks = if (sort == SongSort.FILE_NAME) {
                    tracks.sortedWith(compareBy<TrackEntity> { it.cueTrackNo != null }.then(sorter.byFileName))
                } else {
                    sorter.sorted(tracks, sort) { it }
                },
            )
        }.flowOn(Dispatchers.Default)
    }

    /** 文件夹及其子文件夹中的全部歌曲（"播放全部（含子文件夹）"）；按文件名时先按所在目录、再按文件名。 */
    suspend fun tracksUnder(folderId: Long, path: String): List<TrackEntity> {
        val tracks = trackDao.under(folderId, MediaStoreSource.escapeLike(path)).filterByTags(tagFilter.allowedKeys.first()) { it.trackKey }
        val sort = sorter.sort.first()
        return if (sort == SongSort.FILE_NAME) {
            tracks.sortedWith(compareBy<TrackEntity, String>(NaturalOrderComparator) { sorter.key(it.relativePath) }.then(sorter.byFileName))
        } else {
            sorter.sorted(tracks, sort) { it }
        }
    }

    fun artist(nameKey: String): Flow<ArtistEntity?> = artistDao.observeByNameKey(nameKey)

    /** 艺术家的全部歌曲，不分组，默认修改时间倒序（需求 A-6、U-8）。 */
    fun artistTracks(nameKey: String): Flow<List<TrackEntity>> =
        combine(trackDao.observeByArtist(nameKey), tagFilter.allowedKeys, sorter.sort) { tracks, allowed, sort ->
            sorter.sorted(tracks.filterByTags(allowed) { it.trackKey }, sort) { it }
        }.flowOn(Dispatchers.Default)

    /** 专辑没有显式排序偏好时按碟号、音轨号，CUE 同源分轨按曲序。 */
    fun albumTracks(albumKey: String): Flow<List<TrackEntity>> =
        combine(trackDao.observeByAlbum(albumKey), tagFilter.allowedKeys, sorter.albumSort) { tracks, allowed, sort ->
            sorter.albumSorted(tracks.filterByTags(allowed) { it.trackKey }, sort)
        }.flowOn(Dispatchers.Default)

    suspend fun tracksByIds(ids: List<Long>): List<TrackEntity> {
        val byId = ids.chunked(500).flatMap { trackDao.byIds(it) }.associateBy { it.id }
        return ids.mapNotNull { byId[it] }
    }

    private fun displayName(path: VolumePath): String {
        val last = path.relativePath.trimEnd('/').substringAfterLast('/')
        if (last.isNotEmpty()) return last
        val storage = context.getSystemService(StorageManager::class.java)
        return storage.storageVolumes
            .firstOrNull { (it.mediaStoreVolumeName ?: "") == path.volumeName }
            ?.getDescription(context)
            ?: path.volumeName
    }

    private fun releasePermission(folder: LibraryFolderEntity) {
        runCatching {
            context.contentResolver.releasePersistableUriPermission(
                Uri.parse(folder.treeUri),
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
            )
        }
    }
}
