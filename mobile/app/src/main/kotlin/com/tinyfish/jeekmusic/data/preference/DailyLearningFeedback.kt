package com.tinyfish.jeekmusic.data.preference

import android.content.Context
import android.provider.MediaStore
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONObject
import org.json.JSONArray
import java.security.MessageDigest
import javax.inject.Inject
import javax.inject.Singleton
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.hash
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.DATASET
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.dimensions
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.dimension
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.labels

/** Durable new-review outbox. Old manual history and sync replay never create human answers. */
@Singleton
class DailyLearningFeedback @Inject constructor(@param:ApplicationContext private val context: Context,private val library: JeekDatabase) {
    val store by lazy { PreferenceStore(context) }
    val revision=kotlinx.coroutines.flow.MutableStateFlow(0)
    private val gate=Mutex()
    private val scope=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private var timer: Job?=null
    suspend fun prepare(): String {
        if(store.local("dailyCursor")==null) store.setLocal("dailyCursor",library.tagDao().latestReviewId().toString())
        return store.generation
    }
    fun start() { if(timer?.isActive==true) return; timer=scope.launch { while(isActive) { runCatching { capture() }; delay(30000) } } }
    fun schedule() { scope.launch { runCatching { capture() } } }
    suspend fun resetCursor() { store.setLocal("dailyCursor",library.tagDao().latestReviewId().toString()) }
    fun status()=JSONObject().put("pendingIdentity",store.current(DATASET,"recording").count { it.getJSONObject("payload").optBoolean("selfSelected") && !it.getJSONObject("payload").optBoolean("identityReviewed") }).put("pendingFeatures",store.current(DATASET,"recording").count { it.getJSONObject("payload").optBoolean("selfSelected") && store.local("vector:"+it.getString("entityId"))==null }).put("pending",store.localRows("daily-pending:").size).put("source","explicit_daily_tag_correction").put("basis","track_tag_feedback").put("selectionBias","self_selected_visible_auto_not_independent")
        .put("pendingReasons",JSONArray(store.localRows("daily-problem:").values.groupingBy { it }.eachCount().map { (reason,count) -> JSONObject().put("reason",reason).put("count",count) }))
    suspend fun capture()=gate.withLock {
        val generation=prepare()
        fun set(key: String,value: String) { store.setLocalForGeneration(key,value,generation) }
        for(review in library.tagDao().reviewsAfter(store.local("dailyCursor")!!.toLong())) {
            val record=JSONObject(review.record)
            if(record.optJSONObject("explicitCorrections")!=null && record.optString("learningGeneration")==generation && record.optString("learningDevice")==store.deviceId) {
                record.put("trackKey",review.trackKey).put("reviewId",review.id)
                set("daily-pending:"+review.id.toString().padStart(20,'0'),record.toString())
            }
            set("dailyCursor",review.id.toString())
        }
        var hashed=0; var completed=0
        for((key,json) in store.localRows("daily-pending:",100)) {
            if(store.generation!=generation) break
            val review=JSONObject(json); val trackKey=review.getString("trackKey"); val edits=review.getJSONObject("explicitCorrections")
            var id=""; var content=""
            if(edits.keys().asSequence().any { !edits.isNull(it) } || store.current(DATASET,"answer").any { it.getJSONObject("payload").optString("source")=="explicit_daily_tag_correction" && it.getJSONObject("payload").optString("answer")!="WITHDRAWN" }) {
                val t=library.trackDao().byTrackKeys(listOf(trackKey)).singleOrNull()
                if(t==null) { set("daily-problem:$key","recording_offline"); continue }
                val signature="${t.dateModified}:${t.size}"
                if(review.optString("recordingSignature")!=signature) { set("daily-problem:$key","recording_changed_review_needed"); continue }
                val saved=store.local("daily-content:$trackKey")?.let(::JSONObject)
                if(saved?.optString("signature")==signature) { id=saved.getString("id"); content=saved.getString("content") }
                else {
                    if(hashed>=6) break
                    hashed++
                    val input=runCatching { context.contentResolver.openInputStream(MediaStore.Audio.Media.getContentUri(t.volumeName,t.mediaStoreId)) }.getOrNull()
                    if(input==null) { set("daily-problem:$key","recording_offline"); continue }
                    content=input.use { stream -> val digest=MessageDigest.getInstance("SHA-256"); val buffer=ByteArray(1024*1024); while(true) { currentCoroutineContext().ensureActive(); val n=stream.read(buffer); if(n<0) break; digest.update(buffer,0,n) }; digest.digest().joinToString("") { "%02x".format(it) } }
                    val fresh=library.trackDao().byTrackKeys(listOf(trackKey)).singleOrNull()
                    if(fresh==null || "${fresh.dateModified}:${fresh.size}"!=signature) { set("daily-problem:$key","recording_changed_review_needed"); continue }
                    id=hash("sha256:$content"+(if(t.cueTrackNo!=null) ":cue:${t.startMs ?: 0}:${t.endMs ?: ((t.startMs ?: 0)+t.durationMs)}" else ":whole"))
                }
                set("daily-content:$trackKey",JSONObject().put("id",id).put("content",content).put("signature",signature).toString())
                val records=store.current(DATASET,"recording").filter { it.getString("entityId")==id }
                if(records.size>1) { set("daily-problem:$key","recording_identity_conflict"); continue }
                val r=records.singleOrNull()?.getJSONObject("payload")?.let { JSONObject(it.toString()) } ?: JSONObject().put("recordingId",id).put("contentSignature",content).put("title",t.title).put("artist",t.artistDisplay).put("album",t.album)
                    .put("artists",JSONArray(listOf(t.artistDisplay.uppercase()))).put("workGroup",t.title.uppercase()).put("albumGroup",((t.albumArtist ?: "")+":"+(t.album ?: "")).uppercase()).put("durationMs",t.durationMs).put("identityReviewed",false).put("supported",t.cueTrackNo==null && t.durationMs>=150000)
                if(!r.optBoolean("selfSelected")) { r.put("selfSelected",true).put("exposed",true).put("exposureSource","self_selected_visible_auto"); store.append(DATASET,id,"recording",r,expectedGeneration=generation); invalidate(null) }
                set("binding:$id",t.trackKey)
            }
            var blocked=false
            for(d in edits.keys().asSequence().toList()) {
                if(d !in dimensions) continue
                val bindingKey="daily-answer:"+hash("$trackKey:$d"); val prior=store.local(bindingKey)?.let(::JSONObject)
                var recording=if(edits.isNull(d)) prior?.optString("id").orEmpty() else id
                var signature=if(edits.isNull(d)) prior?.optString("content").orEmpty() else content
                if(edits.isNull(d) && recording.isEmpty() && id.isNotEmpty() && store.current(DATASET,"answer",true).any { it.getString("entityId")==hash("$DATASET:${PreferenceStore.context(d)}:$id:daily") }) { recording=id; signature=content }
                if(recording.isEmpty()) continue
                val label=if(edits.isNull(d)) "WITHDRAWN" else edits.getString(d)
                if(label!="WITHDRAWN" && label !in listOf(labels(d).first,labels(d).second)) continue
                try {
                    store.append(DATASET,hash("$DATASET:${PreferenceStore.context(d)}:$recording:daily"),"answer",JSONObject().put("dimension",d).put("contextId",PreferenceStore.context(d)).put("recordingId",recording).put("contentSignature",signature)
                        .put("answer",label).put("basis","track_tag_feedback").put("reason","").put("source","explicit_daily_tag_correction").put("exposure","self_selected_visible_auto").put("role","training").put("manifestId","daily-feedback-v1").put("sourceReview",hash(store.deviceId+":"+review.getLong("reviewId"))),
                        resolve=edits.isNull(d),expectedGeneration=generation,eventId=hash("daily:"+store.deviceId+":"+review.getLong("reviewId")+":"+d))
                    if(!edits.isNull(d)) set(bindingKey,JSONObject().put("id",recording).put("content",signature).toString())
                    invalidate(d)
                } catch(_: IllegalArgumentException) { blocked=true; set("daily-problem:$key","concurrent_answer_requires_review") }
            }
            if(!blocked) { store.removeLocal(key); store.removeLocal("daily-problem:$key"); completed++ }
        }
        if(completed>0) revision.value++
        status().put("completed",completed).put("hashedFiles",hashed)
    }
    suspend fun close() { scope.coroutineContext[Job]?.cancelAndJoin(); store.close() }
    private fun invalidate(d: String?) {
        for(e in store.current(DATASET,"model").filter { (d==null || dimension(it.getJSONObject("payload"))==d) && it.getJSONObject("payload").optString("state") in listOf("development_required","regression_required","independent_test_required","pass_research_gate","self_selected_candidate") }) {
            store.append(DATASET,e.getString("entityId"),"model",JSONObject(e.getJSONObject("payload").toString()).put("state","invalidated").put("enabled",false),true)
        }
    }
}
