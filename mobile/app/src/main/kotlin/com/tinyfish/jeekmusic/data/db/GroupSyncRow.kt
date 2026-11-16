package com.tinyfish.jeekmusic.data.db

import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey
import androidx.room.Query

/** Group journals share the business database and its transaction; payloads are never credentials. */
@Entity(tableName="sync_group_row",primaryKeys=["kind","key"],indices=[Index(value=["kind","cursor","key"])])
data class GroupSyncRowEntity(val kind: String,val key: String,val value: String,val cursor: Long)

@Dao
interface GroupSyncRowDao {
    @Query("SELECT value FROM sync_group_row WHERE kind=:kind AND `key`=:key")
    suspend fun read(kind: String,key: String): String?
}
