package com.tinyfish.jeekmusic.data.preference

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest
import java.util.UUID

/** Protocol v1, private SQLite revision ledger. No Room/library/settings tables are copied. */
class PreferenceStore(context: Context) : AutoCloseable {
    private val db = SQLiteDatabase.openOrCreateDatabase(File(context.noBackupFilesDir, "preferences.db"), null)
    val deviceId: String
    init {
        db.rawQuery("PRAGMA journal_mode=DELETE",null).use { it.moveToFirst() }; db.execSQL("PRAGMA synchronous=FULL"); db.rawQuery("PRAGMA secure_delete=ON",null).use { it.moveToFirst() }
        db.execSQL("ATTACH DATABASE ? AS evaluation", arrayOf(File(context.noBackupFilesDir, "preferences.db.evaluation").absolutePath))
        db.rawQuery("PRAGMA evaluation.journal_mode=DELETE",null).use { it.moveToFirst() }; db.execSQL("PRAGMA evaluation.synchronous=FULL"); db.rawQuery("PRAGMA evaluation.secure_delete=ON",null).use { it.moveToFirst() }
        require(db.version <= 2) { "Preference database requires a newer app" }
        if (db.version == 0) transaction {
            db.execSQL("CREATE TABLE events(id TEXT PRIMARY KEY,profile TEXT NOT NULL,entity TEXT NOT NULL,kind TEXT NOT NULL,json TEXT NOT NULL)")
            db.execSQL("CREATE INDEX events_entity ON events(profile,entity)")
            db.execSQL("CREATE TABLE evaluation.events(id TEXT PRIMARY KEY,profile TEXT NOT NULL,entity TEXT NOT NULL,kind TEXT NOT NULL,json TEXT NOT NULL)")
            db.execSQL("CREATE TABLE journal(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT NOT NULL UNIQUE,sealed INTEGER NOT NULL)")
            db.execSQL("CREATE TABLE local(key TEXT PRIMARY KEY,value TEXT NOT NULL)")
            db.execSQL("CREATE TABLE peers(id TEXT PRIMARY KEY,cursor INTEGER NOT NULL DEFAULT 0)")
            db.version = 1; db.execSQL("PRAGMA evaluation.user_version=1")
        }
        if(db.version<2) transaction {
            db.execSQL("ALTER TABLE events ADD COLUMN dimension TEXT NOT NULL DEFAULT 'MOOD'")
            db.execSQL("ALTER TABLE evaluation.events ADD COLUMN dimension TEXT NOT NULL DEFAULT 'MOOD'")
            db.execSQL("CREATE INDEX events_head ON events(profile,kind,dimension)"); db.version=2; db.execSQL("PRAGMA evaluation.user_version=2")
        }
        deviceId = local("deviceId") ?: UUID.randomUUID().toString().replace("-", "").also { setLocal("deviceId", it) }
    }
    private inline fun <T> transaction(block: () -> T): T {
        db.beginTransaction()
        try { return block().also { db.setTransactionSuccessful() } } finally { db.endTransaction() }
    }
    @Synchronized fun local(key: String): String? = db.rawQuery("SELECT value FROM local WHERE key=?", arrayOf(key)).use { if (it.moveToFirst()) it.getString(0) else null }
    @Synchronized fun setLocal(key: String, value: String) { db.execSQL("INSERT OR REPLACE INTO local VALUES(?,?)", arrayOf(key, value)) }
    @Synchronized fun read(profile: String, kind: String? = null, evaluation: Boolean = false): List<JSONObject> {
        val source = if (evaluation) "(SELECT * FROM events UNION ALL SELECT * FROM evaluation.events)" else "events"
        return db.rawQuery("SELECT json FROM $source WHERE profile=?" + if (kind == null) "" else " AND kind=?", if (kind == null) arrayOf(profile) else arrayOf(profile, kind)).use { c -> buildList { while (c.moveToNext()) add(JSONObject(c.getString(0))) } }
    }
    @Synchronized fun current(profile: String, kind: String, evaluation: Boolean = false) = read(profile, kind, evaluation).groupBy { it.getString("entityId") }.values.flatMap(::heads)
    @Synchronized fun profiles(): List<String> = db.rawQuery("SELECT DISTINCT profile FROM events WHERE kind='profile' ORDER BY profile", null).use { c -> buildList { while(c.moveToNext()) add(c.getString(0)) } }
    @Synchronized fun append(profile: String, entity: String, kind: String, payload: JSONObject, resolve: Boolean = false): JSONObject {
        val old = heads(read(profile, kind, true).filter { it.getString("entityId") == entity })
        require(old.size <= 1 || resolve) { "Concurrent revisions require explicit review" }
        val event = JSONObject().put("protocol", 2).put("eventId", UUID.randomUUID().toString().replace("-", "")).put("profileId", profile).put("entityId", entity).put("kind", kind)
            .put("deviceId", deviceId).put("revision", (old.maxOfOrNull { it.getLong("revision") } ?: 0) + 1).put("parents", JSONArray(old.map { it.getString("eventId") })).put("at", System.currentTimeMillis()).put("payload", JSONObject(payload.toString()))
        import(listOf(event)); return event
    }
    private fun existing(id: String): JSONObject? = db.rawQuery("SELECT json FROM events WHERE id=? UNION ALL SELECT json FROM evaluation.events WHERE id=?", arrayOf(id,id)).use { if(it.moveToFirst()) JSONObject(it.getString(0)) else null }
    @Synchronized fun import(events: List<JSONObject>): Int {
        require(events.size <= 100) { "At most 100 revisions per page" }; events.forEach(::validate)
        return transaction {
            var count = 0
            for (e in events) {
                val profile=e.getString("profileId")
                if(current(profile,"profile").any { it.getJSONObject("payload").optBoolean("deleted") } && !(e.getString("kind")=="profile" && e.getJSONObject("payload").optBoolean("deleted"))) continue
                val id = e.getString("eventId"); val old = existing(id)
                if (old != null) { require(canonical(old) == canonical(e)) { "Revision ID reused" }; continue }
                for (parent in strings(e.getJSONArray("parents"))) {
                    val p=existing(parent)
                    if(p==null) { require(e.getString("kind")=="profile" && e.getJSONObject("payload").optBoolean("deleted")) { "Missing ancestor; send parent first" }; continue }

                    require(p.getString("profileId") == e.getString("profileId") && p.getString("entityId") == e.getString("entityId") && p.getString("kind") == e.getString("kind") && p.getLong("revision") < e.getLong("revision")) { "Invalid revision ancestry" }
                }
                if(e.getString("kind")=="answer") for(previous in read(profile,"answer",true).filter { it.getString("entityId")==e.getString("entityId") }) for(key in listOf("role","contextId","recordingId","contentSignature")) require(previous.getJSONObject("payload").optString(key)==e.getJSONObject("payload").optString(key)) { "Immutable answer purpose/content" }
                val sealed = e.getString("kind") == "answer" && e.getJSONObject("payload").optString("role") in listOf("development", "sealed_test")
                val table = if(sealed) "evaluation.events" else "events"
                db.execSQL("INSERT INTO $table(id,profile,entity,kind,json,dimension) VALUES(?,?,?,?,?,?)", arrayOf(id,e.getString("profileId"),e.getString("entityId"),e.getString("kind"),e.toString(),dimension(e.getJSONObject("payload"))))
                db.execSQL("INSERT INTO journal(id,sealed) VALUES(?,?)", arrayOf<Any>(id,if(sealed) 1 else 0)); count++
                if(e.getString("kind")=="profile" && e.getJSONObject("payload").optBoolean("deleted")) {
                    val ids=read(profile,"recording").map { it.getString("entityId") }.distinct()
                    for(recording in ids) {
                        val others=db.rawQuery("SELECT COUNT(*) FROM events WHERE kind='recording' AND entity=? AND profile<>?",arrayOf(recording,profile)).use { it.moveToFirst(); it.getInt(0) }
                        if(others==0) for(prefix in listOf("vector:","binding:","signature:")) db.execSQL("DELETE FROM local WHERE key=?",arrayOf(prefix+recording))
                    }
                    db.execSQL("DELETE FROM journal WHERE id IN (SELECT id FROM events WHERE profile=? AND id<>? UNION ALL SELECT id FROM evaluation.events WHERE profile=?)",arrayOf(profile,id,profile))
                    db.execSQL("DELETE FROM events WHERE profile=? AND id<>?",arrayOf(profile,id)); db.execSQL("DELETE FROM evaluation.events WHERE profile=?",arrayOf(profile))
                    db.execSQL("DELETE FROM local WHERE key=? OR key LIKE ?",arrayOf("batch:$profile","batch:$profile:%"))
                }
            }
            if(count > 0) db.execSQL("DELETE FROM local WHERE key='validatedModel'")
            count
        }
    }
    @Synchronized fun page(after: Long, limit: Int = 100): JSONObject {
        require(after >= 0 && limit in 1..100)
        val high = db.rawQuery("SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='journal'),0)",null).use { it.moveToFirst(); it.getLong(0) }
        require(after <= high) { "Peer ledger reset requires cursor 0" }
        val events=JSONArray(); var next=after
        db.rawQuery("SELECT j.seq,COALESCE(a.json,b.json) FROM journal j LEFT JOIN events a ON a.id=j.id LEFT JOIN evaluation.events b ON b.id=j.id WHERE j.seq>? ORDER BY j.seq LIMIT ?",arrayOf(after.toString(),limit.toString())).use { c -> while(c.moveToNext()) { next=c.getLong(0); events.put(JSONObject(c.getString(1))) } }
        if(events.length()<limit) next=high
        return JSONObject().put("protocol",2).put("events",events).put("nextCursor",next).put("hasMore",next<high)
    }
    @Synchronized fun cursor(peer: String): Long = db.rawQuery("SELECT cursor FROM peers WHERE id=?",arrayOf(peer)).use { if(it.moveToFirst()) it.getLong(0) else 0 }
    @Synchronized fun accept(peer: String, page: JSONObject) {
        require(page.getInt("protocol") in 1..2 && page.getLong("nextCursor")>=0)
        import(objects(page.getJSONArray("events"))) // failure before cursor: idempotent retry after crash
        db.execSQL("INSERT INTO peers VALUES(?,?) ON CONFLICT(id) DO UPDATE SET cursor=MAX(cursor,excluded.cursor)",arrayOf<Any>(peer,page.getLong("nextCursor")))
    }
    @Synchronized override fun close() = db.close()
    companion object {
        const val CONTEXT = "general_workout_v1"
        val dimensions=listOf("MOOD","VOICE","GENRE","CALMNESS")
        fun dimension(p: JSONObject)=p.optString("dimension").ifEmpty { "MOOD" }
        fun context(d: String)=if(d=="MOOD") CONTEXT else d.lowercase()+"_personal_v1"
        fun labels(d: String)=when(d) { "MOOD"->"SPORT" to "NON_SPORT"; "VOICE"->"VOCAL" to "INSTRUMENTAL"; "GENRE"->"CLASSICAL" to "NON_CLASSICAL"; "CALMNESS"->"CALM" to "NON_CALM"; else->error("Unknown dimension") }
        val answers = listOf("SPORT","NON_SPORT","VOCAL","INSTRUMENTAL","CLASSICAL","NON_CLASSICAL","CALM","NON_CALM","UNCERTAIN","UNRESOLVED","SKIP","WITHDRAWN")
        val bases = listOf("whole_recording_familiar","listened_full","excerpts_only","unclear")
        fun hash(text: String) = MessageDigest.getInstance("SHA-256").digest(text.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
        fun strings(a: JSONArray) = List(a.length()) { a.getString(it) }
        fun objects(a: JSONArray) = List(a.length()) { a.getJSONObject(it) }
        fun heads(events: Collection<JSONObject>): List<JSONObject> {
            val superseded = events.flatMap { strings(it.getJSONArray("parents")) }.toSet()
            return events.filter { it.getString("eventId") !in superseded }.sortedBy { it.getString("eventId") }
        }
        fun canonical(v: Any?): String = when(v) { is JSONObject -> v.keys().asSequence().sorted().joinToString(",","{","}") { JSONObject.quote(it)+":"+canonical(v.get(it)) }; is JSONArray -> (0 until v.length()).joinToString(",","[","]") { canonical(v.get(it)) }; is String -> JSONObject.quote(v); null, JSONObject.NULL -> "null"; else -> v.toString() }
        fun validate(e: JSONObject) {
            require(e.getInt("protocol") in 1..2)
            for(k in listOf("eventId","profileId","entityId","deviceId")) require(e.getString(k).length in 1..160 && e.getString(k).none(Char::isISOControl))
            require(e.getString("kind") in listOf("profile","recording","manifest","answer","model") && e.getLong("revision")>0 && e.getLong("at")>0 && e.getJSONArray("parents").length()<=64 && e.toString().toByteArray(Charsets.UTF_8).size<=128000)
            require(strings(e.getJSONArray("parents")).all { it.length in 1..160 })
            val p=e.getJSONObject("payload")
            if(e.getString("kind")=="answer") require(p.getString("answer") in answers && p.getString("basis") in bases && p.optString("reason").length<=500 && p.getString("contextId")==context(dimension(p)) && dimension(p) in dimensions && (p.getString("answer") in listOf("UNCERTAIN","UNRESOLVED","SKIP","WITHDRAWN") || p.getString("answer") in listOf(labels(dimension(p)).first,labels(dimension(p)).second)) && p.getString("recordingId").length==64 && p.getString("contentSignature").length==64 && p.getString("manifestId").isNotEmpty() && p.getString("role") in listOf("training","calibration_regression","development","sealed_test"))
        }
    }
}
