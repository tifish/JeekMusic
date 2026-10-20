package com.tinyfish.jeekmusic.data.library

import android.content.ContentUris
import android.content.Context
import android.content.IntentSender
import android.net.Uri
import android.os.Environment
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.util.Log
import com.tinyfish.jeekmusic.data.db.FileMoveDao
import com.tinyfish.jeekmusic.data.db.FolderDao
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.cover.CoverStore
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import com.tinyfish.jeekmusic.data.scanner.LibraryScanner
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 歌曲的文件操作（需求 F-10，docs/design.md §4.7）：删除（移到系统回收站）、改名、移动。
 * 歌曲旁边的同名 .lrc、图片跟着一起处理；CUE 整轨里的歌共用一个文件，不能单独操作，跳过。
 *
 * 改名、移动后歌曲的稳定键变了：立即把曲库和所有按键引用的记录换成新键（[FileMoveDao]），再触发扫描读新的文件。
 * 改名和移动都是文件系统里的重命名，修改时间不变，不需要重新分析。
 */
@Singleton
class FileOperations @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val folderDao: FolderDao,
    private val tree: DocumentTree,
    private val moveDao: FileMoveDao,
    private val trackDao: TrackDao,
    private val scanner: LibraryScanner,
    private val covers: CoverStore,
    private val lyrics: LyricsRepository,
) {
    enum class RenameResult { OK, INVALID, EXISTS, FAILED }

    /** 移动的结果：[moved] 成功，[exists] 目标位置已有同名文件，[failed] 其他原因失败。 */
    data class MoveResult(val moved: Int, val exists: Int, val failed: Int)

    /**
     * 移到系统回收站的请求（需求 F-10）：系统会弹出确认，用户确认后文件进入回收站，30 天内可以恢复。
     * 有"所有文件访问权限"时，同名 .lrc、图片一起放进去（没有权限时 App 看不到它们在 MediaStore 里的记录，留着不动）。
     * 都是 CUE 整轨里的歌时返回 null。
     */
    suspend fun trashRequest(tracks: List<TrackEntity>): IntentSender? = withContext(Dispatchers.IO) {
        val files = fresh(tracks).filter { it.cueTrackNo == null }.distinctBy { it.fileKey }
        if (files.isEmpty()) return@withContext null
        val audio = files.map { MediaStoreSource.contentUri(it.volumeName, it.mediaStoreId) }
        val sidecars = if (Environment.isExternalStorageManager()) sidecarUris(files) else emptyList()
        runCatching { MediaStore.createTrashRequest(context.contentResolver, audio + sidecars, true).intentSender }
            // MediaStore 不接受某些非媒体文件时只放音乐文件
            .recoverCatching { MediaStore.createTrashRequest(context.contentResolver, audio, true).intentSender }
            .onFailure { Log.w(TAG, "trash request failed", it) }
            .getOrNull()
    }

    /** 用户在系统对话框里确认后：重新扫描，曲库里去掉这些歌。 */
    fun afterTrashed() = scanner.requestScan()

    /** 改名（只改文件名，不改扩展名）；同名 .lrc、图片和隐藏目录里的歌词跟着改。 */
    suspend fun rename(selected: TrackEntity, newBaseName: String): RenameResult = withContext(Dispatchers.IO) {
        val track = fresh(listOf(selected)).firstOrNull() ?: return@withContext RenameResult.FAILED
        val base = newBaseName.trim()
        if (track.cueTrackNo != null || base.isEmpty() || base.length > MAX_NAME || ILLEGAL.containsMatchIn(base) || base.startsWith(".")) {
            return@withContext RenameResult.INVALID
        }
        val folder = folderDao.byId(track.folderId) ?: return@withContext RenameResult.FAILED
        val oldBase = track.fileName.substringBeforeLast('.')
        val ext = track.fileName.substringAfterLast('.', "")
        val newName = if (ext.isEmpty()) base else "$base.$ext"
        if (newName == track.fileName) return@withContext RenameResult.OK
        val dirId = DocumentTree.directoryDocumentId(folder, track.relativePath)
        val children = tree.listById(folder, dirId)
        // 只是大小写不同时也算同一个文件
        if (children.any { it.name.equals(newName, ignoreCase = true) && !it.name.equals(track.fileName, ignoreCase = true) }) {
            return@withContext RenameResult.EXISTS
        }
        if (!tree.rename(folder, childDocumentId(dirId, track.fileName), newName)) return@withContext RenameResult.FAILED
        for (side in sidecars(children, oldBase)) {
            val target = base + side.name.substring(oldBase.length)
            if (children.none { it.name.equals(target, ignoreCase = true) }) tree.rename(folder, side.documentId, target)
        }
        renameHiddenLyrics(folder, oldBase, base)
        moveDao.move(track.trackKey, keyOf(track.volumeName, track.relativePath, newName), folder.id, track.relativePath, newName)
        filesChanged()
        RenameResult.OK
    }

    /**
     * 把歌曲移到音乐文件夹 [target] 里的 [relativePath]（MediaStore 的相对路径，以 "/" 结尾）。
     * 同名 .lrc、图片跟着移动；移到另一个音乐文件夹时，隐藏目录里的歌词也搬过去，专辑封面复制一份过去（同专辑的其他歌可能还在原处）。
     */
    suspend fun move(tracks: List<TrackEntity>, target: LibraryFolderEntity, relativePath: String): MoveResult = withContext(Dispatchers.IO) {
        val targetDir = DocumentTree.directoryDocumentId(target, relativePath)
        val targetChildren = tree.listById(target, targetDir).toMutableList()
        val listings = HashMap<String, List<DocumentTree.Child>>()
        var moved = 0
        var exists = 0
        var failed = 0
        for (track in fresh(tracks).filter { it.cueTrackNo == null }.distinctBy { it.fileKey }) {
            if (track.volumeName == target.volumeName && track.relativePath == relativePath) continue
            val source = folderDao.byId(track.folderId)
            if (source == null) {
                failed++
                continue
            }
            if (targetChildren.any { it.name.equals(track.fileName, ignoreCase = true) }) {
                exists++
                continue
            }
            val sourceDir = DocumentTree.directoryDocumentId(source, track.relativePath)
            if (!moveDocument(source, childDocumentId(sourceDir, track.fileName), sourceDir, target, targetDir)) {
                failed++
                continue
            }
            targetChildren += DocumentTree.Child(childDocumentId(targetDir, track.fileName), track.fileName, false)
            val children = listings.getOrPut("${source.id}:$sourceDir") { tree.listById(source, sourceDir) }
            for (side in sidecars(children, track.fileName.substringBeforeLast('.'))) {
                if (targetChildren.none { it.name.equals(side.name, ignoreCase = true) } && moveDocument(source, side.documentId, sourceDir, target, targetDir)) {
                    targetChildren += DocumentTree.Child(childDocumentId(targetDir, side.name), side.name, false)
                }
            }
            if (source.id != target.id) moveAppFiles(source, target, track)
            moveDao.move(track.trackKey, keyOf(target.volumeName, relativePath, track.fileName), target.id, relativePath, track.fileName)
            moved++
        }
        if (moved > 0) filesChanged()
        MoveResult(moved, exists, failed)
    }

    /** 新建子文件夹，返回它的 MediaStore 相对路径；已存在时直接返回。 */
    suspend fun createDirectory(folder: LibraryFolderEntity, parentRelativePath: String, name: String): String? = withContext(Dispatchers.IO) {
        val clean = name.trim()
        if (clean.isEmpty() || ILLEGAL.containsMatchIn(clean) || clean.startsWith(".")) return@withContext null
        val parentId = DocumentTree.directoryDocumentId(folder, parentRelativePath)
        val dir = tree.findChild(folder, parentId, clean)?.takeIf { it.isDirectory }
            ?: tree.create(folder, parentId, DocumentsContract.Document.MIME_TYPE_DIR, clean)
            ?: return@withContext null
        "$parentRelativePath${dir.name}/"
    }

    /** 子文件夹（不含隐藏的，比如 .jeekmusic），按名称排序。 */
    suspend fun subdirectories(folder: LibraryFolderEntity, relativePath: String): List<String> = withContext(Dispatchers.IO) {
        tree.list(folder, relativePath).filter { it.isDirectory && !it.name.startsWith(".") }.map { it.name }.sortedWith(String.CASE_INSENSITIVE_ORDER)
    }

    /** 界面上的歌曲可能是改名、移动之前读的（比如搜索结果），按 id 重新读取曲库里现在的位置。 */
    private suspend fun fresh(tracks: List<TrackEntity>): List<TrackEntity> =
        tracks.map { it.id }.distinct().chunked(SQL_CHUNK).flatMap { trackDao.byIds(it) }

    /** 目录列表的缓存过时了，找同名歌词、封面时会用错；再扫描读新的文件。 */
    private fun filesChanged() {
        covers.invalidateListings()
        lyrics.invalidateAllDirectories()
        scanner.requestScan()
    }

    private fun moveDocument(source: LibraryFolderEntity, documentId: String, sourceDir: String, target: LibraryFolderEntity, targetDir: String): Boolean =
        runCatching {
            DocumentsContract.moveDocument(
                context.contentResolver,
                tree.documentUri(source, documentId),
                tree.documentUri(source, sourceDir),
                tree.documentUri(target, targetDir),
            ) != null
        }.onFailure { Log.w(TAG, "move failed: $documentId", it) }.getOrDefault(false)

    /** 歌曲旁边以 "<文件名>." 开头的歌词、图片。 */
    private fun sidecars(children: List<DocumentTree.Child>, base: String) = children.filter { c ->
        !c.isDirectory && c.name.length > base.length + 1 && c.name.startsWith("$base.", ignoreCase = true) &&
            c.name.substringAfterLast('.').lowercase() in SIDECAR_EXTENSIONS
    }

    private fun renameHiddenLyrics(folder: LibraryFolderEntity, oldBase: String, newBase: String) {
        val dirId = DocumentTree.directoryDocumentId(folder, DocumentTree.appPath(folder, LYRICS_DIR))
        val children = tree.listById(folder, dirId)
        val old = children.firstOrNull { it.name.equals("$oldBase.lrc", ignoreCase = true) } ?: return
        if (children.none { it.name.equals("$newBase.lrc", ignoreCase = true) }) tree.rename(folder, old.documentId, "$newBase.lrc")
    }

    /** 移到另一个音乐文件夹：隐藏目录里的歌词搬过去，专辑封面复制过去（目标那边没有时）。 */
    private fun moveAppFiles(source: LibraryFolderEntity, target: LibraryFolderEntity, track: TrackEntity) {
        val lrc = track.fileName.substringBeforeLast('.') + ".lrc"
        val lyricsDir = DocumentTree.directoryDocumentId(source, DocumentTree.appPath(source, LYRICS_DIR))
        tree.listById(source, lyricsDir).firstOrNull { it.name.equals(lrc, ignoreCase = true) }?.let { file ->
            val bytes = tree.read(source, file.documentId)
            val dir = tree.appDirectory(target, LYRICS_DIR)
            if (bytes != null && dir != null && tree.writeFile(target, dir, file.name, bytes)) tree.delete(source, file.documentId)
        }
        val coversDir = DocumentTree.directoryDocumentId(source, DocumentTree.appPath(source, CoverStore.COVERS_DIR))
        val names = CoverStore.hiddenNames(track)
        tree.listById(source, coversDir).firstOrNull { c -> names.any { it.equals(c.name, ignoreCase = true) } }?.let { file ->
            val dir = tree.appDirectory(target, CoverStore.COVERS_DIR) ?: return@let
            if (tree.findChild(target, dir, file.name) == null) tree.read(source, file.documentId)?.let { tree.writeFile(target, dir, file.name, it) }
        }
    }

    /** 同名 .lrc、图片在 MediaStore 里的记录（需要"所有文件访问权限"才看得到）。 */
    private fun sidecarUris(tracks: List<TrackEntity>): List<Uri> = tracks.groupBy { it.volumeName to it.relativePath }.flatMap { (dir, inDir) ->
        val (volume, relativePath) = dir
        val collection = MediaStore.Files.getContentUri(volume)
        val bases = inDir.map { it.fileName.substringBeforeLast('.').lowercase() }.toSet()
        runCatching {
            context.contentResolver.query(
                collection,
                arrayOf(MediaStore.MediaColumns._ID, MediaStore.MediaColumns.DISPLAY_NAME),
                "${MediaStore.MediaColumns.RELATIVE_PATH} = ?",
                arrayOf(relativePath),
                null,
            )?.use { c ->
                buildList {
                    while (c.moveToNext()) {
                        val name = c.getString(1) ?: continue
                        val ext = name.substringAfterLast('.', "").lowercase()
                        if (ext in SIDECAR_EXTENSIONS && name.substringBeforeLast('.').lowercase() in bases) add(ContentUris.withAppendedId(collection, c.getLong(0)))
                    }
                }
            }.orEmpty()
        }.getOrDefault(emptyList())
    }

    private companion object {
        const val TAG = "FileOperations"
        const val LYRICS_DIR = "lyrics"
        const val MAX_NAME = 200
        const val SQL_CHUNK = 900
        val ILLEGAL = Regex("""[\\/:*?"<>|\x00-\x1f]""")
        val SIDECAR_EXTENSIONS = setOf("lrc", "jpg", "jpeg", "png", "webp")

        fun keyOf(volumeName: String, relativePath: String, fileName: String) = "$volumeName:$relativePath$fileName"
    }
}
