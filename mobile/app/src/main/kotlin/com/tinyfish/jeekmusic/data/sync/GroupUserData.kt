package com.tinyfish.jeekmusic.data.sync

import org.json.JSONArray
import org.json.JSONObject
import org.json.JSONTokener

data class GroupSeed(val payload: String?, val editedAtMs: Long = 0)

/** Register identities/values exclude legacy timestamps; absence is not a remote deletion. */
object GroupUserData {
    private fun invalid(): Nothing = throw GroupSyncException("invalid_user_data")
    fun tupleKey(kind: String, reference: String, suffix: String) = "$kind:${reference.length}:$reference:$suffix"
    private fun readTuple(key: String, kind: String): Pair<String, String> {
        var rest=key.substring(kind.length+1)
        val colon=rest.indexOf(':')
        if(colon<1)invalid()
        val length=rest.substring(0,colon).toIntOrNull() ?: invalid()
        if(length !in 1..2048)invalid()
        rest=rest.substring(colon+1)
        if(rest.length<=length || rest[length]!=':')invalid()
        return rest.substring(0,length) to rest.substring(length+1)
    }
    private fun objects(array: JSONArray?): List<JSONObject> = if(array==null)emptyList() else List(array.length()) { array.getJSONObject(it) }

    fun flatten(state: JSONObject): Map<String,GroupSeed> {
        val result=mutableMapOf<String,GroupSeed>()
        for(playlist in objects(state.optJSONArray("playlists"))) result["playlist:"+playlist.getString("uid")]=GroupSeed(canonical(JSONObject()
            .put("name",playlist.getString("name")).put("items",playlist.optJSONArray("items") ?: JSONArray())),playlist.optLong("updated_at"))
        for(deleted in objects(state.optJSONArray("deleted_playlists"))) {
            val key="playlist:"+deleted.getString("uid");val stamp=deleted.optLong("deleted_at")
            if(result[key]?.let { it.editedAtMs>stamp }!=true)result[key]=GroupSeed(null,stamp)
        }
        state.optJSONObject("tag_overrides")?.let { tags -> for(reference in tags.keys()) {
            val dimensions=tags.getJSONObject(reference)
            for(dimension in dimensions.keys()) {
                val entry=dimensions.getJSONObject(dimension)
                result[tupleKey("tag",reference,dimension)]=GroupSeed(if(entry.isNull("value"))null else canonical(entry.get("value")),entry.optLong("updated_at"))
            }
        } }
        state.optJSONObject("lyrics_offsets")?.let { offsets -> for(reference in offsets.keys()) {
            val entry=offsets.getJSONObject(reference)
            result["offset:$reference"]=GroupSeed(canonical(entry.get("offset_ms")),entry.optLong("updated_at"))
        } }
        state.optJSONObject("rules")?.let { result["rules"]=GroupSeed(canonical(it.opt("value")),it.optLong("updated_at")) }
        state.optJSONObject("nav")?.let { result["navigation"]=GroupSeed(canonical(it.opt("items")),it.optLong("updated_at")) }
        state.optJSONObject("vocal_languages")?.optJSONObject("items")?.let { languages -> for(reference in languages.keys()) {
            for(entry in objects(languages.getJSONArray(reference))) {
                val payload=JSONObject(entry.toString()).also { it.remove("updated_at") }
                result[tupleKey("language",reference,cacheIdentity(payload,true))]=GroupSeed(canonical(payload),entry.optLong("updated_at"))
            }
        } }
        for(entry in objects(state.optJSONArray("analysis_cache"))) {
            val payload=JSONObject(entry.toString()).also { it.remove("reference");it.remove("updated_at") }
            result[tupleKey("features",entry.getString("reference"),cacheIdentity(payload,false))]=GroupSeed(canonical(payload),entry.optLong("updated_at"))
        }
        return result
    }

