package com.tinyfish.jeekmusic.data.db

import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query

/** Timestamp journal commits in the same transaction as synchronized user data. */
@Entity(tableName = "sync_journal")
data class SyncJournalEntity(@PrimaryKey val id: Int = 1, val content: String)

@Dao
interface SyncJournalDao {
    @Query("SELECT content FROM sync_journal WHERE id = 1")
    suspend fun read(): String?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun write(journal: SyncJournalEntity)
}
