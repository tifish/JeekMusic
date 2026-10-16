package com.tinyfish.jeekmusic.dev

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.provider.MediaStore
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject

data class ProbeTrack(
    val id: Long,
    val uri: Uri,
    val title: String,
    val artist: String?,
    val volumeName: String,
    val relativePath: String,
    val fileName: String,
    val durationMs: Long,
    val mimeType: String?,
)

/**
 * M0 验证用：直接列出 MediaStore 中的全部音频。M1 起由 LibraryScanner 按用户选择的文件夹查询。
 */
class MediaStoreProbe @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    suspend fun queryAll(): List<ProbeTrack> = withContext(Dispatchers.IO) {
        val collection = MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL)
        val projection = arrayOf(
            MediaStore.Audio.Media._ID,
            MediaStore.Audio.Media.TITLE,
            MediaStore.Audio.Media.ARTIST,
            MediaStore.Audio.Media.VOLUME_NAME,
            MediaStore.Audio.Media.RELATIVE_PATH,
            MediaStore.Audio.Media.DISPLAY_NAME,
            MediaStore.Audio.Media.DURATION,
            MediaStore.Audio.Media.MIME_TYPE,
        )
        val sortOrder = "${MediaStore.Audio.Media.RELATIVE_PATH}, ${MediaStore.Audio.Media.DISPLAY_NAME}"

        context.contentResolver.query(collection, projection, null, null, sortOrder)?.use { c ->
            val idCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media._ID)
            val titleCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.TITLE)
            val artistCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.ARTIST)
            val volumeCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.VOLUME_NAME)
            val pathCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.RELATIVE_PATH)
            val nameCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DISPLAY_NAME)
            val durationCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DURATION)
            val mimeCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.MIME_TYPE)

            buildList(c.count) {
                while (c.moveToNext()) {
                    val id = c.getLong(idCol)
                    val volume = c.getString(volumeCol) ?: MediaStore.VOLUME_EXTERNAL_PRIMARY
                    add(
                        ProbeTrack(
                            id = id,
                            uri = ContentUris.withAppendedId(MediaStore.Audio.Media.getContentUri(volume), id),
                            title = c.getString(titleCol) ?: c.getString(nameCol).orEmpty(),
                            artist = c.getString(artistCol)?.takeUnless { it == MediaStore.UNKNOWN_STRING },
                            volumeName = volume,
                            relativePath = c.getString(pathCol).orEmpty(),
                            fileName = c.getString(nameCol).orEmpty(),
                            durationMs = c.getLong(durationCol),
                            mimeType = c.getString(mimeCol),
                        ),
                    )
                }
            }
        }.orEmpty()
    }
}
