package com.tinyfish.jeekmusic.data.db

import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Embedded
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Update
import kotlinx.coroutines.flow.Flow

data class FolderWithCount(
    @Embedded val folder: LibraryFolderEntity,
    @ColumnInfo(name = "track_count") val trackCount: Int,
)

/** 某个艺术家在当前筛选下的歌曲数。 */
data class ArtistCount(
    @ColumnInfo(name = "artist_id") val artistId: Long,
    val count: Int,
)

data class DirectoryCount(
    @ColumnInfo(name = "relative_path") val relativePath: String,
    val count: Int,
)

/** 扫描时用于增量比对的最小信息。 */
data class TrackSignature(
    val id: Long,
    @ColumnInfo(name = "track_key") val trackKey: String,
    @ColumnInfo(name = "file_key") val fileKey: String,
    @ColumnInfo(name = "date_modified") val dateModified: Long,
    val size: Long,
)

@Dao
interface FolderDao {
    @Insert
    suspend fun insert(folder: LibraryFolderEntity): Long

    @Query("DELETE FROM library_folder WHERE id IN (:ids)")
    suspend fun delete(ids: List<Long>)

    @Query("SELECT * FROM library_folder ORDER BY display_name")
    suspend fun all(): List<LibraryFolderEntity>

    @Query("SELECT * FROM library_folder WHERE id = :id")
    suspend fun byId(id: Long): LibraryFolderEntity?

    @Query(
        """
        SELECT f.*, (
            SELECT COUNT(*) FROM track t WHERE t.folder_id = f.id
            AND (:mood IS NULL OR t.track_key IN (SELECT track_key FROM track_tag WHERE dimension = 'MOOD' AND value = :mood))
            AND (:voice IS NULL OR t.track_key IN (SELECT track_key FROM track_tag WHERE dimension = 'VOICE' AND value = :voice))
            AND (:genre IS NULL OR t.track_key IN (SELECT track_key FROM track_tag WHERE dimension = 'GENRE' AND value = :genre))
        ) AS track_count
        FROM library_folder f ORDER BY f.display_name
        """,
    )
    fun observeWithCounts(mood: String?, voice: String?, genre: String?): Flow<List<FolderWithCount>>
}

@Dao
interface TrackDao {
    @Query("SELECT id, track_key, file_key, date_modified, size FROM track")
    suspend fun signatures(): List<TrackSignature>

    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insert(tracks: List<TrackEntity>): List<Long>

    @Update
    suspend fun update(tracks: List<TrackEntity>)

    @Query("DELETE FROM track WHERE id IN (:ids)")
    suspend fun delete(ids: List<Long>)

    @Query("SELECT * FROM track WHERE id = :id")
    suspend fun byId(id: Long): TrackEntity?

    @Query("SELECT * FROM track WHERE id IN (:ids)")
    suspend fun byIds(ids: List<Long>): List<TrackEntity>

    @Query("SELECT * FROM track WHERE track_key IN (:keys)")
    suspend fun byTrackKeys(keys: List<String>): List<TrackEntity>

    @Query("SELECT * FROM track")
    suspend fun all(): List<TrackEntity>

    @Query("SELECT COUNT(*) FROM track")
    fun observeCount(): Flow<Int>

    /** 某个音乐文件夹下、以 prefix 开头的各目录的歌曲数（只算符合筛选的），用来在内存中折叠出子文件夹。 */
    @Query(
        """
        SELECT relative_path, COUNT(*) AS count FROM track t
        WHERE folder_id = :folderId AND relative_path LIKE :prefix || '%' ESCAPE '\'
        AND (:mood IS NULL OR t.track_key IN (SELECT track_key FROM track_tag WHERE dimension = 'MOOD' AND value = :mood))
        AND (:voice IS NULL OR t.track_key IN (SELECT track_key FROM track_tag WHERE dimension = 'VOICE' AND value = :voice))
        AND (:genre IS NULL OR t.track_key IN (SELECT track_key FROM track_tag WHERE dimension = 'GENRE' AND value = :genre))
        GROUP BY relative_path
        """,
    )
    fun observeDirectoryCounts(folderId: Long, prefix: String, mood: String?, voice: String?, genre: String?): Flow<List<DirectoryCount>>

