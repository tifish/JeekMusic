package com.tinyfish.jeekmusic.data.scanner

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.provider.MediaStore
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton
import org.json.JSONArray
import org.json.JSONObject

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
    val modifiedNanos: Int? = null,
) {
    val fileKey: String get() = "$volumeName:$relativePath$fileName"
}

/** 从 MediaStore 发现音频文件（docs/design.md §4.1）。 */
@Singleton
class MediaStoreSource @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val fileIndex: LocalFileIndex = LocalFileIndex(),
) {
    fun requestFileRefresh()=fileIndex.requestRefresh()
    suspend fun query(volumeName: String, relativePath: String): List<MediaStoreAudio> = withContext(Dispatchers.IO) {
        val cacheKey="library:$volumeName:$relativePath"
        val version=MediaStore.getVersion(context,volumeName)
        val generation=MediaStore.getGeneration(context,volumeName)
        val cached=fileIndex.mediaCheckpoint(cacheKey)?.takeIf { it.optString("version")==version && it.optLong("generation")<=generation }
        val previous=cached?.getJSONArray("rows")?.let { rows -> List(rows.length()) { fromJson(rows.getJSONObject(it)) } }.orEmpty()
        if(cached?.optLong("generation")==generation)return@withContext previous
        val byId=previous.associateBy { it.mediaStoreId }
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
            MediaStore.Audio.Media.GENERATION_MODIFIED,
        )
        val (selection, args) = if (relativePath.isEmpty()) {
            null to null
        } else {
            "${MediaStore.Audio.Media.RELATIVE_PATH} LIKE ? ESCAPE '\\'" to arrayOf(escapeLike(relativePath) + "%")
        }
        val result: List<MediaStoreAudio> = context.contentResolver.query(collection, projection, selection, args, null)?.use { c ->
            val id = c.getColumnIndexOrThrow(MediaStore.Audio.Media._ID)
            val path = c.getColumnIndexOrThrow(MediaStore.Audio.Media.RELATIVE_PATH)
            val name = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DISPLAY_NAME)
            val modified = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DATE_MODIFIED)
            val size = c.getColumnIndexOrThrow(MediaStore.Audio.Media.SIZE)
            val duration = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DURATION)
            val mime = c.getColumnIndexOrThrow(MediaStore.Audio.Media.MIME_TYPE)
            val bitrate = c.getColumnIndexOrThrow(MediaStore.Audio.Media.BITRATE)
            val modifiedGeneration=c.getColumnIndexOrThrow(MediaStore.Audio.Media.GENERATION_MODIFIED)
            buildList<MediaStoreAudio>(c.count) {
                while (c.moveToNext()) {
                    val fileName = c.getString(name) ?: continue
                    val retained=byId[c.getLong(id)]?.takeIf { old -> old.dateModified==c.getLong(modified) && old.size==c.getLong(size) && c.getLong(modifiedGeneration)<=cached!!.optLong("generation") }
                    add(
                        MediaStoreAudio(
                            mediaStoreId = c.getLong(id),
                            volumeName = volumeName,
                            relativePath = c.getString(path).orEmpty(),
                            fileName = fileName,
                            dateModified = c.getLong(modified),
                            modifiedNanos = if(retained!=null)retained.modifiedNanos else readModifiedNanos(contentUri(volumeName, c.getLong(id)), c.getLong(modified), c.getLong(size)),
                            size = c.getLong(size),
                            durationMs = c.getLong(duration),
                            mimeType = c.getString(mime),
                            bitrate = if (c.isNull(bitrate)) null else c.getLong(bitrate).toIntOrNullSafe(),
                        ),
                    )
                }
            }
        } ?: throw java.io.IOException("Media index unavailable")
        fileIndex.mediaCheckpoint(cacheKey,JSONObject().put("version",version).put("generation",generation).put("rows",JSONArray(result.map(::toJson))))
        result
    }

    private fun Long.toIntOrNullSafe(): Int? = if (this in 1..Int.MAX_VALUE) toInt() else null

    // Metadata only: no read of audio bytes and no guessed filesystem path. A stale MediaStore
    // row must not be combined with the fractional timestamp of a different file version.
    internal fun readModifiedNanos(uri: Uri, seconds: Long, size: Long): Int? = try {
        context.contentResolver.openFileDescriptor(uri, "r")?.use { descriptor ->
            val identity = fileIndex.observeDescriptor(descriptor.fd)
            matchingModifiedNanos(seconds, size, identity)
        }
    } catch (_: java.io.IOException) { null }
      catch (_: SecurityException) { null }

    companion object {
        private fun toJson(row: MediaStoreAudio)=JSONObject().put("id",row.mediaStoreId).put("volume",row.volumeName).put("path",row.relativePath).put("name",row.fileName).put("modified",row.dateModified).put("size",row.size).put("duration",row.durationMs).put("mime",row.mimeType).put("bitrate",row.bitrate).put("nanos",row.modifiedNanos)
        private fun fromJson(row: JSONObject)=MediaStoreAudio(row.getLong("id"),row.getString("volume"),row.getString("path"),row.getString("name"),row.getLong("modified"),row.getLong("size"),row.getLong("duration"),row.optString("mime").takeUnless { row.isNull("mime") },row.optInt("bitrate").takeUnless { row.isNull("bitrate") },row.optInt("nanos").takeUnless { row.isNull("nanos") })
        internal fun matchingModifiedNanos(seconds: Long, size: Long, identity: LongArray): Int? =
            if (identity.size == 5 && identity[2] == size && identity[3] == seconds && identity[4] in 0..999_999_999L)
                identity[4].toInt() else null

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
