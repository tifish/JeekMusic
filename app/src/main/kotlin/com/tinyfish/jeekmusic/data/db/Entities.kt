package com.tinyfish.jeekmusic.data.db

import androidx.compose.runtime.Immutable
import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.ForeignKey
import androidx.room.Index
import androidx.room.PrimaryKey

/** 用户添加的音乐文件夹（docs/design.md §3）。 */
@Entity(
    tableName = "library_folder",
    indices = [Index(value = ["tree_uri"], unique = true)],
)
data class LibraryFolderEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    @ColumnInfo(name = "tree_uri") val treeUri: String,
    /** SAF 树的 documentId，如 "primary:Music"，用来拼出子文件的 documentId。 */
    @ColumnInfo(name = "tree_document_id") val treeDocumentId: String,
    @ColumnInfo(name = "volume_name") val volumeName: String,
    /** 空串或以 "/" 结尾，如 "Music/Pop/"。 */
    @ColumnInfo(name = "relative_path") val relativePath: String,
    @ColumnInfo(name = "display_name") val displayName: String,
    @ColumnInfo(name = "added_at") val addedAt: Long,
)

@Immutable
@Entity(
    tableName = "track",
    foreignKeys = [
        ForeignKey(
            entity = LibraryFolderEntity::class,
            parentColumns = ["id"],
            childColumns = ["folder_id"],
            onDelete = ForeignKey.CASCADE,
        ),
    ],
    indices = [
        Index(value = ["track_key"], unique = true),
        Index(value = ["folder_id", "relative_path"]),
        Index(value = ["album_key"]),
        Index(value = ["file_key"]),
    ],
)
data class TrackEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    /** 稳定键：file_key；CUE 虚拟歌曲为 file_key + "#" + 两位音轨号。跨表引用都用它。 */
    @ColumnInfo(name = "track_key") val trackKey: String,
    /** volume_name + ":" + relative_path + file_name。 */
    @ColumnInfo(name = "file_key") val fileKey: String,
    @ColumnInfo(name = "media_store_id") val mediaStoreId: Long,
    @ColumnInfo(name = "volume_name") val volumeName: String,
    @ColumnInfo(name = "folder_id") val folderId: Long,
    @ColumnInfo(name = "relative_path") val relativePath: String,
    @ColumnInfo(name = "file_name") val fileName: String,
    @ColumnInfo(name = "title_tag") val titleTag: String?,
    /** 展示用：标题标签 → 文件名推断 → 去掉扩展名的文件名。 */
    val title: String,
    val album: String?,
    @ColumnInfo(name = "album_artist") val albumArtist: String?,
    @ColumnInfo(name = "album_key") val albumKey: String?,
    /** 原始艺术家标签，多值以 \u0000 连接；null 表示没有标签。 */
    @ColumnInfo(name = "artist_raw") val artistRaw: String?,
    /** 展示用：艺术家标签或文件名推断结果，都没有时为空串。 */
    @ColumnInfo(name = "artist_display") val artistDisplay: String,
    @ColumnInfo(name = "artist_inferred") val artistInferred: Boolean,
    @ColumnInfo(name = "track_no") val trackNo: Int?,
    @ColumnInfo(name = "disc_no") val discNo: Int?,
    val year: Int?,
    val genre: String?,
    @ColumnInfo(name = "duration_ms") val durationMs: Long,
    @ColumnInfo(name = "mime_type") val mimeType: String?,
    val bitrate: Int?,
    @ColumnInfo(name = "sample_rate") val sampleRate: Int?,
    @ColumnInfo(name = "date_modified") val dateModified: Long,
    val size: Long,
    @ColumnInfo(name = "has_embedded_lyrics") val hasEmbeddedLyrics: Boolean,
    /** 以下三项只有 CUE 虚拟歌曲才有（docs/design.md §4.6）。 */
    @ColumnInfo(name = "cue_track_no") val cueTrackNo: Int? = null,
    @ColumnInfo(name = "start_ms") val startMs: Long? = null,
    @ColumnInfo(name = "end_ms") val endMs: Long? = null,
)

@Immutable
@Entity(
    tableName = "artist",
    indices = [Index(value = ["name_key"], unique = true), Index(value = ["sort_key"])],
)
data class ArtistEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    /** 首次出现时的写法；"未知艺术家"为空串。 */
    val name: String,
    @ColumnInfo(name = "name_key") val nameKey: String,
    @ColumnInfo(name = "sort_key") val sortKey: String,
    @ColumnInfo(name = "index_letter") val indexLetter: String,
    @ColumnInfo(name = "track_count") val trackCount: Int = 0,
)

@Entity(
    tableName = "track_artist",
    primaryKeys = ["track_id", "artist_id"],
    foreignKeys = [
        ForeignKey(entity = TrackEntity::class, parentColumns = ["id"], childColumns = ["track_id"], onDelete = ForeignKey.CASCADE),
        ForeignKey(entity = ArtistEntity::class, parentColumns = ["id"], childColumns = ["artist_id"], onDelete = ForeignKey.CASCADE),
    ],
    indices = [Index(value = ["artist_id"])],
)
data class TrackArtistEntity(
    @ColumnInfo(name = "track_id") val trackId: Long,
    @ColumnInfo(name = "artist_id") val artistId: Long,
)

/** "未知艺术家"在 artist 表中的 name_key。 */
const val UNKNOWN_ARTIST_KEY = ""

/** 标签栏（需求 T-1 ~ T-5，docs/design.md §5.1）。 */
@Entity(tableName = "pinned_tab", indices = [Index(value = ["type", "ref"], unique = true)])
data class PinnedTabEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    /** FOLDERS | ARTISTS | PLAYLISTS | ARTIST | MOOD */
    val type: String,
    /** ARTIST：artist.name_key；MOOD："SPORT" / "QUIET"；内置标签为空串。 */
    val ref: String = "",
    val position: Int,
)

@Entity(tableName = "playlist")
data class PlaylistEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val name: String,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
)

@Entity(
    tableName = "playlist_item",
    foreignKeys = [ForeignKey(entity = PlaylistEntity::class, parentColumns = ["id"], childColumns = ["playlist_id"], onDelete = ForeignKey.CASCADE)],
    indices = [Index(value = ["playlist_id", "position"])],
)
data class PlaylistItemEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    @ColumnInfo(name = "playlist_id") val playlistId: Long,
    val position: Int,
    /** 引用稳定键，不引用 track.id，文件暂时不可用时条目保留（需求 PL-3）。 */
    @ColumnInfo(name = "track_key") val trackKey: String,
)

/** 续播队列（docs/design.md §7.5）。 */
@Entity(tableName = "queue_item")
data class QueueItemEntity(
    @PrimaryKey val position: Int,
    @ColumnInfo(name = "track_key") val trackKey: String,
)

/** 每首歌的歌词时间偏移（需求 LY-6）。 */
@Entity(tableName = "lyrics_offset")
data class LyricsOffsetEntity(
    @PrimaryKey @ColumnInfo(name = "track_key") val trackKey: String,
    @ColumnInfo(name = "offset_ms") val offsetMs: Long,
)

/** 在线歌词无法写回音乐目录时的兜底，以及"未找到"的负缓存（docs/design.md §8.4、§8.5）。 */
@Entity(tableName = "lyrics_cache")
data class LyricsCacheEntity(
    @PrimaryKey @ColumnInfo(name = "track_key") val trackKey: String,
    val source: String,
    /** null 表示在线也没有找到。 */
    val content: String?,
    val synced: Boolean,
    @ColumnInfo(name = "fetched_at") val fetchedAt: Long,
)
