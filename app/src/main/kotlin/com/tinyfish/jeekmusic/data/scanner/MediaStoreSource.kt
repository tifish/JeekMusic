package com.tinyfish.jeekmusic.data.scanner

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.provider.MediaStore
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject

data class MediaStoreAudio(
    val mediaStoreId: Long,
    val volumeName: String,
    val relativePath: String,
    val fileName: String,
    val dateModified: Long,
    val size: Long,
    val durationMs: Long,
    val mimeType: String?,
    val bitrate: Int?,
) {
    val fileKey: String get() = "$volumeName:$relativePath$fileName"
}

/** 从 MediaStore 发现音频文件（docs/design.md §4.1）。 */
class MediaStoreSource @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    suspend fun query(volumeName: String, relativePath: String): List<MediaStoreAudio> = withContext(Dispatchers.IO) {
        val collection = MediaStore.Audio.Media.getContentUri(volumeName)
        val projection = arrayOf(
            MediaStore.Audio.Media._ID,
            MediaStore.Audio.Media.RELATIVE_PATH,
            MediaStore.Audio.Media.DISPLAY_NAME,
            MediaStore.Audio.Media.DATE_MODIFIED,
            MediaStore.Audio.Media.SIZE,
            MediaStore.Audio.Media.DURATION,
            MediaStore.Audio.Media.MIME_TYPE,
            MediaStore.Audio.Media.BITRATE,
        )
        val (selection, args) = if (relativePath.isEmpty()) {
            null to null
        } else {
            "${MediaStore.Audio.Media.RELATIVE_PATH} LIKE ? ESCAPE '\\'" to arrayOf(escapeLike(relativePath) + "%")
        }
        context.contentResolver.query(collection, projection, selection, args, null)?.use { c ->
            val id = c.getColumnIndexOrThrow(MediaStore.Audio.Media._ID)
            val path = c.getColumnIndexOrThrow(MediaStore.Audio.Media.RELATIVE_PATH)
            val name = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DISPLAY_NAME)
            val modified = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DATE_MODIFIED)
            val size = c.getColumnIndexOrThrow(MediaStore.Audio.Media.SIZE)
            val duration = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DURATION)
            val mime = c.getColumnIndexOrThrow(MediaStore.Audio.Media.MIME_TYPE)
            val bitrate = c.getColumnIndexOrThrow(MediaStore.Audio.Media.BITRATE)
            buildList(c.count) {
                while (c.moveToNext()) {
                    val fileName = c.getString(name) ?: continue
                    add(
                        MediaStoreAudio(
                            mediaStoreId = c.getLong(id),
                            volumeName = volumeName,
                            relativePath = c.getString(path).orEmpty(),
                            fileName = fileName,
                            dateModified = c.getLong(modified),
                            size = c.getLong(size),
                            durationMs = c.getLong(duration),
                            mimeType = c.getString(mime),
                            bitrate = if (c.isNull(bitrate)) null else c.getLong(bitrate).toIntOrNullSafe(),
                        ),
                    )
                }
            }
        }.orEmpty()
    }

    private fun Long.toIntOrNullSafe(): Int? = if (this in 1..Int.MAX_VALUE) toInt() else null

    companion object {
        fun contentUri(volumeName: String, mediaStoreId: Long): Uri =
            ContentUris.withAppendedId(MediaStore.Audio.Media.getContentUri(volumeName), mediaStoreId)

        internal fun escapeLike(value: String) =
            value.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
    }
}

/** ExoPlayer 能原生播放的格式（需求 P-9）。其他格式扫描时跳过，只计数。 */
object SupportedFormats {
    private val EXTENSIONS = setOf("mp3", "flac", "m4a", "mp4", "aac", "ogg", "oga", "opus", "wav", "mka", "amr")

    fun isSupported(fileName: String): Boolean =
        fileName.substringAfterLast('.', "").lowercase() in EXTENSIONS
}