    fun materialize(mutations: Iterable<GroupMutation>, applyStamp: Long): JSONObject {
        val state=JSONObject()
        fun objectAt(parent: JSONObject,key: String)=parent.optJSONObject(key) ?: JSONObject().also { parent.put(key,it) }
        fun arrayAt(key: String)=state.optJSONArray(key) ?: JSONArray().also { state.put(key,it) }
        for(mutation in mutations) {
            GroupSyncContract.validate(mutation)
            val key=mutation.key
            val value=mutation.payload?.let(GroupJson::parse)
            when {
                key.startsWith("playlist:") -> {
                    val uid=key.substring(9);if(uid.length !in 1..256)invalid()
                    if(mutation.payload==null)arrayAt("deleted_playlists").put(JSONObject().put("uid",uid).put("deleted_at",applyStamp))
                    else {
                        if(value !is JSONObject || value.opt("items") !is JSONArray || value.opt("name") !is String)invalid()
                        val items=value.getJSONArray("items")
                        if((0 until items.length()).any { items.get(it) !is String })invalid()
                        arrayAt("playlists").put(JSONObject().put("uid",uid).put("name",value.getString("name")).put("items",JSONArray(items.toString())).put("updated_at",applyStamp))
                    }
                }
                key.startsWith("tag:") -> {
                    val (reference,dimension)=readTuple(key,"tag")
                    if(dimension !in setOf("MOOD","VOICE","GENRE","CALMNESS") || (mutation.payload!=null && value !is String))invalid()
                    objectAt(objectAt(state,"tag_overrides"),reference).put(dimension,JSONObject().put("value",value ?: JSONObject.NULL).put("updated_at",applyStamp))
                }
                key.startsWith("offset:") -> {
                    val reference=key.substring(7);if(reference.isEmpty())invalid()
                    if(mutation.payload!=null && value !is Int && value !is Long)invalid()
                    val offset=(value as? Number)?.toLong() ?: 0L
                    if(offset !in Int.MIN_VALUE.toLong()..Int.MAX_VALUE.toLong())invalid()
                    objectAt(state,"lyrics_offsets").put(reference,JSONObject().put("offset_ms",offset).put("updated_at",applyStamp))
                }
                key=="rules" -> {
                    if(value !is JSONObject || value.opt("infer_from_file_name") !is Boolean || value.opt("file_name_format") !is String)invalid()
                    for(field in listOf("artist_separators","artist_exceptions")) {
                        val array=value.optJSONArray(field) ?: invalid()
                        if(array.length()>1000 || (0 until array.length()).any { array.get(it) !is String })invalid()
                    }
                    if(!value.isNull("legacy_charset") && value.opt("legacy_charset") !is String)invalid()
                    state.put("rules",JSONObject().put("value",value).put("updated_at",applyStamp))
                }
                key=="navigation" -> {
                    if(value !is JSONArray || value.length()>1000)invalid()
                    repeat(value.length()) { index ->
                        val item=value.optJSONObject(index) ?: invalid()
                        if(item.opt("type") !is String || (!item.isNull("ref") && item.opt("ref") !is String))invalid()
                    }
                    state.put("nav",JSONObject().put("items",value).put("updated_at",applyStamp))
                }
                isCache(key) -> {
                    val language=key.startsWith("language:");val (reference,suffix)=readTuple(key,if(language)"language" else "features")
                    if(value !is JSONObject || cacheIdentity(value,language)!=suffix)invalid()
                    if(language) {
                        val source=value.getString("source");val status=value.getString("state")
                        if(source !in listOf("AUTO","MANUAL") || status !in listOf("known","unknown","instrumental","auto") || (status=="auto" && source!="MANUAL"))invalid()
                        val codes=value.optJSONArray("languages") ?: invalid()
                        if(codes.length()>16 || (0 until codes.length()).any { codes.get(it) !is String || !codes.getString(it).matches(Regex("[a-z]{2,3}|other")) } ||
                            (0 until codes.length()).map { codes.getString(it) }.distinct().size!=codes.length() || (status=="known")!=(codes.length()>0))invalid()
                        if(!value.isNull("confidence")) { val number=value.get("confidence") as? Number ?: invalid();if(!number.toDouble().isFinite() || number.toDouble() !in 0.0..1.0)invalid() }
                        val envelope=objectAt(state,"vocal_languages").put("version",1)
                        val items=objectAt(envelope,"items");val rows=items.optJSONArray(reference) ?: JSONArray().also { items.put(reference,it) }
                        rows.put(JSONObject(value.toString()).put("updated_at",applyStamp))
                    } else {
                        if(value.opt("features") !is JSONObject)invalid()
                        arrayAt("analysis_cache").put(JSONObject(value.toString()).put("reference",reference).put("updated_at",applyStamp))
                    }
                }
                else -> invalid()
            }
        }
        return state
    }
    fun deletesWhenMissing(key: String)=key.startsWith("playlist:") || key.startsWith("tag:") || key.startsWith("offset:")
    fun isCache(key: String)=key.startsWith("language:") || key.startsWith("features:")
    private fun cacheIdentity(cache: JSONObject,language: Boolean): String {
        fun integer(key: String): Long { val value=cache.get(key);if(value !is Long && value !is Int)invalid();return (value as Number).toLong().also { if(it !in 0..GroupSyncContract.MAX_INTEGER)invalid() } }
        val prefix=if(language)cache.getString("source") else integer("analyzer_version").toString()
        return "$prefix:${integer("size")}:${integer("duration_ms")}:${integer("start_ms")}"
    }
    fun canonical(value: Any?): String = when(value) {
        null,JSONObject.NULL -> "null"
        is JSONObject -> value.keys().asSequence().sorted().joinToString(",","{","}") { quote(it)+":"+canonical(value.get(it)) }
        is JSONArray -> (0 until value.length()).joinToString(",","[","]") { canonical(value.get(it)) }
        is String -> quote(value)
        else -> value.toString()
    }
    private fun quote(value: String)=buildString {
        append('"')
        for(c in value) append(when(c) { '"'->"\\\"";'\\'->"\\\\";'\b'->"\\b";'\u000c'->"\\f";'\n'->"\\n";'\r'->"\\r";'\t'->"\\t";else->if(c<' ')"\\u"+c.code.toString(16).padStart(4,'0') else c.toString() })
        append('"')
    }
}
