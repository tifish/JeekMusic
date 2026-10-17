package com.tinyfish.jeekmusic.data.cover

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.core.graphics.scale
import com.kyant.taglib.Picture
import com.kyant.taglib.TagLib
import com.tinyfish.jeekmusic.data.db.FolderDao
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.library.DocumentTree
import com.tinyfish.jeekmusic.data.library.appDirectory
import com.tinyfish.jeekmusic.data.library.childDocumentId
import com.tinyfish.jeekmusic.data.library.delete
import com.tinyfish.jeekmusic.data.library.writeFile
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource
import com.tinyfish.jeekmusic.data.settings.CoverSaveMode
import com.tinyfish.jeekmusic.playback.ArtworkVersions
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 封面的查找、缓存和保存（需求 C-1～C-3，docs/design.md §7.4）。
 *
 * 查找顺序：
 * 1. .jeekmusic/covers/ 里的在线封面（用户为这首歌选的，所以最优先）
 * 2. 歌曲旁边的同名图片（歌名.jpg）
 * 3. 内嵌封面
 * 4. 同目录的 cover.jpg / folder.jpg 等
 *
 * 结果缩放后按专辑缓存在 cacheDir/artwork（没有专辑时按歌曲），"没有封面"也会缓存。
 */
@Singleton
class CoverStore @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val trackDao: TrackDao,
    private val folderDao: FolderDao,
    private val tree: DocumentTree,
) {
    private val cacheDir by lazy { File(context.cacheDir, "artwork").apply { mkdirs() } }
    private val locks = ConcurrentHashMap<String, Mutex>()

    /** 目录的子文件列表；只有一个大目录时每次都列一遍很慢。本 App 写入封面后使对应目录失效。 */
    private val listings = LruCache<String, List<DocumentTree.Child>>(32)

    /** 缩放好的封面缓存文件；没有封面时返回 null。 */
    suspend fun cachedFile(track: TrackEntity, large: Boolean): File? = withContext(Dispatchers.IO) {
        val size = if (large) LARGE_PX else SMALL_PX
        val key = cacheKey(track)
        val file = File(cacheDir, "${key}_$size.jpg")
        if (!file.exists()) {
            locks.getOrPut(key) { Mutex() }.withLock {
                val none = File(cacheDir, "$key.none")
                if (!file.exists() && !none.exists()) {
                    val bytes = resolve(track)
                    if (bytes == null || !writeScaled(bytes, size, file)) none.createNewFile()
                }
            }
        }
        file.takeIf { it.exists() }
    }

    suspend fun hasCover(track: TrackEntity): Boolean = cachedFile(track, large = false) != null

    private suspend fun resolve(track: TrackEntity): ByteArray? {
        val folder = folderDao.byId(track.folderId)
        return folder?.let { hiddenCover(it, track) }
            ?: folder?.let { sidecar(it, track) }
            ?: embeddedCover(track)
            ?: folder?.let { folderImage(it, track) }
    }

    private fun hiddenCover(folder: LibraryFolderEntity, track: TrackEntity): ByteArray? {
        val children = listing(folder, coversPath(folder)).takeIf { it.isNotEmpty() } ?: return null
        return hiddenNames(track).firstNotNullOfOrNull { name -> children.firstOrNull { it.name.equals(name, ignoreCase = true) } }
            ?.let { tree.read(folder, it.documentId) }
    }

    private fun sidecar(folder: LibraryFolderEntity, track: TrackEntity): ByteArray? {
        val base = track.fileName.substringBeforeLast('.')
        val children = listing(folder, track.relativePath)
        return IMAGE_EXTENSIONS.firstNotNullOfOrNull { ext -> children.firstOrNull { !it.isDirectory && it.name.equals("$base.$ext", ignoreCase = true) } }
            ?.let { tree.read(folder, it.documentId) }
    }

    private fun embeddedCover(track: TrackEntity): ByteArray? = runCatching {
        val uri = MediaStoreSource.contentUri(track.volumeName, track.mediaStoreId)
        context.contentResolver.openFileDescriptor(uri, "r")?.use { pfd ->
            // TagLib 的 FileStream 会关闭传入的 fd，所以传 dup 出来的副本
            TagLib.getFrontCover(pfd.dup().detachFd())?.data
        }
    }.getOrNull()

    private fun folderImage(folder: LibraryFolderEntity, track: TrackEntity): ByteArray? {
        val children = listing(folder, track.relativePath).filter { !it.isDirectory }
        val best = COVER_NAMES.firstNotNullOfOrNull { name -> children.firstOrNull { it.name.equals(name, ignoreCase = true) } } ?: return null
        return tree.read(folder, best.documentId)
    }

    private fun listing(folder: LibraryFolderEntity, relativePath: String): List<DocumentTree.Child> {
        val key = "${folder.id}:$relativePath"
        return listings.get(key) ?: tree.list(folder, relativePath).also { listings.put(key, it) }
    }

    /**
     * 保存在线封面（需求 C-3）。图片统一转成 JPEG；写进文件失败时改存到隐藏目录。
     * 保存后立即用新封面替换缓存，并通知界面重新加载同一专辑的所有封面。
     */
    suspend fun save(track: TrackEntity, image: ByteArray, mode: CoverSaveMode): CoverSaveMode? = withContext(Dispatchers.IO) {
        val jpeg = toJpeg(image) ?: return@withContext null
        val folder = folderDao.byId(track.folderId) ?: return@withContext null
        val saved = when (mode) {
            CoverSaveMode.EMBED -> CoverSaveMode.EMBED.takeIf { embed(folder, track, jpeg) }
            CoverSaveMode.SIDECAR -> CoverSaveMode.SIDECAR.takeIf { writeSidecar(folder, track, jpeg) }
            CoverSaveMode.HIDDEN_FOLDER -> null
        } ?: CoverSaveMode.HIDDEN_FOLDER.takeIf { writeHidden(folder, track, jpeg) }
        // 隐藏目录里的封面优先级最高：存到别处时删掉这首歌原来的，否则新封面不会显示
        if (saved != null && saved != CoverSaveMode.HIDDEN_FOLDER) deleteHidden(folder, track)
        if (saved != null) replaceCache(track, jpeg)
        saved
    }

    private fun embed(folder: LibraryFolderEntity, track: TrackEntity, jpeg: ByteArray): Boolean = runCatching {
        val fileId = childDocumentId(DocumentTree.directoryDocumentId(folder, track.relativePath), track.fileName)
        tree.resolver.openFileDescriptor(tree.documentUri(folder, fileId), "rw")?.use { pfd ->
            val others = TagLib.getPictures(pfd.dup().detachFd()).filter { it.pictureType != FRONT_COVER }
            val pictures = listOf(Picture(jpeg, "", FRONT_COVER, "image/jpeg")) + others
            TagLib.savePictures(pfd.dup().detachFd(), pictures.toTypedArray())
        } ?: false
    }.getOrDefault(false)

    private fun writeSidecar(folder: LibraryFolderEntity, track: TrackEntity, jpeg: ByteArray): Boolean {
        val dirId = DocumentTree.directoryDocumentId(folder, track.relativePath)
        val name = track.fileName.substringBeforeLast('.') + ".jpg"
        return tree.writeFile(folder, dirId, name, jpeg).also { listings.remove("${folder.id}:${track.relativePath}") }
    }

    private fun writeHidden(folder: LibraryFolderEntity, track: TrackEntity, jpeg: ByteArray): Boolean {
        val dirId = tree.appDirectory(folder, COVERS_DIR) ?: return false
        return tree.writeFile(folder, dirId, hiddenNames(track).first(), jpeg).also { listings.remove("${folder.id}:${coversPath(folder)}") }
    }

    private fun deleteHidden(folder: LibraryFolderEntity, track: TrackEntity) {
        val children = listing(folder, coversPath(folder))
        val names = hiddenNames(track)
        children.filter { c -> names.any { it.equals(c.name, ignoreCase = true) } }.forEach { tree.delete(folder, it.documentId) }
        listings.remove("${folder.id}:${coversPath(folder)}")
    }

    private suspend fun replaceCache(track: TrackEntity, jpeg: ByteArray) {
        val key = cacheKey(track)
        locks.getOrPut(key) { Mutex() }.withLock {
            File(cacheDir, "$key.none").delete()
            for (size in listOf(SMALL_PX, LARGE_PX)) {
                val file = File(cacheDir, "${key}_$size.jpg")
                file.delete()
                writeScaled(jpeg, size, file)
            }
        }
        val ids = track.albumKey?.let { trackDao.idsByAlbum(it) } ?: listOf(track.id)
        ArtworkVersions.bump(ids + track.id)
    }

    private fun writeScaled(bytes: ByteArray, size: Int, target: File): Boolean {
        val bitmap = decodeScaled(bytes, size) ?: return false
        val tmp = File(target.parentFile, target.name + ".tmp")
        tmp.outputStream().use { bitmap.compress(Bitmap.CompressFormat.JPEG, 90, it) }
        return tmp.renameTo(target)
    }

    companion object {
        private const val SMALL_PX = 144
        private const val LARGE_PX = 720
        private const val MAX_SAVED_PX = 1200
        private const val FRONT_COVER = "Front Cover"
        const val COVERS_DIR = "covers"

        private val IMAGE_EXTENSIONS = listOf("jpg", "jpeg", "png", "webp")
        private val COVER_NAMES = listOf("cover", "folder", "front", "albumart", "album").flatMap { base -> IMAGE_EXTENSIONS.map { "$base.$it" } }
        private val ILLEGAL = Regex("""[\\/:*?"<>|\x00-\x1f]""")

        private fun coversPath(folder: LibraryFolderEntity) = DocumentTree.appPath(folder, COVERS_DIR)

        /**
         * 隐藏目录里的文件名：有专辑时是"专辑艺术家（或第一位艺术家） - 专辑.jpg"，同一专辑共用；
         * 没有专辑时是"艺术家 - 歌名.jpg"。第一个是保存时用的名字。
         */
        fun hiddenNames(track: TrackEntity): List<String> {
            val artist = track.albumArtist?.takeIf { it.isNotBlank() } ?: CoverMatcher.primaryArtist(track.artistDisplay)
            val album = track.album?.takeIf { it.isNotBlank() }?.let { fileName(artist, it) }
            return listOfNotNull(album, fileName(track.artistDisplay, track.titleTag ?: track.title))
        }

        private fun fileName(vararg parts: String): String =
            parts.map { it.trim() }.filter { it.isNotEmpty() }.joinToString(" - ")
                .replace(ILLEGAL, "_").trim('.', ' ').take(120).ifEmpty { "_" } + ".jpg"

        private fun cacheKey(track: TrackEntity): String = sha1(track.albumKey ?: track.trackKey)

        private fun sha1(text: String): String =
            MessageDigest.getInstance("SHA-1").digest(text.toByteArray()).joinToString("") { "%02x".format(it) }

        private fun decodeScaled(bytes: ByteArray, size: Int): Bitmap? {
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
            var sample = 1
            while (bounds.outWidth / (sample * 2) >= size && bounds.outHeight / (sample * 2) >= size) sample *= 2
            val decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample })
                ?: return null
            val scale = size.toFloat() / maxOf(decoded.width, decoded.height)
            return if (scale < 1f) {
                decoded.scale((decoded.width * scale).toInt().coerceAtLeast(1), (decoded.height * scale).toInt().coerceAtLeast(1))
            } else {
                decoded
            }
        }

        /** 不大于 [MAX_SAVED_PX] 的 JPEG 原样保存；其他格式或过大的图片重新编码。不是图片时返回 null。 */
        fun toJpeg(bytes: ByteArray): ByteArray? {
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
            if (bounds.outMimeType == "image/jpeg" && maxOf(bounds.outWidth, bounds.outHeight) <= MAX_SAVED_PX) return bytes
            val bitmap = decodeScaled(bytes, MAX_SAVED_PX) ?: return null
            return ByteArrayOutputStream().use { out ->
                bitmap.compress(Bitmap.CompressFormat.JPEG, 92, out)
                out.toByteArray()
            }
        }
    }
}
