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

    @Query("SELECT * FROM track WHERE folder_id = :folderId AND relative_path LIKE :prefix || '%' ESCAPE '\'")
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
