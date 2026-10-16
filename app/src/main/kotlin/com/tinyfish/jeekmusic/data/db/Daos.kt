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
        SELECT f.*, (SELECT COUNT(*) FROM track t WHERE t.folder_id = f.id) AS track_count
        FROM library_folder f ORDER BY f.display_name
        """,
    )
    fun observeWithCounts(): Flow<List<FolderWithCount>>
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

    /** 某个音乐文件夹下、以 prefix 开头的各目录的歌曲数，用来在内存中折叠出子文件夹。 */
    @Query(
        """
        SELECT relative_path, COUNT(*) AS count FROM track
        WHERE folder_id = :folderId AND relative_path LIKE :prefix || '%' ESCAPE '\'
        GROUP BY relative_path
        """,
    )
    fun observeDirectoryCounts(folderId: Long, prefix: String): Flow<List<DirectoryCount>>

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
}

@Dao
interface TabDao {
    @Query("SELECT * FROM pinned_tab ORDER BY position")
    fun observeAll(): Flow<List<PinnedTabEntity>>

    @Query("SELECT * FROM pinned_tab ORDER BY position")
    suspend fun all(): List<PinnedTabEntity>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insert(tabs: List<PinnedTabEntity>)

    @Query("DELETE FROM pinned_tab WHERE type = :type AND ref = :ref")
    suspend fun delete(type: String, ref: String)

    @Query("UPDATE pinned_tab SET position = :position WHERE id = :id")
    suspend fun setPosition(id: Long, position: Int)

    @Query("SELECT COALESCE(MAX(position), -1) FROM pinned_tab")
    suspend fun maxPosition(): Int
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
    @Query(
        """
        SELECT * FROM track WHERE title LIKE :pattern ESCAPE '\' OR artist_display LIKE :pattern ESCAPE '\'
        OR album LIKE :pattern ESCAPE '\' OR file_name LIKE :pattern ESCAPE '\'
        ORDER BY title COLLATE NOCASE LIMIT :limit
        """,
    )
    suspend fun tracks(pattern: String, limit: Int): List<TrackEntity>

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
interface MoodDao {
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

    @Query("SELECT * FROM track_features WHERE failed = 0")
    suspend fun allFeatures(): List<TrackFeaturesEntity>

    @Query("SELECT * FROM track_features WHERE track_key = :trackKey")
    suspend fun features(trackKey: String): TrackFeaturesEntity?

    @Query("SELECT * FROM mood_override")
    suspend fun overrides(): List<MoodOverrideEntity>

    @Query("SELECT * FROM mood_override WHERE track_key = :trackKey")
    suspend fun override(trackKey: String): MoodOverrideEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun setOverride(entity: MoodOverrideEntity)

    @Query("DELETE FROM mood_override WHERE track_key = :trackKey")
    suspend fun clearOverride(trackKey: String)

    @Query("DELETE FROM track_mood")
    suspend fun clearMoods()

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertMoods(moods: List<TrackMoodEntity>)

    @Query("SELECT * FROM track_mood WHERE track_key = :trackKey")
    suspend fun mood(trackKey: String): TrackMoodEntity?

    @Query("SELECT t.* FROM track_mood m JOIN track t ON t.track_key = m.track_key WHERE m.mood = :mood ORDER BY t.title COLLATE NOCASE")
    fun observeTracks(mood: String): Flow<List<TrackEntity>>
}
