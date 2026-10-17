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
import com.tinyfish.jeekmusic.data.library.AudioFileEditor
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
 * 封面按专辑共用。查找顺序：
 * 1. 这首歌自己的：.jeekmusic/covers/ 里的在线封面（用户选的，所以最优先）→ 歌曲旁边的同名图片 → 内嵌封面
 * 2. 同专辑其他歌曲的（同样的顺序）：专辑里只要有一个文件带封面，整张专辑都用它，也不会再去下载
 * 3. 同目录的 cover.jpg / folder.jpg 等
 *
 * 结果缩放后按专辑缓存在 cacheDir/artwork2（没有专辑时按歌曲），"没有封面"也会缓存。
 */
@Singleton
class CoverStore @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val trackDao: TrackDao,
    private val folderDao: FolderDao,
    private val tree: DocumentTree,
    private val editor: AudioFileEditor,
) {
    // 旧版本只看一首歌就缓存"没有封面"，同专辑其他文件里的封面会被漏掉：换个目录重建缓存
    private val cacheDir by lazy {
        File(context.cacheDir, "artwork").deleteRecursively()
        File(context.cacheDir, "artwork2").apply { mkdirs() }
    }
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
                    val bytes = albumImage(track)
                    if (bytes == null || !writeScaled(bytes, size, file)) none.createNewFile()
                }
            }
        }
        file.takeIf { it.exists() }
    }

    suspend fun hasCover(track: TrackEntity): Boolean = cachedFile(track, large = false) != null

    /** 整张专辑的封面原图（查找顺序见类注释）；都没有时返回 null。 */
    suspend fun albumImage(track: TrackEntity): ByteArray? = withContext(Dispatchers.IO) {
        val folders = HashMap<Long, LibraryFolderEntity?>()
        suspend fun folderOf(t: TrackEntity) = folders.getOrPut(t.folderId) { folderDao.byId(t.folderId) }
        albumOf(track).take(MAX_ALBUM_FILES).firstNotNullOfOrNull { t -> ownImage(folderOf(t), t) }
            ?: folderOf(track)?.let { folderImage(it, track) }
    }

    /**
     * 同专辑的歌曲，[track] 排第一；没有专辑时只有它自己。
     * CUE 整轨的各首共用一个文件，只保留一首。
     */
    suspend fun albumOf(track: TrackEntity): List<TrackEntity> {
        val others = track.albumKey?.let { trackDao.byAlbum(it) }.orEmpty().filter { it.id != track.id }
        return (listOf(track) + others).distinctBy { it.fileKey }
    }

    /**
     * 这首歌自己有没有按 [mode] 保存的封面：写入文件看文件里有没有内嵌封面，同名图片看有没有图片，
     * 隐藏目录则看整张专辑（隐藏目录里同一专辑本来就只存一张）。
     */
    suspend fun hasOwnCover(track: TrackEntity, mode: CoverSaveMode): Boolean = withContext(Dispatchers.IO) {
        when (mode) {
            CoverSaveMode.EMBED -> embeddedCover(track) != null
            CoverSaveMode.SIDECAR -> folderDao.byId(track.folderId)?.let { sidecar(it, track) } != null
            CoverSaveMode.HIDDEN_FOLDER -> hasCover(track)
        }
    }

    /** 能不能写入音乐文件（需要"所有文件访问权限"，见 [AudioFileEditor]）。 */
    fun canEmbed(): Boolean = editor.canEdit()

    /** 实际会用的保存方式：选了写入文件但没有权限时，保存总会改存到隐藏目录。 */
    fun effectiveMode(mode: CoverSaveMode): CoverSaveMode =
        if (mode == CoverSaveMode.EMBED && !canEmbed()) CoverSaveMode.HIDDEN_FOLDER else mode

    private fun ownImage(folder: LibraryFolderEntity?, track: TrackEntity): ByteArray? =
        folder?.let { hiddenCover(it, track) } ?: folder?.let { sidecar(it, track) } ?: embeddedCover(track)

    private fun hiddenCover(folder: LibraryFolderEntity, track: TrackEntity): ByteArray? =
        hiddenFile(folder, track)?.let { tree.read(folder, it.documentId) }

    private fun sidecar(folder: LibraryFolderEntity, track: TrackEntity): ByteArray? =
        sidecarFile(folder, track)?.let { tree.read(folder, it.documentId) }

    private fun hiddenFile(folder: LibraryFolderEntity, track: TrackEntity): DocumentTree.Child? {
        val children = listing(folder, coversPath(folder)).takeIf { it.isNotEmpty() } ?: return null
        return hiddenNames(track).firstNotNullOfOrNull { name -> children.firstOrNull { it.name.equals(name, ignoreCase = true) } }
    }

    private fun sidecarFile(folder: LibraryFolderEntity, track: TrackEntity): DocumentTree.Child? {
        val base = track.fileName.substringBeforeLast('.')
        val children = listing(folder, track.relativePath)
        return IMAGE_EXTENSIONS.firstNotNullOfOrNull { ext -> children.firstOrNull { !it.isDirectory && it.name.equals("$base.$ext", ignoreCase = true) } }
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

    /** 保存结果：实际的保存位置；写入文件或同名图片时 [files] 是写了几个。 */
    data class Saved(val mode: CoverSaveMode, val files: Int)

    /**
     * 把封面保存给同一专辑的 [tracks]（需求 C-3、C-4），第一首决定隐藏目录里的文件名。图片统一转成 JPEG。
     * - 隐藏目录：整张专辑存一张
     * - 写入文件 / 同名图片：每个文件都写；有写不进去的（比如没有"所有文件访问权限"）就再在隐藏目录存一张
     * 保存后立即用新封面替换缓存，并通知界面重新加载同一专辑的所有封面。
     */
    suspend fun save(tracks: List<TrackEntity>, image: ByteArray, mode: CoverSaveMode): Saved? = withContext(Dispatchers.IO) {
        val first = tracks.firstOrNull() ?: return@withContext null
        val jpeg = toJpeg(image) ?: return@withContext null
        val folders = HashMap<Long, LibraryFolderEntity?>()
        suspend fun folderOf(t: TrackEntity) = folders.getOrPut(t.folderId) { folderDao.byId(t.folderId) }
        var files = 0
        var failed = false
        if (mode != CoverSaveMode.HIDDEN_FOLDER) {
            for (t in tracks.distinctBy { it.fileKey }) {
                val folder = folderOf(t)
                val ok = folder != null && if (mode == CoverSaveMode.EMBED) embed(folder, t, jpeg) else writeSidecar(folder, t, jpeg)
                // 隐藏目录里的封面优先级最高：存到别处时删掉这首歌原来的，否则新封面不会显示；
                // 写入文件时同名图片也排在前面，同样删掉（需求 C-7）
                if (ok) {
                    deleteHidden(folder, t)
                    if (mode == CoverSaveMode.EMBED) deleteSidecar(folder, t)
                    files++
                } else {
                    failed = true
                }
            }
        }
        val hidden = (mode == CoverSaveMode.HIDDEN_FOLDER || failed) && folderOf(first)?.let { writeHidden(it, first, jpeg) } == true
        val saved = when {
            files > 0 -> Saved(mode, files)
            hidden -> Saved(CoverSaveMode.HIDDEN_FOLDER, 0)
            else -> null
        }
        if (saved != null) replaceCache(first, jpeg)
        saved
    }

    /** 没有"所有文件访问权限"时不写入（保持修改时间不变需要它），由调用方改存到隐藏目录。 */
    private suspend fun embed(folder: LibraryFolderEntity, track: TrackEntity, jpeg: ByteArray): Boolean = editor.edit(folder, track) { pfd ->
        val others = TagLib.getPictures(pfd.dup().detachFd()).filter { it.pictureType != FRONT_COVER }
        val pictures = listOf(Picture(jpeg, "", FRONT_COVER, "image/jpeg")) + others
        TagLib.savePictures(pfd.dup().detachFd(), pictures.toTypedArray())
    }

    private fun writeSidecar(folder: LibraryFolderEntity, track: TrackEntity, jpeg: ByteArray): Boolean {
        val dirId = DocumentTree.directoryDocumentId(folder, track.relativePath)
        val name = track.fileName.substringBeforeLast('.') + ".jpg"
        return tree.writeFile(folder, dirId, name, jpeg).also { listings.remove("${folder.id}:${track.relativePath}") }
    }

    private fun writeHidden(folder: LibraryFolderEntity, track: TrackEntity, jpeg: ByteArray): Boolean {
        val dirId = tree.appDirectory(folder, COVERS_DIR) ?: return false
        return tree.writeFile(folder, dirId, hiddenNames(track).first(), jpeg).also { listings.remove("${folder.id}:${coversPath(folder)}") }
    }

    private fun deleteSidecar(folder: LibraryFolderEntity, track: TrackEntity) {
        sidecarFile(folder, track)?.let { tree.delete(folder, it.documentId) }
        listings.remove("${folder.id}:${track.relativePath}")
    }

    /**
     * 把已经下载成文件的封面写入音乐文件（需求 C-7）：每个文件用它自己的（隐藏目录 > 同名图片，和显示时的顺序一样），
     * 写入成功后删掉同名图片；隐藏目录里的一张图片由整张专辑共用，专辑里的文件都写入成功才删。
     * 目录里的 cover.jpg 等不是下载的，不动。返回写入了几个文件。
     */
    suspend fun embedDownloaded(tracks: List<TrackEntity>, isStopped: () -> Boolean = { false }): Int = withContext(Dispatchers.IO) {
        if (!canEmbed()) return@withContext 0
        val folders = HashMap<Long, LibraryFolderEntity?>()
        // 隐藏目录里的图片 → 还有没写进去的文件
        val hiddenUsers = HashMap<Pair<Long, String>, Boolean>()
        val hiddenFiles = HashMap<Pair<Long, String>, DocumentTree.Child>()
        val changed = ArrayList<TrackEntity>()
        // 按目录排，目录列表的缓存才能命中
        for (t in tracks.distinctBy { it.fileKey }.sortedWith(compareBy({ it.folderId }, { it.relativePath }))) {
            if (isStopped()) break
            val folder = folders.getOrPut(t.folderId) { folderDao.byId(t.folderId) } ?: continue
            val hidden = hiddenFile(folder, t)
            val side = sidecarFile(folder, t)
            val source = hidden ?: side ?: continue
            val jpeg = tree.read(folder, source.documentId)?.let(::toJpeg)
            val ok = jpeg != null && embed(folder, t, jpeg)
            hidden?.let {
                val key = folder.id to it.documentId
                hiddenFiles[key] = it
                hiddenUsers[key] = (hiddenUsers[key] ?: true) && ok
            }
            if (!ok) continue
            if (side != null) deleteSidecar(folder, t)
            changed += t
        }
        for ((key, allDone) in hiddenUsers) {
            if (!allDone) continue
            val folder = folders[key.first] ?: continue
            tree.delete(folder, hiddenFiles.getValue(key).documentId)
            listings.remove("${folder.id}:${coversPath(folder)}")
        }
        // 显示的还是同一张图，但来源变了：清掉缓存重新找，免得缓存和文件对不上
        changed.distinctBy { cacheKey(it) }.forEach { invalidate(it) }
        changed.size
    }

    private suspend fun invalidate(track: TrackEntity) {
        val key = cacheKey(track)
        locks.getOrPut(key) { Mutex() }.withLock {
            File(cacheDir, "$key.none").delete()
            for (size in listOf(SMALL_PX, LARGE_PX)) File(cacheDir, "${key}_$size.jpg").delete()
        }
        ArtworkVersions.bump(track.albumKey?.let { trackDao.idsByAlbum(it) }.orEmpty() + track.id)
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

        /** 找同专辑封面时最多看几个文件：专辑一般不会更大，避免误把几百首归成一张"专辑"时逐个读文件。 */
        private const val MAX_ALBUM_FILES = 50
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
