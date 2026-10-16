package com.tinyfish.jeekmusic.playback

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.DocumentsContract
import com.kyant.taglib.TagLib
import com.tinyfish.jeekmusic.data.db.FolderDao
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.runBlocking
import java.io.File
import java.io.FileNotFoundException
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

/**
 * 封面的统一入口（docs/design.md §7.4）：列表缩略图、播放页、通知栏、锁屏都从这里取。
 *
 * content://<authority>/track/<trackId>/<small|large>
 *
 * 优先内嵌封面，其次同目录的 cover.jpg / folder.jpg 等（需求 C-1）；都没有时返回 FileNotFound，由调用方显示占位图。
 * 结果按专辑缓存（没有专辑时按歌曲），同一张专辑只解码一次；"没有封面"也会缓存。
 */
class ArtworkProvider : ContentProvider() {

    @EntryPoint
    @InstallIn(SingletonComponent::class)
    interface Dependencies {
        fun trackDao(): TrackDao
        fun folderDao(): FolderDao
    }

    private val deps by lazy {
        EntryPointAccessors.fromApplication(requireContext().applicationContext, Dependencies::class.java)
    }
    private val cacheDir by lazy { File(requireContext().cacheDir, "artwork").apply { mkdirs() } }
    private val locks = ConcurrentHashMap<String, Any>()

    override fun onCreate() = true

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        val segments = uri.pathSegments
        val trackId = segments.getOrNull(1)?.toLongOrNull() ?: throw FileNotFoundException(uri.toString())
        val size = if (segments.getOrNull(2) == "large") LARGE_PX else SMALL_PX

        val track = runBlocking { deps.trackDao().byId(trackId) } ?: throw FileNotFoundException(uri.toString())
        val key = sha1(track.albumKey ?: track.trackKey)
        val file = File(cacheDir, "${key}_$size.jpg")
        val none = File(cacheDir, "$key.none")

        if (!file.exists()) {
            synchronized(locks.getOrPut(key) { Any() }) {
                if (!file.exists() && !none.exists()) {
                    val bytes = embeddedCover(track) ?: folderImage(track)
                    if (bytes == null || !writeScaled(bytes, size, file)) none.createNewFile()
                }
            }
        }
        if (!file.exists()) throw FileNotFoundException("no artwork for $uri")
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY)
    }

    private fun embeddedCover(track: TrackEntity): ByteArray? = runCatching {
        val uri = MediaStoreSource.contentUri(track.volumeName, track.mediaStoreId)
        requireContext().contentResolver.openFileDescriptor(uri, "r")?.use { pfd ->
            TagLib.getFrontCover(pfd.dup().detachFd())?.data
        }
    }.getOrNull()

    private fun folderImage(track: TrackEntity): ByteArray? = runCatching {
        val folder = runBlocking { deps.folderDao().byId(track.folderId) } ?: return null
        val treeUri = Uri.parse(folder.treeUri)
        val dirId = directoryDocumentId(folder, track.relativePath)
        val children = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, dirId)
        val resolver = requireContext().contentResolver
        val candidates = resolver.query(
            children,
            arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME),
            null, null, null,
        )?.use { c ->
            buildList { while (c.moveToNext()) add(c.getString(0) to c.getString(1).lowercase()) }
        }.orEmpty()
        val best = COVER_NAMES.firstNotNullOfOrNull { name -> candidates.firstOrNull { it.second == name } } ?: return null
        resolver.openInputStream(DocumentsContract.buildDocumentUriUsingTree(treeUri, best.first))?.use { it.readBytes() }
    }.getOrNull()

    private fun writeScaled(bytes: ByteArray, size: Int, target: File): Boolean {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return false
        var sample = 1
        while (bounds.outWidth / (sample * 2) >= size && bounds.outHeight / (sample * 2) >= size) sample *= 2
        val decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample })
            ?: return false
        val scale = size.toFloat() / maxOf(decoded.width, decoded.height)
        val bitmap = if (scale < 1f) {
            Bitmap.createScaledBitmap(decoded, (decoded.width * scale).toInt().coerceAtLeast(1), (decoded.height * scale).toInt().coerceAtLeast(1), true)
        } else {
            decoded
        }
        val tmp = File(target.parentFile, target.name + ".tmp")
        tmp.outputStream().use { bitmap.compress(Bitmap.CompressFormat.JPEG, 90, it) }
        return tmp.renameTo(target)
    }

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor? = null
    override fun getType(uri: Uri) = "image/jpeg"
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?) = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?) = 0

    companion object {
        private const val SMALL_PX = 144
        private const val LARGE_PX = 720

        private val COVER_NAMES = listOf("cover", "folder", "front", "albumart", "album")
            .flatMap { base -> listOf("jpg", "jpeg", "png", "webp").map { "$base.$it" } }

        /** 歌曲所在目录在 SAF 树中的 documentId（ExternalStorageProvider 的 id 是基于路径的）。 */
        fun directoryDocumentId(folder: LibraryFolderEntity, trackRelativePath: String): String {
            val rest = trackRelativePath.removePrefix(folder.relativePath).trimEnd('/')
            return when {
                rest.isEmpty() -> folder.treeDocumentId
                folder.treeDocumentId.endsWith(":") -> folder.treeDocumentId + rest
                else -> "${folder.treeDocumentId.trimEnd('/')}/$rest"
            }
        }

        private fun sha1(text: String): String =
            MessageDigest.getInstance("SHA-1").digest(text.toByteArray()).joinToString("") { "%02x".format(it) }
    }
}
