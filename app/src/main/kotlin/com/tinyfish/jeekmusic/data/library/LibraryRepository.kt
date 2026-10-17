package com.tinyfish.jeekmusic.data.library

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
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.map
import com.tinyfish.jeekmusic.data.scanner.SortKeys
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

@Singleton
class LibraryRepository @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val db: JeekDatabase,
    private val folderDao: FolderDao,
    private val trackDao: TrackDao,
    private val artistDao: ArtistDao,
    private val scanner: LibraryScanner,
) {
    val folders: Flow<List<FolderWithCount>> = folderDao.observeWithCounts()

    val artists: Flow<List<ArtistEntity>> = artistDao.observeAll()

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
        return combine(
            trackDao.observeDirectoryCounts(folderId, prefix),
            trackDao.observeInDirectory(folderId, path),
        ) { counts, tracks ->
            DirectoryContent(
                folders = FolderTree.children(path, counts),
                // CUE 虚拟歌曲排在同目录普通歌曲的后面，按音轨号排序
                tracks = tracks.sortedWith(
                    compareBy<TrackEntity> { it.cueTrackNo != null }
                        .thenBy(NaturalOrderComparator) { it.fileName }
                        .thenBy { it.cueTrackNo ?: 0 },
                ),
            )
        }
    }

    /** 文件夹及其子文件夹中的全部歌曲，按路径和文件名自然排序（"播放全部（含子文件夹）"）。 */
    suspend fun tracksUnder(folderId: Long, path: String): List<TrackEntity> =
        trackDao.under(folderId, MediaStoreSource.escapeLike(path))
            .sortedWith(
                compareBy<TrackEntity, String>(NaturalOrderComparator) { it.relativePath }
                    .thenBy(NaturalOrderComparator) { it.fileName }
                    .thenBy { it.cueTrackNo ?: 0 },
            )

    fun artist(nameKey: String): Flow<ArtistEntity?> = artistDao.observeByNameKey(nameKey)

    private val sortKeys by lazy { SortKeys.android() }

    /** 艺术家的全部歌曲，不分组，按标题排序（中文按拼音），同名的再按文件名排序（需求 A-6）。 */
    fun artistTracks(nameKey: String): Flow<List<TrackEntity>> = trackDao.observeByArtist(nameKey).map { tracks ->
        tracks.map { it to sortKeys.sortKey(it.title) }
            .sortedWith(
                compareBy<Pair<TrackEntity, String>, String>(NaturalOrderComparator) { it.second }
                    .thenBy(NaturalOrderComparator) { it.first.fileName }
                    .thenBy { it.first.cueTrackNo ?: 0 },
            )
            .map { it.first }
    }.flowOn(Dispatchers.Default)

    fun albumTracks(albumKey: String): Flow<List<TrackEntity>> = trackDao.observeByAlbum(albumKey)

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
