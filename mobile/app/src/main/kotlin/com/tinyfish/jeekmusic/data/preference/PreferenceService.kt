package com.tinyfish.jeekmusic.data.preference

import android.content.Context
import android.provider.MediaStore
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.CONTEXT
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.dimension
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.context
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.labels
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.dimensions
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.answers
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.bases
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.hash
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.objects
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.strings

@Singleton
class PreferenceService @Inject constructor(@param:ApplicationContext private val context: Context, private val library: JeekDatabase, private val lan: com.tinyfish.jeekmusic.data.sync.LanSyncStore, private val userData: com.tinyfish.jeekmusic.data.sync.SyncService) {
    // Lazy: adding the module does not touch storage or change settings-task startup behaviour.
    val store by lazy { PreferenceStore(context) }
    private var foregroundJob: kotlinx.coroutines.Job?=null
    @Volatile private var transport: com.tinyfish.jeekmusic.data.sync.LanProtocol?=null
    private val syncGate=kotlinx.coroutines.sync.Mutex()
    val syncState=MutableStateFlow("idle")
    fun onForeground(scope: kotlinx.coroutines.CoroutineScope) {
        if(foregroundJob?.isActive==true) return
        foregroundJob=scope.launch(Dispatchers.IO) {
            while(kotlinx.coroutines.currentCoroutineContext().isActive) {
                if(lan.read().has("connection")) try { synchronize() } catch(e: kotlinx.coroutines.CancellationException) { throw e } catch(_: Exception) { syncState.value="offline_or_older_peer; pending retry" }
                kotlinx.coroutines.delay(30000)
            }
        }
    }
    fun onBackground() { foregroundJob?.cancel(); transport?.cancel(); foregroundJob=null }
    private val _revision=MutableStateFlow(0)
    val revision=_revision.asStateFlow()
    val activeDimension get()=store.local("activeDimension") ?: "MOOD"
    private fun batchKey(p: String,d: String)="batch:"+p+(if(d=="MOOD") "" else ":$d")
    fun selectDimension(d: String) {
        require(d in dimensions); store.setLocal("activeDimension",d)
        val p=activeProfile
        if(p.isNotEmpty() && !deleted(p) && store.current(p,"manifest").none { dimension(it.getJSONObject("payload"))==d && it.getJSONObject("payload").has("items") }) {
            store.current(p,"manifest").filter { it.getJSONObject("payload").optString("role")=="training" && it.getJSONObject("payload").has("items") }.maxByOrNull { it.getLong("at") }?.let { source ->
                val id=UUID.randomUUID().toString().replace("-",""); val payload=JSONObject(source.getJSONObject("payload").toString()).put("dimension",d); payload.put("hash",hash(payload.toString())); store.append(p,id,"manifest",payload); store.setLocal(batchKey(p,d),id)
            }
        }
        notifyChanged()
    }
    val activeProfile get()=store.local("activeProfile").orEmpty()
    fun notifyChanged() { _revision.value++ }
    fun deleted(p: String)=store.current(p,"profile").any { it.getJSONObject("payload").optBoolean("deleted") }
    fun profiles()=store.profiles().map { id -> JSONObject().put("profileId",id).put("name",store.current(id,"profile").firstOrNull()?.getJSONObject("payload")?.optString("name").orEmpty()).put("deleted",deleted(id)) }
    fun create(name: String) {
        require(name.trim().length in 1..80)
        val id=UUID.randomUUID().toString().replace("-","")
        store.append(id,id,"profile",JSONObject().put("name",name.trim()).put("contextId",CONTEXT).put("deleted",false)); select(id)
    }
    fun select(id: String) { require(id in store.profiles() && !deleted(id)); store.setLocal("activeProfile",id); notifyChanged() }
    private fun profile(): String=activeProfile.also { require(it.isNotEmpty() && !deleted(it)) { "Create or select a live profile" }; require(store.current(it,"profile").size==1) { "Profile conflict" } }
    fun status(): JSONObject {
        val p=activeProfile
        val answers=store.current(p,"answer").filter { dimension(it.getJSONObject("payload"))==activeDimension }.groupBy { it.getString("entityId") }
        return JSONObject().put("dimension",activeDimension).put("profileId",p).put("answered",answers.values.count { it.size==1 && it.single().getJSONObject("payload").getString("answer") !in listOf("SKIP","WITHDRAWN") })
            .put("conflicts",answers.values.count { it.size>1 }).put("modelEnabled",false)
            .put("training",store.current(p,"model").filter { dimension(it.getJSONObject("payload"))==activeDimension }.maxByOrNull { it.getLong("at") }?.getJSONObject("payload")?.optString("state") ?: "windows_cpu_required")
    }
    fun batch(): JSONObject {
        val p=profile()
        val manifests=store.current(p,"manifest").filter { dimension(it.getJSONObject("payload"))==activeDimension && it.getJSONObject("payload").optJSONArray("items")?.length() in 1..6 }
        val saved=store.local(batchKey(p,activeDimension))
        val manifest=manifests.singleOrNull { it.getString("entityId")==saved } ?: manifests.maxByOrNull { it.getLong("at") }
            ?: return JSONObject().put("items",JSONArray()).put("state","not_prepared")
        store.setLocal(batchKey(p,activeDimension),manifest.getString("entityId"))
        val records=store.current(p,"recording").groupBy { it.getString("entityId") }
        val answers=store.current(p,"answer",true).groupBy { it.getString("entityId") }
        val rows=JSONArray()
        for(id in strings(manifest.getJSONObject("payload").getJSONArray("items"))) {
            val r=records[id]?.singleOrNull()?.getJSONObject("payload") ?: continue
            val repeat=id in strings(manifest.getJSONObject("payload").optJSONArray("repeatIds") ?: JSONArray())
            val a=answers[if(repeat) hash("$p:${context(activeDimension)}:$id:repeat:"+manifest.getString("entityId")) else hash("$p:${context(activeDimension)}:$id")]
            rows.put(JSONObject().put("recordingId",id).put("title",r.optString("title")).put("artist",r.optString("artist")).put("album",r.optString("album")).put("durationMs",r.optLong("durationMs"))
                .put("answer",a?.singleOrNull()?.getJSONObject("payload")?.optString("answer").orEmpty()).put("conflict",(a?.size ?: 0)>1).put("contentSignature",r.getString("contentSignature")))
        }
        return JSONObject().put("manifestId",manifest.getString("entityId")).put("state",manifest.getJSONObject("payload").optString("state")).put("items",rows)
    }
    fun answer(id: String, answer: String, basis: String, reason: String, resolve: Boolean=false) {
        val p=profile(); require(answer in answers && basis in bases && reason.length<=500)
        val m=store.current(p,"manifest").first { dimension(it.getJSONObject("payload"))==activeDimension && it.getString("entityId")==batch().getString("manifestId") && id in strings(it.getJSONObject("payload").optJSONArray("items") ?: JSONArray()) }
        val r=store.current(p,"recording").single { it.getString("entityId")==id }.getJSONObject("payload")
        val payload=JSONObject().put("recordingId",id).put("contentSignature",r.getString("contentSignature")).put("contextId",context(activeDimension)).put("dimension",activeDimension).put("answer",answer).put("basis",basis).put("reason",reason)
            .put("source",if(store.current(p,"profile").any { it.getJSONObject("payload").optString("purpose")=="synthetic_validation" }) "synthetic_fixture" else "explicit_human_blind_batch").put("exposure",m.getJSONObject("payload").optString("exposure")).put("role",m.getJSONObject("payload").getString("role"))
            .put("manifestId",m.getString("entityId")).put("manifestHash",m.getJSONObject("payload").optString("hash"))
        val repeat=id in strings(m.getJSONObject("payload").optJSONArray("repeatIds") ?: JSONArray()); payload.put("repeat",repeat)
        val entity=if(repeat) hash("$p:${context(activeDimension)}:$id:repeat:"+m.getString("entityId")) else hash("$p:${context(activeDimension)}:$id")
        val revisesExisting=store.current(p,"answer",true).any { it.getString("entityId")==entity }
        store.append(p,entity,"answer",payload,resolve)
        if((m.getJSONObject("payload").getString("role")=="training" && !repeat) || answer=="WITHDRAWN" || revisesExisting) invalidateHead(p,activeDimension)
        notifyChanged()
    }
    private fun invalidateHead(p: String,d: String) {
        for(e in store.current(p,"model").filter { dimension(it.getJSONObject("payload"))==d && it.getJSONObject("payload").optString("state") in listOf("development_required","regression_required","independent_test_required","pass_research_gate") }) {
            val payload=JSONObject(e.getJSONObject("payload").toString()).put("state","invalidated").put("enabled",false); store.append(p,e.getString("entityId"),"model",payload,true)
        }
    }
    fun conflicts(id: String)=store.current(profile(),"answer").filter { dimension(it.getJSONObject("payload"))==activeDimension && it.getJSONObject("payload").getString("recordingId")==id }.map { e -> e.getJSONObject("payload").getString("answer")+" · "+e.getJSONObject("payload").getString("basis")+" · "+e.getString("deviceId").take(8) }
    fun delete() { val p=profile(); store.append(p,p,"profile",JSONObject().put("deleted",true).put("contextId",CONTEXT),true); notifyChanged() }
    fun export()=JSONObject().put("protocol",2).put("events",JSONArray(store.read(profile(),evaluation=true))).toString()
    suspend fun synchronize(includeUserData: Boolean=true)=withContext(Dispatchers.IO) { syncGate.withLock {
        syncState.value="syncing"
        val connection=lan.read().getJSONObject("connection")
        val peer="pc:"+connection.getString("pin")
        var upload=(store.local("upload:$peer") ?: "0").toLong()
        var pages=0
        com.tinyfish.jeekmusic.data.sync.LanProtocol(connection).use { client ->
            transport=client
            if(includeUserData) userData.acceptLan(client.call("state",userData.exportLan(false).put("includeFeatures",false)))
            do {
                kotlinx.coroutines.currentCoroutineContext().ensureActive()
                val page=store.page(upload)
                val reply=client.call("preference_delta",JSONObject().put("protocol",2).put("sender",store.deviceId).put("page",page).put("after",store.cursor(peer)))
                store.accept(peer,reply)
                upload=reply.getLong("acceptedCursor"); store.setLocal("upload:$peer",upload.toString())
                pages++
            } while((reply.getBoolean("hasMore") || page.getBoolean("hasMore")) && pages<100)
        }
        transport=null; syncState.value="synced"; notifyChanged()
    } }
    suspend fun trainingStatus()=withContext(Dispatchers.IO) {
        com.tinyfish.jeekmusic.data.sync.LanProtocol(lan.read().getJSONObject("connection")).use { it.call("preference_training",JSONObject().put("profileId",profile()).put("dimension",activeDimension)) }
    }
    private fun frozenBatch(p: String,study: JSONObject): JSONObject {
        val d=activeDimension
        require(d in strings(study.optJSONArray("supportedHeads") ?: org.json.JSONArray(if(study.optJSONObject("headProtocols")?.optInt("protocol")==2 && study.getJSONArray("training").length()==48 && study.getJSONArray("development").length()==12 && study.getJSONArray("sealed_test").length()==48) dimensions else listOf("MOOD")))) { "This compact study covers the other heads; workout requires its own larger independent-study budget" }
        val records=store.current(p,"recording")
        require(records.all { it.getJSONObject("payload").optBoolean("identityReviewed") }) { "Identity review changed; evaluation locked" }
        val parent=records.associate { it.getString("entityId") to it.getString("entityId") }.toMutableMap()
        fun root(id: String): String { var r=id; while(parent[r]!=r) r=parent.getValue(r); return r }
        val seen=mutableMapOf<String,String>()
        for(e in records) {
            val id=e.getString("entityId"); val r=e.getJSONObject("payload")
            val keys=listOf("content:"+r.optString("contentSignature"),"work:"+r.optString("workGroup"),"album:"+r.optString("albumGroup"))+strings(r.optJSONArray("artists") ?: JSONArray()).map { "artist:$it" }
            for(key in keys.filterNot { it.endsWith(":") }) { val other=seen[key]; if(other==null) seen[key]=id else parent[root(id)]=root(other) }
        }
        val sides=mutableMapOf<String,MutableSet<String>>()
        for(role in listOf("training","development","sealed_test")) for(id in strings(study.getJSONArray(role))) { require(parent.containsKey(id)); sides.getOrPut(root(id)) { mutableSetOf() }+=role }
        require(sides.values.none { it.size>1 }) { "Cross-dimension identity contamination; sealed evaluation invalid" }
        val models=store.current(p,"model").filter { dimension(it.getJSONObject("payload"))==d }
        require(models.none { it.getJSONObject("payload").optString("state") in listOf("validation_failed","inconclusive","regression_required","pass_research_gate") }) { "Finish the frozen candidate gate on PC before another stage" }
        val role=if(models.any { it.getJSONObject("payload").optString("state")=="independent_test_required" }) "sealed_test" else if(models.any { it.getJSONObject("payload").optString("state")=="development_required" }) "development" else "training"
        val manifests=store.current(p,"manifest").filter { dimension(it.getJSONObject("payload"))==d }
        fun save(role: String,ids: List<String>,repeats: List<String> = emptyList(),tag: String=""): JSONObject {
            val id=UUID.randomUUID().toString().replace("-","")
            val payload=JSONObject().put("dimension",d).put("protocolVersion","personal-tags-v2-batch").put("studyHash",study.getString("hash")).put("role",role).put("items",JSONArray(ids)).put("repeatIds",JSONArray(repeats)).put("repeatTag",tag).put("state","frozen_$role").put("independent",role!="training").put("exposure","blind_ui_no_predictions")
            payload.put("hash",hash(payload.toString())); store.append(p,id,"manifest",payload); store.setLocal(batchKey(p,d),id); notifyChanged(); return batch()
        }
        if(d=="MOOD") for(repeatRole in listOf("training","development")) {
            val count=manifests.filter { it.getJSONObject("payload").optString("role")==repeatRole }.flatMap { strings(it.getJSONObject("payload").optJSONArray("items") ?: JSONArray()) }.distinct().size
            for(threshold in if(repeatRole=="training") listOf(12,24,36,48) else listOf(6,12)) {
                val tag="$repeatRole-$threshold"
                if(count<threshold || manifests.any { it.getJSONObject("payload").optString("repeatTag")==tag }) continue
                val id=strings(study.getJSONArray(repeatRole)).take(threshold).minBy { hash(study.getString("seed")+":repeat:"+tag+":"+it) }
                return save(repeatRole,listOf(id),listOf(id),tag)
            }
        }
        val all=strings(study.getJSONArray(role)).take(if(d=="MOOD") Int.MAX_VALUE else if(role=="training") 36 else if(role=="development") 8 else 24)
        val attempted=manifests.filter { it.getJSONObject("payload").optString("role")==role }.flatMap { strings(it.getJSONObject("payload").optJSONArray("items") ?: JSONArray()) }.toSet()
        val nodes=if(d=="MOOD") listOf(24,36,48) else listOf(12,24,36)
        if(role=="training" && attempted.size in nodes) require(models.any { it.getJSONObject("payload").optString("state")=="training_node_failed" && it.getJSONObject("payload").optInt("attempted")==attempted.size }) { "Run the fixed training node on PC before more questions" }
        val node=if(role=="training") nodes.firstOrNull { it>attempted.size } ?: all.size else all.size
        val next=all.filterNot { it in attempted }.take(minOf(6,node-attempted.size))
        require(next.isNotEmpty()) { "Frozen stage budget exhausted; no replacement of uncertain/withdrawn examples" }
        return save(role,next)
    }
    /** Explicit bounded reconciliation. Metadata only shortlists; complete bytes decide identity. */
    suspend fun matchLocalBatch()=withContext(Dispatchers.IO) {
        kotlinx.coroutines.withTimeout(60000) {
            val p=profile(); val b=batch(); val manifest=b.getString("manifestId")
            val ids=objects(b.getJSONArray("items")).map { it.getString("recordingId") }.toSet()
            val records=store.current(p,"recording").filter { it.getString("entityId") in ids }.map { it.getJSONObject("payload") }
            val candidates=library.trackDao().all().filter { t -> t.cueTrackNo==null && records.any { kotlin.math.abs(it.optLong("durationMs")-t.durationMs)<=1000 } }.sortedBy { hash("identity-match-v2:"+it.trackKey) }
            var offset=store.local("matchCursor:$manifest")?.toIntOrNull() ?: 0
            if(offset>=candidates.size) offset=0
            var checked=0; var matched=0
            for(t in candidates.drop(offset).take(6)) {
                kotlinx.coroutines.currentCoroutineContext().ensureActive(); checked++
                val uri=MediaStore.Audio.Media.getContentUri(t.volumeName,t.mediaStoreId)
                val signature=context.contentResolver.openInputStream(uri)?.use { input ->
                    val digest=MessageDigest.getInstance("SHA-256"); val buffer=ByteArray(1024*1024)
                    while(true) { kotlinx.coroutines.currentCoroutineContext().ensureActive(); val n=input.read(buffer); if(n<0) break; digest.update(buffer,0,n) }
                    digest.digest().joinToString("") { "%02x".format(it) }
                } ?: continue
                val id=hash("sha256:$signature:whole")
                if(id !in ids || records.none { it.optString("recordingId")==id && it.optString("contentSignature")==signature }) continue
                store.setLocal("binding:$id",t.trackKey); store.setLocal("bindingVerified:$id",System.currentTimeMillis().toString()); matched++
            }
            store.setLocal("matchCursor:$manifest",(offset+checked).toString()); notifyChanged()
            JSONObject().put("checkedFiles",checked).put("matched",matched).put("hasMoreCandidates",offset+checked<candidates.size).put("bindingMethod","exact_full_content_sha256_never_title")
        }
    }
    suspend fun prepare()=withContext(Dispatchers.IO) {
        val p=profile(); val old=batch()
        if(objects(old.getJSONArray("items")).any { it.optString("answer").isEmpty() || it.optBoolean("conflict") }) return@withContext old
        store.current(p,"manifest").singleOrNull { it.getString("entityId")=="study-v1" }?.let { return@withContext frozenBatch(p,it.getJSONObject("payload")) }
        val attempted=store.current(p,"manifest").filter { dimension(it.getJSONObject("payload"))==activeDimension }.flatMap { strings(it.getJSONObject("payload").optJSONArray("items") ?: JSONArray()) }.toSet()
        require(attempted.size<(if(activeDimension=="MOOD") 48 else 36)) { "Training attempt budget reached" }
        val ids=mutableListOf<String>()
        for(t in library.trackDao().all().filter { it.cueTrackNo==null && it.durationMs>=150000 && !it.album.isNullOrBlank() && it.artistDisplay.isNotBlank() }.sortedBy { hash("personal-workout-v1:"+it.trackKey) }) {
            val uri=MediaStore.Audio.Media.getContentUri(t.volumeName,t.mediaStoreId)
            val content=runCatching { context.contentResolver.openInputStream(uri)?.use { input ->
                val digest=MessageDigest.getInstance("SHA-256"); val buffer=ByteArray(1024*1024)
                while(true) { val n=input.read(buffer); if(n<0) break; digest.update(buffer,0,n) }; digest.digest().joinToString("") { "%02x".format(it) }
            } }.getOrNull() ?: continue
            val id=hash("sha256:$content:whole"); if(id in attempted || id in ids) continue
            if(store.current(p,"recording").none { it.getString("entityId")==id }) store.append(p,id,"recording",JSONObject().put("recordingId",id).put("contentSignature",content).put("title",t.title).put("artist",t.artistDisplay).put("album",t.album)
                .put("artists",JSONArray(listOf(t.artistDisplay))).put("workGroup",t.title).put("albumGroup",t.album).put("durationMs",t.durationMs).put("identityReviewed",false).put("exposed",false).put("supported",true))
            store.setLocal("binding:$id",t.trackKey); ids+=id; if(ids.size==6) break
        }
        require(ids.isNotEmpty()) { "No supported new recordings" }
        val id=UUID.randomUUID().toString().replace("-","")
        val payload=JSONObject().put("dimension",activeDimension).put("protocolVersion","v2-operational-pilot").put("seed","personal-workout-v1").put("items",JSONArray(ids)).put("role","training").put("state","identity_incomplete_pilot").put("independent",false).put("exposure","blind_ui_no_predictions")
        payload.put("hash",hash(payload.toString())); store.append(p,id,"manifest",payload); store.setLocal(batchKey(p,activeDimension),id); notifyChanged(); batch()
    }
}
