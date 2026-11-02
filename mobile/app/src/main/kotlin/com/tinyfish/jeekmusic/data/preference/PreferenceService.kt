package com.tinyfish.jeekmusic.data.preference

import android.content.Context
import android.provider.MediaStore
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.currentCoroutineContext
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
class PreferenceService @Inject constructor(@param:ApplicationContext private val context: Context, private val library: JeekDatabase, private val lan: com.tinyfish.jeekmusic.data.sync.LanSyncStore, private val userData: com.tinyfish.jeekmusic.data.sync.SyncService, private val feedback: DailyLearningFeedback, private val tags: com.tinyfish.jeekmusic.analysis.TagRepository) {
    // Lazy: adding the module does not touch storage or change settings-task startup behaviour.
    val store get()=feedback.store
    private var foregroundJob: kotlinx.coroutines.Job?=null
    @Volatile private var transport: com.tinyfish.jeekmusic.data.sync.LanProtocol?=null
    private val syncGate=kotlinx.coroutines.sync.Mutex()
    val syncState=MutableStateFlow("idle")
    fun onForeground(scope: kotlinx.coroutines.CoroutineScope) {
        feedback.start()
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
    val revision=kotlinx.coroutines.flow.merge(_revision,feedback.revision)
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
    val activeProfile get()=PreferenceStore.DATASET
    fun notifyChanged() { _revision.value++ }
    fun deleted(p: String)=p!=PreferenceStore.DATASET
    private fun profile()=PreferenceStore.DATASET
    suspend fun status(): JSONObject {
        val p=activeProfile
        val answers=store.current(p,"answer").filter { dimension(it.getJSONObject("payload"))==activeDimension }.groupBy { it.getString("entityId") }
        val tracks=library.trackDao().all(); val results=feedback.personalResults()
        val matched=tracks.count { results[it.trackKey]?.optString("signature")=="${it.dateModified}:${it.size}" }
        return JSONObject().put("dimension",activeDimension).put("profileId",p).put("answered",answers.values.count { it.size==1 && it.single().getJSONObject("payload").getString("answer") !in listOf("SKIP","WITHDRAWN") })
            .put("matchedSongs",matched).put("fallbackSongs",tracks.size-matched).put("resultPagesPending",DesktopResultCursor(store).offset>0).put("dailyFeedback",feedback.status()).put("heads",localHeads()).put("desktop",store.local("desktop-learning")?.let(::JSONObject) ?: JSONObject()).put("conflicts",answers.values.count { it.size>1 }).put("modelEnabled",false).put("desktopResultsAvailable",matched>0)
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
                .put("answer",a?.singleOrNull()?.getJSONObject("payload")?.optString("answer").orEmpty()).put("basis",a?.singleOrNull()?.getJSONObject("payload")?.optString("basis").orEmpty()).put("reason",a?.singleOrNull()?.getJSONObject("payload")?.optString("reason").orEmpty()).put("conflict",(a?.size ?: 0)>1).put("contentSignature",r.getString("contentSignature")))
        }
        return JSONObject().put("manifestId",manifest.getString("entityId")).put("state",manifest.getJSONObject("payload").optString("state")).put("items",rows)
    }
    fun answer(id: String, answer: String, basis: String, reason: String, resolve: Boolean=false) {
        val p=profile(); val generation=store.generation; require(answer in answers && basis in bases && reason.length<=500)
        val m=store.current(p,"manifest").first { dimension(it.getJSONObject("payload"))==activeDimension && it.getString("entityId")==batch().getString("manifestId") && id in strings(it.getJSONObject("payload").optJSONArray("items") ?: JSONArray()) }
        val r=store.current(p,"recording").single { it.getString("entityId")==id }.getJSONObject("payload")
        val payload=JSONObject().put("recordingId",id).put("contentSignature",r.getString("contentSignature")).put("contextId",context(activeDimension)).put("dimension",activeDimension).put("answer",answer).put("basis",basis).put("reason",reason)
            .put("source",if(store.local("syntheticFixture")=="1") "synthetic_fixture" else "explicit_human_blind_batch").put("exposure",m.getJSONObject("payload").optString("exposure")).put("role",m.getJSONObject("payload").getString("role"))
            .put("manifestId",m.getString("entityId")).put("manifestHash",m.getJSONObject("payload").optString("hash"))
        val repeat=id in strings(m.getJSONObject("payload").optJSONArray("repeatIds") ?: JSONArray()); payload.put("repeat",repeat)
        val entity=if(repeat) hash("$p:${context(activeDimension)}:$id:repeat:"+m.getString("entityId")) else hash("$p:${context(activeDimension)}:$id")
        val revisesExisting=store.current(p,"answer",true).any { it.getString("entityId")==entity }
        store.append(p,entity,"answer",payload,resolve,expectedGeneration=generation)
        if((m.getJSONObject("payload").getString("role")=="training" && !repeat) || answer=="WITHDRAWN" || revisesExisting) invalidateHead(p,activeDimension)
        notifyChanged()
    }
    private fun invalidateHead(p: String,d: String) {
        for(e in store.current(p,"model").filter { dimension(it.getJSONObject("payload"))==d && it.getJSONObject("payload").optString("state") in listOf("development_required","regression_required","independent_test_required","pass_research_gate","self_selected_candidate","active") }) {
            val payload=JSONObject(e.getJSONObject("payload").toString()).put("state","invalidated").put("enabled",false); store.append(p,e.getString("entityId"),"model",payload,true)
        }
    }
    fun conflicts(id: String)=store.current(profile(),"answer").filter { dimension(it.getJSONObject("payload"))==activeDimension && it.getJSONObject("payload").getString("recordingId")==id }.map { e -> e.getJSONObject("payload").getString("answer")+" · "+e.getJSONObject("payload").getString("basis")+" · "+e.getString("deviceId").take(8) }
    suspend fun delete() { store.clearLearning(); tags.recomputeAll(); notifyChanged() }
    fun export()=JSONObject().put("protocol",PreferenceStore.VERSION).put("resetBoundary",JSONArray(store.resetBoundary)).put("generation",store.generation).put("events",JSONArray(store.read(profile(),evaluation=true))).toString()
    suspend fun synchronize(includeUserData: Boolean=true)=withContext(Dispatchers.IO) { syncGate.withLock {
        feedback.capture()
        syncState.value="syncing"
        val connection=lan.read().getJSONObject("connection")
        val peer="pc:"+connection.getString("pin")
        var upload=(store.local("upload:$peer") ?: "0").toLong()
        var pages=0
        com.tinyfish.jeekmusic.data.sync.LanProtocol(connection).use { client ->
            transport=client
            if (!client.call("capabilities").getJSONObject("permissions").optBoolean("writeSync")) {
                transport=null; syncState.value="read_only"; notifyChanged(); return@withLock
            }
            if(includeUserData) userData.acceptLan(client.call("state",userData.exportLan(false).put("includeFeatures",false)))
            do {
                kotlinx.coroutines.currentCoroutineContext().ensureActive()
                val page=store.page(upload)
                val reply=client.call("preference_delta",JSONObject().put("protocol",PreferenceStore.VERSION).put("sender",store.deviceId).put("page",page).put("after",store.cursor(peer)))
                store.accept(peer,reply)
                upload=reply.getLong("acceptedCursor"); store.setLocal("upload:$peer",upload.toString())
                pages++
            } while((reply.getBoolean("hasMore") || page.getBoolean("hasMore")) && pages<100)
        }
        transport=null; syncState.value="synced"; notifyChanged()
        // Old paired versions fail explicitly; labels already committed above remain available offline.
        try { synchronizeResults() } finally { tags.recomputeAll() }
    } }
    suspend fun trainingStatus()=learningAction("status")
    private fun localHeads(): JSONArray {
        val all=store.current(activeProfile,"answer")
        return JSONArray(dimensions.map { d ->
            var usable=all.filter { dimension(it.getJSONObject("payload"))==d && it.getJSONObject("payload").optString("role")=="training" }
                .groupBy { it.getJSONObject("payload").optString("recordingId") }.values.filter { group -> group.groupBy { it.getString("entityId") }.values.all { it.size==1 } }
                .map { group -> group.map { it.getJSONObject("payload") }.filter { it.optString("answer") in listOf(labels(d).first,labels(d).second) && ((it.optString("source")=="explicit_daily_tag_correction" && it.optString("basis")=="track_tag_feedback") || (it.optString("source")=="explicit_human_blind_batch" && it.optString("basis") in listOf("whole_recording_familiar","listened_full"))) } }
                .filter { rows -> rows.isNotEmpty() && rows.map { it.getString("answer") }.distinct().size==1 }.map { it.first() }
            val hasStudy=store.current(activeProfile,"manifest").any { it.getString("entityId")=="study-v1" }
            if(!hasStudy && usable.any { it.optString("source")=="explicit_daily_tag_correction" }) {
                usable=usable.filter { it.optString("source")=="explicit_daily_tag_correction" }.sortedBy { it.optString("recordingId") }
                val count=(if(d=="MOOD") listOf(24,36,48) else listOf(12,24,36)).filter { it<=usable.size }.maxOrNull() ?: usable.size
                usable=usable.take(count)
            }
            val positive=usable.count { it.getString("answer")==labels(d).first }; val negative=usable.size-positive
            val computer=store.local("desktop-head-summaries")?.let { objects(JSONArray(it)).singleOrNull { h -> h.optString("dimension")==d && h.optString("answerRevision")==feedback.answerRevision(d) } }
            val job=store.current(activeProfile,"model").filter { dimension(it.getJSONObject("payload"))==d && it.getJSONObject("payload").optString("category")=="job" }.maxByOrNull { it.getLong("at") }?.getJSONObject("payload")
            JSONObject().put("failure",LearningFailure.sanitize(computer?.optJSONObject("learning")?.optJSONObject("failure") ?: job?.optJSONObject("failure"))).put("dimension",d).put("annotated",all.filter { dimension(it.getJSONObject("payload"))==d }.groupBy { it.getString("entityId") }.values.count { it.size==1 && it.single().getJSONObject("payload").optString("answer") !in listOf("SKIP","WITHDRAWN") }).put("available",usable.size).put("positive",positive).put("negative",negative)
                .put("computerMaterial",computer?.optJSONObject("material"))
                .put("missingTotal",maxOf((if(d=="MOOD" && !hasStudy) 24 else if(d=="MOOD") 16 else 12)-usable.size,((if(d=="MOOD") 8 else 6)-positive).coerceAtLeast(0)+((if(d=="MOOD") 8 else 6)-negative).coerceAtLeast(0))).put("missingPositive",((if(d=="MOOD") 8 else 6)-positive).coerceAtLeast(0)).put("missingNegative",((if(d=="MOOD") 8 else 6)-negative).coerceAtLeast(0))
                .put("state",store.current(activeProfile,"model").filter { dimension(it.getJSONObject("payload"))==d && it.getJSONObject("payload").optString("category")=="job" }.maxByOrNull { it.getLong("at") }?.getJSONObject("payload")?.optString("state") ?: "idle")
        })
    }
    private suspend fun synchronizeResults() {
        val generation=store.generation
        com.tinyfish.jeekmusic.data.sync.LanProtocol(lan.read().getJSONObject("connection")).use { client ->
            val cursor=DesktopResultCursor(store); var pages=0
            do {
                currentCoroutineContext().ensureActive()
                val reply=try { client.call("preference_results",JSONObject().put("protocol",3).put("offset",cursor.offset).put("version",cursor.version)) }
                    catch(e: com.tinyfish.jeekmusic.data.sync.LanRemoteException) { cursor.reset(); throw e }
                val compatible=reply.getInt("resultProtocol")==1 && reply.getString("scope")=="sha256_whole_recording" && reply.getInt("analyzerVersion")==com.tinyfish.jeekmusic.analysis.TagRepository.ANALYZER_VERSION && reply.getInt("rulesVersion") in 9..com.tinyfish.jeekmusic.analysis.TagRepository.RULES_VERSION
                if(!compatible) { store.removeLocal("desktop-heads"); error("Recognition versions differ; update both devices") }
                require(reply.getString("generation")==generation && store.generation==generation) { "Learning changed during sync; retry" }
                for(row in objects(reply.getJSONArray("rows"))) {
                    val content=row.getString("contentSignature"); require(content.matches(Regex("[0-9a-f]{64}")))
                    store.setLocalForGeneration("desktop-content:$content",row.toString(),generation)
                }
                store.setLocalForGeneration("desktop-heads",JSONObject().put("generation",generation).put("resultProtocol",1).put("analyzerVersion",reply.getInt("analyzerVersion")).put("rulesVersion",reply.getInt("rulesVersion")).put("heads",reply.getJSONArray("heads")).toString(),generation)
                cursor.commit(reply); pages++
            } while(reply.getBoolean("hasMore") && pages<100)
            val status=client.call("preference_training",JSONObject().put("protocol",3).put("action","status"))
            store.setLocalForGeneration("desktop-learning",(status.optJSONObject("learning") ?: JSONObject()).toString(),generation)
            status.optJSONArray("heads")?.let { store.setLocalForGeneration("desktop-head-summaries",it.toString(),generation) }
        }
        // At most six exact byte hashes per foreground exchange; already matched signatures resume.
        kotlinx.coroutines.withTimeout(60000) {
            val contents=store.localRows("desktop-content:",100000)
            if(contents.isEmpty()) return@withTimeout
            val tracks=library.trackDao().all().filter { it.cueTrackNo==null }
            var offset=store.local("desktop-match-cursor")?.toIntOrNull() ?: 0
            if(offset>=tracks.size) offset=0
            var checked=0
            for(t in tracks.drop(offset)) {
                currentCoroutineContext().ensureActive()
                val signature="${t.dateModified}:${t.size}"
                val saved=store.local("desktop-binding:"+t.trackKey)?.let(::JSONObject) ?: store.local("daily-content:"+t.trackKey)?.let(::JSONObject)
                val content=if(saved?.optString("signature")==signature) saved.optString("content") else {
                    if(checked>=6) break
                    checked++
                    val digest=MessageDigest.getInstance("SHA-256")
                    val input=runCatching { context.contentResolver.openInputStream(MediaStore.Audio.Media.getContentUri(t.volumeName,t.mediaStoreId)) }.getOrNull()
                    if(input==null) { offset++; continue }
                    input.use { stream -> val buffer=ByteArray(1024*1024); while(true) { currentCoroutineContext().ensureActive(); val n=stream.read(buffer); if(n<0) break; digest.update(buffer,0,n) } }
                    digest.digest().joinToString("") { "%02x".format(it) }
                }
                val fresh=library.trackDao().byTrackKeys(listOf(t.trackKey)).singleOrNull()
                if(fresh==null || "${fresh.dateModified}:${fresh.size}"!=signature) { offset++; continue }
                store.setLocalForGeneration("desktop-binding:"+t.trackKey,JSONObject().put("signature",signature).put("content",content).toString(),generation)
                contents["desktop-content:$content"]?.let { raw ->
                    val row=JSONObject(raw)
                    if(kotlin.math.abs(row.getLong("durationMs")-t.durationMs)<=1000) store.setLocalForGeneration("desktop-result:"+t.trackKey,JSONObject().put("signature",signature).put("heads",row.getJSONObject("heads")).toString(),generation)
                }
                offset++
            }
            store.setLocalForGeneration("desktop-match-cursor",offset.toString(),generation)
        }
    }
    suspend fun learningAction(action: String,downloadConfirmed: Boolean=false)=withContext(Dispatchers.IO) {
        require(action in listOf("status","learn","resume","cancel"))
        if(action!="status") synchronize()
        com.tinyfish.jeekmusic.data.sync.LanProtocol(lan.read().getJSONObject("connection")).use {
            val response=it.call("preference_training",JSONObject().put("protocol",PreferenceStore.VERSION).put("action",action).put("downloadConfirmed",downloadConfirmed))
            val state=if(action=="status") response.optJSONObject("learning") ?: response else response
            response.optJSONArray("heads")?.let { store.setLocal("desktop-head-summaries",it.toString()) }
            store.setLocal("desktop-learning",state.toString()); notifyChanged(); state
        }
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
        val reserved=(strings(study.getJSONArray("development"))+strings(study.getJSONArray("sealed_test"))).filter { it in parent }.map(::root).toSet()
        require(records.none { it.getJSONObject("payload").optBoolean("exposed") && root(it.getString("entityId")) in reserved }) { "Self-selected feedback contaminated independent validation; review on PC" }
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
        val p=profile(); val generation=store.generation; val old=batch()
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
            require(store.generation==generation) { "Learning data cleared; start labeling again" }
            val id=hash("sha256:$content:whole"); if(id in attempted || id in ids) continue
            if(store.current(p,"recording").none { it.getString("entityId")==id }) store.append(p,id,"recording",JSONObject().put("recordingId",id).put("contentSignature",content).put("title",t.title).put("artist",t.artistDisplay).put("album",t.album)
                .put("artists",JSONArray(listOf(t.artistDisplay))).put("workGroup",t.title).put("albumGroup",t.album).put("durationMs",t.durationMs).put("identityReviewed",false).put("exposed",false).put("supported",true),expectedGeneration=generation)
            store.setLocal("binding:$id",t.trackKey); ids+=id; if(ids.size==6) break
        }
        require(ids.isNotEmpty()) { "No supported new recordings" }
        val id=UUID.randomUUID().toString().replace("-","")
        val payload=JSONObject().put("dimension",activeDimension).put("protocolVersion","v2-operational-pilot").put("seed","personal-workout-v1").put("items",JSONArray(ids)).put("role","training").put("state","identity_incomplete_pilot").put("independent",false).put("exposure","blind_ui_no_predictions")
        payload.put("hash",hash(payload.toString())); store.append(p,id,"manifest",payload,expectedGeneration=generation); store.setLocal(batchKey(p,activeDimension),id); notifyChanged(); batch()
    }
}