    @Query("SELECT * FROM track WHERE folder_id = :folderId AND relative_path = :relativePath")
    fun observeInDirectory(folderId: Long, relativePath: String): Flow<List<TrackEntity>>

    @Query("""SELECT * FROM track WHERE folder_id = :folderId AND relative_path LIKE :prefix || '%' ESCAPE '\'""")
    suspend fun under(folderId: Long, prefix: String): List<TrackEntity>

    @Query(
        """
        SELECT t.* FROM track t JOIN track_artist ta ON ta.track_id = t.id
        JOIN artist a ON a.id = ta.artist_id WHERE a.name_key = :nameKey
        """,
    )
    fun observeByArtist(nameKey: String): Flow<List<TrackEntity>>

    @Query("SELECT * FROM track WHERE album_key = :albumKey ORDER BY disc_no, track_no, file_name")
    fun observeByAlbum(albumKey: String): Flow<List<TrackEntity>>

    /** 写入封面、歌词后立即更新文件大小（修改时间保持不变），不用等重新扫描。 */
    @Query("UPDATE track SET size = :size WHERE file_key = :fileKey")
    suspend fun updateSize(fileKey: String, size: Long)

    @Query("SELECT * FROM track WHERE album_key = :albumKey ORDER BY disc_no, track_no, file_name")
    suspend fun byAlbum(albumKey: String): List<TrackEntity>

    @Query("SELECT id FROM track WHERE album_key = :albumKey")
    suspend fun idsByAlbum(albumKey: String): List<Long>
}

@Dao
interface ArtistDao {
    @Query("SELECT * FROM artist WHERE name_key IN (:keys)")
    suspend fun byNameKeys(keys: List<String>): List<ArtistEntity>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insert(artists: List<ArtistEntity>): List<Long>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertLinks(links: List<TrackArtistEntity>)

    @Query("DELETE FROM track_artist WHERE track_id IN (:trackIds)")
    suspend fun deleteLinks(trackIds: List<Long>)

    @Query("DELETE FROM track_artist")
    suspend fun deleteAllLinks()

    @Query("DELETE FROM artist WHERE id NOT IN (SELECT artist_id FROM track_artist)")
    suspend fun deleteOrphans()

    @Query("UPDATE artist SET track_count = (SELECT COUNT(*) FROM track_artist ta WHERE ta.artist_id = artist.id)")
    suspend fun updateTrackCounts()

    @Query("SELECT * FROM artist ORDER BY CASE WHEN name_key = '' THEN 1 ELSE 0 END, sort_key, name_key")
    fun observeAll(): Flow<List<ArtistEntity>>

    @Query("SELECT * FROM artist WHERE name_key = :nameKey")
    fun observeByNameKey(nameKey: String): Flow<ArtistEntity?>

    /** 每个艺术家符合筛选的歌曲数；没有这类歌的艺术家不在结果里。 */
    @Query(
        """
        SELECT ta.artist_id, COUNT(*) AS count FROM track_artist ta
        JOIN track t ON t.id = ta.track_id
        WHERE (:mood IS NULL OR t.track_key IN (SELECT track_key FROM track_tag WHERE dimension = 'MOOD' AND value = :mood))
        AND (:voice IS NULL OR t.track_key IN (SELECT track_key FROM track_tag WHERE dimension = 'VOICE' AND value = :voice))
        AND (:genre IS NULL OR t.track_key IN (SELECT track_key FROM track_tag WHERE dimension = 'GENRE' AND value = :genre))
        GROUP BY ta.artist_id
        """,
    )
    fun observeCounts(mood: String?, voice: String?, genre: String?): Flow<List<ArtistCount>>
}

@Dao
interface TabDao {
    @Query("SELECT * FROM pinned_tab ORDER BY position")
    fun observeAll(): Flow<List<PinnedTabEntity>>

    @Query("DELETE FROM pinned_tab WHERE type = :type")
    suspend fun deleteType(type: String)

    @Query("SELECT * FROM pinned_tab ORDER BY position")
    suspend fun all(): List<PinnedTabEntity>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insert(tabs: List<PinnedTabEntity>)

