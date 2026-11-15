package com.tinyfish.jeekmusic.data.scanner

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

internal data class LocalIndexSnapshot(val root: String, val selection: List<String>, val rows: Map<String, String>, val revision: Long, val confirmed: Long = -1)

/** Current inventory and outstanding hints only, never an edit history. Independent of
 * the reconciler's scratch tables, which can be reset by a remote checkpoint change. */
internal class LocalIndexStore(context: Context) {
    private val file = File(context.noBackupFilesDir, "local-file-index.db")
    private val db by lazy {
        SQLiteDatabase.openOrCreateDatabase(file, null).apply {
            execSQL("PRAGMA synchronous=FULL")
            execSQL("CREATE TABLE IF NOT EXISTS state(id INTEGER PRIMARY KEY, sequence INTEGER)")
            execSQL("INSERT OR IGNORE INTO state VALUES(1,0)")
            execSQL("CREATE TABLE IF NOT EXISTS dirty(path TEXT PRIMARY KEY, sequence INTEGER)")
            execSQL("CREATE TABLE IF NOT EXISTS inventory(id TEXT PRIMARY KEY, root TEXT, selection TEXT, revision INTEGER, confirmed INTEGER)")
            execSQL("CREATE TABLE IF NOT EXISTS files(id TEXT, path TEXT, json TEXT, PRIMARY KEY(id,path))")
            execSQL("CREATE TABLE IF NOT EXISTS media(id TEXT PRIMARY KEY, json TEXT)")
        }
    }
    private fun transaction(block: () -> Unit) {
        db.beginTransaction()
        try { block(); db.setTransactionSuccessful() } finally { db.endTransaction() }
    }
    fun sequence(): Long = db.rawQuery("SELECT sequence FROM state WHERE id=1", null).use { it.moveToFirst(); it.getLong(0) }
    fun dirty(): Map<String, Long> = db.rawQuery("SELECT path,sequence FROM dirty", null).use { c -> buildMap { while(c.moveToNext()) put(c.getString(0),c.getLong(1)) } }
    fun minimumConfirmed(): Long? = db.rawQuery("SELECT MIN(confirmed) FROM inventory",null).use { it.moveToFirst();if(it.isNull(0))null else it.getLong(0) }
    fun mark(path: String): Long {
        val next = sequence() + 1
        transaction {
            db.execSQL("UPDATE state SET sequence=? WHERE id=1", arrayOf(next))
            db.execSQL("INSERT OR REPLACE INTO dirty VALUES(?,?)", arrayOf<Any>(path,next))
        }
        return next
    }
    fun load(id: String): LocalIndexSnapshot? = db.rawQuery("SELECT root,selection,revision,confirmed FROM inventory WHERE id=?", arrayOf(id)).use { c ->
        if(!c.moveToFirst()) null else {
            val selected = JSONArray(c.getString(1))
            val rows = db.rawQuery("SELECT path,json FROM files WHERE id=?", arrayOf(id)).use { files -> buildMap { while(files.moveToNext()) put(files.getString(0),files.getString(1)) } }
            LocalIndexSnapshot(c.getString(0),List(selected.length()) { selected.getString(it) },rows,c.getLong(2),c.getLong(3))
        }
    }
    fun save(id: String, snapshot: LocalIndexSnapshot, old: LocalIndexSnapshot?) = transaction {
        // A changed directory identity replaces the old inventory for this mapping.
        val prefix=id.substringBefore(':')+":"
        val retired=db.rawQuery("SELECT id FROM inventory WHERE id!=? AND substr(id,1,?)=?",arrayOf(id,prefix.length.toString(),prefix)).use { c -> buildList { while(c.moveToNext())add(c.getString(0)) } }
        for(key in retired) { db.execSQL("DELETE FROM files WHERE id=?",arrayOf(key));db.execSQL("DELETE FROM inventory WHERE id=?",arrayOf(key)) }
        if(old == null) db.execSQL("DELETE FROM files WHERE id=?",arrayOf(id))
        else for(path in old.rows.keys - snapshot.rows.keys) db.execSQL("DELETE FROM files WHERE id=? AND path=?",arrayOf(id,path))
        for((path,json) in snapshot.rows) if(old?.rows?.get(path) != json) db.execSQL("INSERT OR REPLACE INTO files VALUES(?,?,?)",arrayOf(id,path,json))
        db.execSQL("INSERT OR REPLACE INTO inventory VALUES(?,?,?,?,?)",arrayOf<Any>(id,snapshot.root,JSONArray(snapshot.selection).toString(),snapshot.revision,snapshot.confirmed))
        db.execSQL("DELETE FROM dirty WHERE sequence <= (SELECT MIN(confirmed) FROM inventory)")
    }
    fun invalidate() = transaction { db.execSQL("DELETE FROM inventory"); db.execSQL("DELETE FROM files"); db.execSQL("DELETE FROM media") }
    fun retainMappings(keys: Set<String>) = transaction {
        val retired=db.rawQuery("SELECT id FROM inventory",null).use { c -> buildList { while(c.moveToNext())if(c.getString(0).substringBefore(':') !in keys)add(c.getString(0)) } }
        for(key in retired) { db.execSQL("DELETE FROM files WHERE id=?",arrayOf(key));db.execSQL("DELETE FROM inventory WHERE id=?",arrayOf(key)) }
        val media=db.rawQuery("SELECT id FROM media",null).use { c -> buildList { while(c.moveToNext()) { val id=c.getString(0);if(!id.startsWith("library:") && id.substringBefore(':') !in keys)add(id) } } }
        for(key in media)db.execSQL("DELETE FROM media WHERE id=?",arrayOf(key))
    }
    fun media(id: String): JSONObject? = db.rawQuery("SELECT json FROM media WHERE id=?",arrayOf(id)).use { if(it.moveToFirst()) JSONObject(it.getString(0)) else null }
    fun media(id: String, value: JSONObject) { db.execSQL("INSERT OR REPLACE INTO media VALUES(?,?)",arrayOf(id,value.toString())) }
}
