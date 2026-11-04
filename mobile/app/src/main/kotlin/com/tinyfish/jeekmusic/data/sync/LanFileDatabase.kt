package com.tinyfish.jeekmusic.data.sync

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import dagger.hilt.android.qualifiers.ApplicationContext
import org.json.JSONObject
import java.io.File
import java.text.Normalizer
import java.util.Locale
import javax.inject.Inject
import javax.inject.Singleton

/** Private paged scratch snapshots plus durable transaction journals; never contains a token. */
@Singleton
class LanFileDatabase @Inject constructor(@ApplicationContext context: Context): java.io.Closeable {
    private var backupTotal: Long?=null
    private var stageTotal: Long?=null
    private fun backupSize(row: JSONObject?)=if(row?.has("backupIdentity")==true)row.optLong("backupBytes") else 0L
    private fun stageSize(row: JSONObject?)=if(row?.optString("state") in setOf("prepared","old_moved","copying") && (row?.has("stageIdentity")==true || row?.has("targetIdentity")==true))row.optLong("size") else 0L
    private val file=File(context.noBackupFilesDir,"lan-file-state.db")
    private val db by lazy {
        SQLiteDatabase.openOrCreateDatabase(file,null).apply {
            rawQuery("PRAGMA journal_mode=DELETE",null).use { it.moveToFirst() };execSQL("PRAGMA synchronous=FULL");execSQL("PRAGMA cache_size=-8192");rawQuery("PRAGMA max_page_count=262144",null).use { it.moveToFirst() }
            execSQL("CREATE TABLE IF NOT EXISTS entries(mapping TEXT,side TEXT,path TEXT,normalized TEXT,json TEXT,PRIMARY KEY(mapping,side,path),UNIQUE(mapping,side,normalized))")
            execSQL("CREATE TABLE IF NOT EXISTS dependency_baseline(mapping TEXT,path TEXT,json TEXT,PRIMARY KEY(mapping,path))")
            execSQL("CREATE TABLE IF NOT EXISTS baseline(mapping TEXT,path TEXT,json TEXT,PRIMARY KEY(mapping,path))")
            execSQL("CREATE TABLE IF NOT EXISTS transactions(operation TEXT PRIMARY KEY,mapping TEXT,path TEXT,scope TEXT,json TEXT)")
            execSQL("CREATE INDEX IF NOT EXISTS transaction_path ON transactions(mapping,path,scope)")
            execSQL("CREATE TABLE IF NOT EXISTS held(mapping TEXT,path TEXT,PRIMARY KEY(mapping,path))")
            execSQL("CREATE TABLE IF NOT EXISTS refresh(mapping TEXT,path TEXT,json TEXT,PRIMARY KEY(mapping,path))")
            execSQL("CREATE TABLE IF NOT EXISTS cue_bindings(mapping TEXT,audio TEXT,cue TEXT,PRIMARY KEY(mapping,audio,cue))")
            execSQL("CREATE INDEX IF NOT EXISTS cue_sheet ON cue_bindings(mapping,cue)")
            execSQL("CREATE TABLE IF NOT EXISTS issues(id TEXT UNIQUE,mapping TEXT,path TEXT,json TEXT,choice TEXT,PRIMARY KEY(mapping,path))")
        }
    }
    @Synchronized fun resetSnapshots(mapping: String) {
        db.execSQL("DELETE FROM held WHERE mapping=?",arrayOf(mapping));db.execSQL("DELETE FROM entries WHERE mapping=?",arrayOf(mapping));db.execSQL("DELETE FROM baseline WHERE mapping=?",arrayOf(mapping))
    }
    @Synchronized fun entry(mapping: String,side: String,row: JSONObject) {
        val path=LanProtocol.relative(row.getString("path"))
        val normalized=Normalizer.normalize(path,Normalizer.Form.NFC).lowercase(Locale.ROOT)
        db.rawQuery("SELECT path FROM entries WHERE mapping=? AND side=? AND normalized=?",arrayOf(mapping,side,normalized)).use { c ->
            require(!c.moveToFirst() || c.getString(0)==path) { "Case or Unicode file collision" }
        }
        db.execSQL("INSERT OR REPLACE INTO entries VALUES(?,?,?,?,?)",arrayOf(mapping,side,path,normalized,row.toString()))
    }
    @Synchronized fun baseline(mapping: String,row: JSONObject) {
        val path=LanProtocol.relative(row.getString("path"))
        db.execSQL("INSERT OR REPLACE INTO baseline VALUES(?,?,?)",arrayOf(mapping,path,row.toString()))
    }
    @Synchronized fun baseline(mapping: String,path: String): JSONObject? = one("SELECT json FROM baseline WHERE mapping=? AND path=?",arrayOf(mapping,path)) ?: one("SELECT json FROM dependency_baseline WHERE mapping=? AND path=?",arrayOf(mapping,path))
    @Synchronized fun dependencyBaseline(mapping: String,path: String,local: String,remote: String) {
        val row=JSONObject().put("path",path).put("version","dependency").put("comparisonProtocol",2).put("localVersion",local).put("remoteVersion",remote)
        db.execSQL("INSERT OR REPLACE INTO dependency_baseline VALUES(?,?,?)",arrayOf(mapping,path,row.toString()))
    }
    @Synchronized fun entry(mapping: String,side: String,path: String): JSONObject? = one("SELECT json FROM entries WHERE mapping=? AND side=? AND path=?",arrayOf(mapping,side,path))
    @Synchronized fun paths(mapping: String,offset: Int): List<String> = db.rawQuery("SELECT path FROM entries WHERE mapping=? UNION SELECT path FROM baseline WHERE mapping=? UNION SELECT path FROM issues WHERE mapping=? ORDER BY path LIMIT 200 OFFSET ?",arrayOf(mapping,mapping,mapping,offset.toString())).use { c ->
        buildList { while(c.moveToNext())add(c.getString(0)) }
    }
    @Synchronized fun paths(mapping: String,offset: Int,cues: Boolean): List<String> = db.rawQuery("SELECT path FROM (SELECT path FROM entries WHERE mapping=? UNION SELECT path FROM baseline WHERE mapping=? UNION SELECT path FROM issues WHERE mapping=?) WHERE lower(substr(path,-4)) ${if(cues) "=" else "!="} '.cue' ORDER BY path LIMIT 200 OFFSET ?",arrayOf(mapping,mapping,mapping,offset.toString())).use { c -> buildList { while(c.moveToNext())add(c.getString(0)) } }
    @Synchronized fun requirePortableLayout(mapping: String) {
        db.rawQuery("SELECT normalized FROM entries WHERE mapping=? GROUP BY normalized HAVING MIN(path)!=MAX(path) LIMIT 1",arrayOf(mapping)).use { check(!it.moveToFirst()) { "Case or Unicode layout conflict; align the filenames before syncing" } }
    }
    @Synchronized fun hold(mapping: String,path: String) { db.execSQL("INSERT OR IGNORE INTO held VALUES(?,?)",arrayOf(mapping,path)) }
    @Synchronized fun held(mapping: String,path: String)=db.rawQuery("SELECT 1 FROM held WHERE mapping=? AND path=?",arrayOf(mapping,path)).use { it.moveToFirst() }
    @Volatile internal var onIssuesChanged: (() -> Unit)? = null
    @Synchronized fun issue(row: JSONObject) {
        val previous=one("SELECT json FROM issues WHERE mapping=? AND path=?",arrayOf(row.getString("mapping"),row.getString("path")))
        if(previous?.optString("id")==row.getString("id"))return
        db.execSQL("INSERT OR REPLACE INTO issues VALUES(?,?,?,?,NULL)",arrayOf(row.getString("id"),row.getString("mapping"),row.getString("path"),row.toString()))
        onIssuesChanged?.invoke()
    }
    @Synchronized fun issue(id: String): JSONObject? = db.rawQuery("SELECT json,choice FROM issues WHERE id=?",arrayOf(id)).use { c -> if(c.moveToFirst())JSONObject(c.getString(0)).also { if(!c.isNull(1))it.put("choice",c.getString(1)) } else null }
    @Synchronized fun choose(id: String,choice: String) { require(choice in setOf("local","remote"));check(issue(id)!=null);db.execSQL("UPDATE issues SET choice=? WHERE id=?",arrayOf(choice,id)) }
    @Synchronized fun queueChoice(id: String,choice: String) {
        require(choice in setOf("local","remote"))
        val row=issue(id) ?: error("Conflict changed")
        val queued=row.optString("queuedChoice")
        check(row.optString("choice") !in setOf("local","remote") || queued==choice) { "Conflict already chosen" }
        check(queued.isEmpty() || queued==choice) { "Conflict already queued" }
        row.remove("choice");row.put("queuedChoice",choice)
        db.execSQL("UPDATE issues SET json=?,choice=? WHERE id=?",arrayOf(row.toString(),choice,id))
        onIssuesChanged?.invoke()
    }
    @Synchronized fun updateIssue(row: JSONObject) {
        // A user may queue a choice while the reconciler is awaiting the server.
        // Preserve it until a server-confirmed choice arrives; never lose that race.
        val existing=one("SELECT json FROM issues WHERE id=?",arrayOf(row.getString("id"))) ?: return
        val queued=existing.optString("queuedChoice")
        // A delayed snapshot must not undo a server-confirmed first-writer choice.
        if(existing.optString("choice") in setOf("local","remote"))row.put("choice",existing.getString("choice"))
        val confirmed=row.optString("choice") in setOf("local","remote")
        if(!confirmed && queued in setOf("local","remote"))row.put("queuedChoice",queued) else row.remove("queuedChoice")
        db.execSQL("UPDATE issues SET json=?,choice=? WHERE id=?",arrayOf(row.toString(),if(!confirmed && queued.isNotEmpty())queued else null,row.getString("id")))
        if(existing.toString()!=row.toString())onIssuesChanged?.invoke()
    }
    @Synchronized fun issueForPath(mapping: String,path: String): JSONObject? = one("SELECT json FROM issues WHERE mapping=? AND path=?",arrayOf(mapping,path))
    @Synchronized fun issuePage(offset: Int=0): List<JSONObject> = db.rawQuery("SELECT json FROM issues ORDER BY mapping,path LIMIT 100 OFFSET ?",arrayOf(offset.toString())).use { c -> buildList { while(c.moveToNext())add(JSONObject(c.getString(0))) } }
    @Synchronized fun issueCount(): Int = db.rawQuery("SELECT COUNT(*) FROM issues",null).use { it.moveToFirst();it.getInt(0) }
    @Synchronized fun resolved(mapping: String,path: String) { if(db.delete("issues","mapping=? AND path=?",arrayOf(mapping,path))>0)onIssuesChanged?.invoke() }
    @Synchronized fun issues(mapping: String): List<JSONObject> = db.rawQuery("SELECT json FROM issues WHERE mapping=? ORDER BY path LIMIT 100",arrayOf(mapping)).use { c -> buildList { while(c.moveToNext())add(JSONObject(c.getString(0))) } }
    @Synchronized fun transaction(mapping: String,path: String,scope: String): JSONObject? = one("SELECT json FROM transactions WHERE mapping=? AND path=? AND scope=? ORDER BY rowid DESC LIMIT 1",arrayOf(mapping,path,scope))
    @Synchronized fun transaction(row: JSONObject) {
        val existing=one("SELECT json FROM transactions WHERE operation=?",arrayOf(row.getString("operation")))
        if(existing==null) {
            val count=db.rawQuery("SELECT COUNT(*) FROM transactions",null).use { it.moveToFirst();it.getInt(0) }
            check(count<250000) { "File transaction history budget reached; retained backups need review" }
        }
        db.execSQL("INSERT OR REPLACE INTO transactions VALUES(?,?,?,?,?)",arrayOf(row.getString("operation"),row.getString("mapping"),row.getString("path"),row.getString("scope"),row.toString()))
        backupTotal=backupTotal?.let { Math.addExact(it-backupSize(existing),backupSize(row)) }
        stageTotal=stageTotal?.let { Math.addExact(it-stageSize(existing),stageSize(row)) }
    }
    @Synchronized fun transactions(mapping: String,offset: Int=0): List<JSONObject> = db.rawQuery("SELECT json FROM transactions WHERE mapping=? ORDER BY operation LIMIT 200 OFFSET ?",arrayOf(mapping,offset.toString())).use { c -> buildList { while(c.moveToNext())add(JSONObject(c.getString(0))) } }
    @Synchronized fun backupRows(mapping: String): List<JSONObject> = db.rawQuery("SELECT json FROM transactions WHERE mapping=? AND json_type(json,'$.backupIdentity')='array' ORDER BY operation LIMIT 100",arrayOf(mapping)).use { c -> buildList { while(c.moveToNext())add(JSONObject(c.getString(0))) } }
    @Synchronized fun receipt(operation: String): JSONObject?=one("SELECT json FROM transactions WHERE operation=?",arrayOf(operation))
    @Synchronized fun pendingPaths(mapping: String): List<String> = db.rawQuery("SELECT path FROM transactions WHERE mapping=? AND json_extract(json,'$.state') IN ('prepared','old_moved','vault_prepared','vault_old_moved','copying') LIMIT 4097",arrayOf(mapping)).use { c -> buildList { while(c.moveToNext()) { check(size<4096) { "Pending file work budget exceeded" };add(c.getString(0)) } } }
    @Synchronized fun backupBytes(): Long { if(backupTotal==null)backupTotal=db.rawQuery("SELECT json FROM transactions",null).use { c -> var size=0L;while(c.moveToNext())size=Math.addExact(size,backupSize(JSONObject(c.getString(0))));size };return backupTotal!! }
    @Synchronized fun stagingBytes(excluding: String): Long { if(stageTotal==null)stageTotal=db.rawQuery("SELECT json FROM transactions",null).use { c -> var size=0L;while(c.moveToNext())size=Math.addExact(size,stageSize(JSONObject(c.getString(0))));size };return stageTotal!!-stageSize(one("SELECT json FROM transactions WHERE operation=?",arrayOf(excluding))) }
    @Synchronized fun confirmed(row: JSONObject) {
        if(row.has("backupIdentity"))transaction(row.put("state","acknowledged")) else { transaction(row.put("state","acknowledged"));db.execSQL("DELETE FROM transactions WHERE operation=?",arrayOf(row.getString("operation"))) }
    }
    @Synchronized fun queueRefresh(mapping: String,path: String,row: JSONObject) { db.execSQL("INSERT OR REPLACE INTO refresh VALUES(?,?,?)",arrayOf(mapping,path,row.toString())) }
    @Synchronized fun refreshPaths(mapping: String): List<String> = db.rawQuery("SELECT path FROM refresh WHERE mapping=? ORDER BY path LIMIT 200",arrayOf(mapping)).use { c -> buildList { while(c.moveToNext())add(c.getString(0)) } }
    @Synchronized fun refresh(mapping: String,path: String): JSONObject? = one("SELECT json FROM refresh WHERE mapping=? AND path=?",arrayOf(mapping,path))
    @Synchronized fun refreshed(mapping: String,path: String) { db.execSQL("DELETE FROM refresh WHERE mapping=? AND path=?",arrayOf(mapping,path)) }
    @Synchronized fun cueAudio(mapping: String,cue: String): List<String> = db.rawQuery("SELECT audio FROM cue_bindings WHERE mapping=? AND cue=?",arrayOf(mapping,cue)).use { c -> buildList { while(c.moveToNext())add(c.getString(0)) } }
    @Synchronized fun cues(mapping: String,audio: String): List<String> = db.rawQuery("SELECT cue FROM cue_bindings WHERE mapping=? AND audio=?",arrayOf(mapping,audio)).use { c -> buildList { while(c.moveToNext())add(c.getString(0)) } }
    @Synchronized fun bindCue(mapping: String,cue: String,audio: List<String>) {
        require(audio.size<=1000);db.beginTransaction()
        try { db.execSQL("DELETE FROM cue_bindings WHERE mapping=? AND cue=?",arrayOf(mapping,cue));for(path in audio.distinct()) { LanProtocol.relative(path);db.execSQL("INSERT OR IGNORE INTO cue_bindings VALUES(?,?,?)",arrayOf(mapping,path,cue)) };db.setTransactionSuccessful() } finally { db.endTransaction() }
    }
    private fun one(sql: String,args: Array<String>): JSONObject? = db.rawQuery(sql,args).use { if(it.moveToFirst()) JSONObject(it.getString(0)) else null }
    @Synchronized override fun close()=db.close()
}