    @Query("DELETE FROM pinned_tab WHERE type = :type AND ref = :ref")
    suspend fun delete(type: String, ref: String)

    @Query("UPDATE pinned_tab SET position = :position WHERE id = :id")
    suspend fun setPosition(id: Long, position: Int)
}

data class PlaylistWithCount(
    @Embedded val playlist: PlaylistEntity,
    @ColumnInfo(name = "item_count") val itemCount: Int,
)

/** 播放列表条目；track 为空表示文件暂时不可用（需求 PL-3）。 */
data class PlaylistEntry(
    @Embedded(prefix = "item_") val item: PlaylistItemEntity,
    @Embedded val track: TrackEntity?,
)

@Dao
interface PlaylistDao {
    @Query(
        """
        SELECT p.*, (SELECT COUNT(*) FROM playlist_item i WHERE i.playlist_id = p.id) AS item_count
        FROM playlist p ORDER BY p.name COLLATE NOCASE
        """,
    )
    fun observeAll(): Flow<List<PlaylistWithCount>>

    @Query("SELECT * FROM playlist ORDER BY name COLLATE NOCASE")
    suspend fun all(): List<PlaylistEntity>

    @Query("SELECT * FROM playlist WHERE id = :id")
    fun observe(id: Long): Flow<PlaylistEntity?>

    @Insert
    suspend fun insert(playlist: PlaylistEntity): Long

    @Query("UPDATE playlist SET name = :name, updated_at = :now WHERE id = :id")
    suspend fun rename(id: Long, name: String, now: Long)

    @Query("UPDATE playlist SET updated_at = :now WHERE id = :id")
    suspend fun touch(id: Long, now: Long)

    @Query("DELETE FROM playlist WHERE id = :id")
    suspend fun delete(id: Long)

    @Query(
        """
        SELECT i.id AS item_id, i.playlist_id AS item_playlist_id, i.position AS item_position, i.track_key AS item_track_key, t.*
        FROM playlist_item i LEFT JOIN track t ON t.track_key = i.track_key
        WHERE i.playlist_id = :playlistId ORDER BY i.position
        """,
    )
    fun observeEntries(playlistId: Long): Flow<List<PlaylistEntry>>

    @Query("SELECT * FROM playlist_item WHERE playlist_id = :playlistId ORDER BY position")
    suspend fun items(playlistId: Long): List<PlaylistItemEntity>

    @Query("SELECT COALESCE(MAX(position), -1) FROM playlist_item WHERE playlist_id = :playlistId")
    suspend fun maxPosition(playlistId: Long): Int

    @Insert
    suspend fun insertItems(items: List<PlaylistItemEntity>)

    @Query("DELETE FROM playlist_item WHERE id = :itemId")
    suspend fun deleteItem(itemId: Long)

    @Query("DELETE FROM playlist_item WHERE playlist_id = :playlistId")
    suspend fun clearItems(playlistId: Long)
}

@Dao
interface QueueDao {
    @Query("SELECT track_key FROM queue_item ORDER BY position")
    suspend fun trackKeys(): List<String>

    @Query("DELETE FROM queue_item")
    suspend fun clear()

    @Insert
    suspend fun insert(items: List<QueueItemEntity>)
}

@Dao
interface SearchDao {
    /**
     * 匹配的全部歌曲，不排序也不限制条数：SQL 排不了拼音（COLLATE NOCASE 对中文是按 Unicode 码位），
     * 由调用方在内存里按拼音排好再截取（docs/design.md §5.6）。
     */
    @Query(
        """
        SELECT * FROM track WHERE title LIKE :pattern ESCAPE '\' OR artist_display LIKE :pattern ESCAPE '\'
        OR album LIKE :pattern ESCAPE '\' OR file_name LIKE :pattern ESCAPE '\'
        """,
    )
    suspend fun tracks(pattern: String): List<TrackEntity>

    @Query("""SELECT * FROM artist WHERE name LIKE :pattern ESCAPE '\' ORDER BY sort_key LIMIT :limit""")
    suspend fun artists(pattern: String, limit: Int): List<ArtistEntity>

