package com.tinyfish.jeekmusic.data.sync

import com.tinyfish.jeekmusic.data.db.JeekDatabase
import org.json.JSONObject

/** Raw operations participate in the caller's Room transaction; never use a second database connection. */
internal class GroupSqlRows(private val database: JeekDatabase) : GroupRows {
    private val db get()=database.openHelper.writableDatabase
    override fun read(kind: String,key: String): String? = db.query("SELECT value FROM sync_group_row WHERE kind=? AND `key`=?",arrayOf(kind,key)).use { if(it.moveToFirst())it.getString(0) else null }
    private fun quota(rowDelta: Long,byteDelta: Long) {
        var stored=read("quota","global")
        if(stored==null) {
            val actual=db.query("SELECT COUNT(*),COALESCE(SUM(length(CAST(value AS BLOB))+length(CAST(kind AS BLOB))+length(CAST(`key` AS BLOB))),0) FROM sync_group_row WHERE kind<>'quota'").use { it.moveToFirst();it.getLong(0) to it.getLong(1) }
            stored=JSONObject().put("rows",actual.first).put("bytes",actual.second).toString()
            db.execSQL("INSERT INTO sync_group_row(kind,`key`,value,cursor) VALUES('quota','global',?,0)",arrayOf(stored))
        }
        val quota=JSONObject(stored)
        val count=quota.getLong("rows")+rowDelta;val bytes=quota.getLong("bytes")+byteDelta
        if(count !in 0..500_000L || bytes !in 0..256L*1024*1024)throw GroupSyncException("group_storage_budget")
        db.execSQL("UPDATE sync_group_row SET value=? WHERE kind='quota' AND `key`='global'",arrayOf(JSONObject().put("rows",count).put("bytes",bytes).toString()))
    }
    override fun put(kind: String,key: String,value: String,cursor: Long) {
        val bytes=value.toByteArray(Charsets.UTF_8).size
        if(kind=="quota" || bytes>2_000_000)throw GroupSyncException("row_budget")
        val old=read(kind,key)
        quota(if(old==null)1 else 0,bytes.toLong()-(old?.toByteArray(Charsets.UTF_8)?.size ?: 0)+(if(old==null)kind.toByteArray(Charsets.UTF_8).size+key.toByteArray(Charsets.UTF_8).size else 0))
        db.execSQL("INSERT INTO sync_group_row(kind,`key`,value,cursor) VALUES(?,?,?,?) ON CONFLICT(kind,`key`) DO UPDATE SET value=excluded.value,cursor=excluded.cursor",arrayOf<Any>(kind,key,value,cursor))
    }
    override fun delete(kind: String,key: String) {
        check(kind!="quota")
        val old=read(kind,key) ?: return;quota(-1,-old.toByteArray(Charsets.UTF_8).size.toLong()-kind.toByteArray(Charsets.UTF_8).size-key.toByteArray(Charsets.UTF_8).size)
        db.execSQL("DELETE FROM sync_group_row WHERE kind=? AND `key`=?",arrayOf(kind,key))
    }
    private fun readPage(sql: String,args: Array<Any>,limit: Int): List<GroupRow> {
        if(limit !in 1..201)throw GroupSyncException("batch_limit")
        val result=mutableListOf<GroupRow>();var bytes=0
        db.query(sql,args+limit).use { cursor -> while(cursor.moveToNext()) {
            val value=cursor.getString(1);val length=value.toByteArray(Charsets.UTF_8).size
            if(result.isNotEmpty() && bytes+length>1_048_576)break
            result+=GroupRow(cursor.getString(0),value,cursor.getLong(2));bytes+=length
        } }
        return result
    }
    override fun page(kind: String,after: Long,through: Long,limit: Int)=readPage(
        "SELECT `key`,value,cursor FROM sync_group_row WHERE kind=? AND cursor>? AND cursor<=? ORDER BY cursor,`key` LIMIT ?",arrayOf(kind,after,through),limit)
    override fun scan(kind: String,afterKey: String,limit: Int)=readPage(
        "SELECT `key`,value,cursor FROM sync_group_row WHERE kind=? AND `key`>? ORDER BY `key` LIMIT ?",arrayOf(kind,afterKey),limit)
    override fun count(kind: String)=db.query("SELECT COUNT(*) FROM sync_group_row WHERE kind=?",arrayOf(kind)).use { it.moveToFirst();it.getLong(0) }
}
