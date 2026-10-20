package com.tinyfish.jeekmusic.playback

import android.net.Uri
import android.os.Bundle
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import com.tinyfish.jeekmusic.BuildConfig
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource

/** Track ↔ MediaItem（docs/design.md §7.4）。 */
object MediaItemMapper {

    const val EXTRA_TRACK_KEY = "track_key"
    const val EXTRA_ALBUM_KEY = "album_key"
    const val EXTRA_ARTIST_KEYS = "artist_keys"

    /** 手动添加到队列的（"下一首播放""添加到队列"，需求 P-15）；队列重新生成时不动它们。 */
    const val EXTRA_MANUAL = "manual"

    private const val PREFIX = "track:"
    const val ARTWORK_AUTHORITY = "${BuildConfig.APPLICATION_ID}.artwork"

    fun mediaId(trackId: Long) = "$PREFIX$trackId"

    fun trackId(mediaId: String): Long? = if (mediaId.startsWith(PREFIX)) mediaId.substring(PREFIX.length).toLongOrNull() else null

    fun artworkUri(trackId: Long, large: Boolean = false): Uri =
        Uri.parse("content://$ARTWORK_AUTHORITY/track/$trackId/${if (large) "large" else "small"}")

    fun isManual(item: MediaItem): Boolean = item.mediaMetadata.extras?.getBoolean(EXTRA_MANUAL) == true

    /** Service 端使用：带播放 URI 和完整元数据。 */
    fun toMediaItem(track: TrackEntity, manual: Boolean = false): MediaItem = MediaItem.Builder()
        .setMediaId(mediaId(track.id))
        .setUri(MediaStoreSource.contentUri(track.volumeName, track.mediaStoreId))
        .setMediaMetadata(metadata(track, manual))
        .apply {
            if (track.startMs != null) {
                setClippingConfiguration(
                    MediaItem.ClippingConfiguration.Builder()
                        .setStartPositionMs(track.startMs)
                        .setEndPositionMs(track.endMs ?: C.TIME_END_OF_SOURCE)
                        .build(),
                )
            }
        }
        .build()

    private fun metadata(track: TrackEntity, manual: Boolean = false): MediaMetadata = MediaMetadata.Builder()
        .setTitle(track.title)
        .setArtist(track.artistDisplay.ifEmpty { null })
        .setAlbumTitle(track.album)
        .setArtworkUri(artworkUri(track.id, large = true))
        .setDurationMs(track.durationMs)
        .setIsPlayable(true)
        .setIsBrowsable(false)
        .setExtras(
            Bundle().apply {
                putString(EXTRA_TRACK_KEY, track.trackKey)
                putString(EXTRA_ALBUM_KEY, track.albumKey)
                if (manual) putBoolean(EXTRA_MANUAL, true)
            },
        )
        .build()
}