    @Query(
        """
        SELECT a.* FROM artist a JOIN track_artist ta ON ta.artist_id = a.id
        WHERE ta.track_id = :trackId ORDER BY a.sort_key
        """,
    )
    suspend fun artistsOfTrack(trackId: Long): List<ArtistEntity>
}

@Dao
interface LyricsDao {
    @Query("SELECT offset_ms FROM lyrics_offset WHERE track_key = :trackKey")
    suspend fun offset(trackKey: String): Long?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun setOffset(offset: LyricsOffsetEntity)

    @Query("DELETE FROM lyrics_offset WHERE track_key = :trackKey")
    suspend fun clearOffset(trackKey: String)

    @Query("SELECT * FROM lyrics_offset")
    suspend fun allOffsets(): List<LyricsOffsetEntity>

    @Query("SELECT * FROM lyrics_cache WHERE track_key = :trackKey")
    suspend fun cached(trackKey: String): LyricsCacheEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun cache(entry: LyricsCacheEntity)

    @Query("DELETE FROM lyrics_cache")
    suspend fun clearCache()
}

@Dao
interface TagDao {
    /** 写入封面、歌词后文件大小变了，但音频没变：把同一文件（CUE 整轨的各首）的分析签名改成新的，不用重新分析。 */
    @Query(
        """
        UPDATE track_features SET signature = :newSignature
        WHERE signature = :oldSignature AND track_key IN (SELECT track_key FROM track WHERE file_key = :fileKey)
        """,
    )
    suspend fun renameSignature(fileKey: String, oldSignature: String, newSignature: String)

    /** 还没分析、文件变了、或者分析算法升级了的歌曲。 */
    @Query(
        """
        SELECT t.* FROM track t LEFT JOIN track_features f ON f.track_key = t.track_key
        WHERE f.track_key IS NULL OR f.signature != (t.date_modified || ':' || t.size) OR f.analyzer_version < :version
        LIMIT :limit
        """,
    )
    suspend fun pending(version: Int, limit: Int): List<TrackEntity>

    @Query(
        """
        SELECT COUNT(*) FROM track t LEFT JOIN track_features f ON f.track_key = t.track_key
        WHERE f.track_key IS NULL OR f.signature != (t.date_modified || ':' || t.size) OR f.analyzer_version < :version
        """,
    )
    fun observePendingCount(version: Int): Flow<Int>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun saveFeatures(features: TrackFeaturesEntity)

    /** 智能标签用的歌名和专辑名（曲名补救古典，需求 M-10）。 */
    @Query("SELECT track_key AS trackKey, title, album FROM track")
    suspend fun titles(): List<TrackTitle>

    @Query("SELECT track_key AS trackKey, title, album FROM track WHERE track_key = :trackKey LIMIT 1")
    suspend fun title(trackKey: String): TrackTitle?

    @Query("SELECT * FROM track_features WHERE failed = 0")
    suspend fun allFeatures(): List<TrackFeaturesEntity>

    @Query("SELECT * FROM track_features WHERE track_key = :trackKey")
    suspend fun features(trackKey: String): TrackFeaturesEntity?

    @Query("SELECT * FROM tag_override")
    suspend fun overrides(): List<TagOverrideEntity>

    @Query("SELECT * FROM tag_override WHERE track_key = :trackKey")
    suspend fun overrides(trackKey: String): List<TagOverrideEntity>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun setOverride(entity: TagOverrideEntity)

    @Query("DELETE FROM tag_override WHERE track_key = :trackKey AND dimension = :dimension")
    suspend fun clearOverride(trackKey: String, dimension: String)

    @Query("DELETE FROM track_tag")
    suspend fun clearTags()

    @Query("SELECT COUNT(*) FROM track_tag")
    suspend fun countTags(): Int

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertTags(tags: List<TrackTagEntity>)

    @Query("SELECT * FROM track_tag WHERE track_key = :trackKey")
    suspend fun tags(trackKey: String): List<TrackTagEntity>

    @Query("SELECT track_key FROM track_tag WHERE dimension = :dimension AND value = :value")
    fun observeKeys(dimension: String, value: String): Flow<List<String>>
}

/** 歌曲的歌名和专辑名，见 [TagDao.titles]。 */
data class TrackTitle(val trackKey: String, val title: String, val album: String?)
