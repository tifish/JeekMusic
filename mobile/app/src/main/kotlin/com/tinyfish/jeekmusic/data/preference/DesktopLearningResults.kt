package com.tinyfish.jeekmusic.data.preference

import android.provider.MediaStore
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withTimeout
import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.hash
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.DATASET
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.dimension
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.objects
import com.tinyfish.jeekmusic.data.preference.PreferenceStore.Companion.labels

internal fun compatiblePersonalResults(analyzer: Int,rules: Int)=analyzer==com.tinyfish.jeekmusic.analysis.TagRepository.ANALYZER_VERSION && rules in 9..com.tinyfish.jeekmusic.analysis.TagRepository.RULES_VERSION

/** Data-only results over the existing certificate-pinned paired connection. */
internal fun DailyLearningFeedback.answerRevision(d: String)=hash(store.current(DATASET,"answer",true).filter { dimension(it.getJSONObject("payload"))==d }.map { it.getString("eventId") }.sorted().joinToString("\n"))

internal fun DailyLearningFeedback.personalResults(): Map<String,JSONObject> {
    val active=store.local("desktop-heads")?.let(::JSONObject) ?: return emptyMap()
    if(active.optString("generation")!=store.generation || active.optInt("resultProtocol")!=1 || !compatiblePersonalResults(active.optInt("analyzerVersion"),active.optInt("rulesVersion"))) return emptyMap()
    val heads=objects(active.optJSONArray("heads") ?: JSONArray()).filter { it.optBoolean("enabled") && it.optString("answerRevision")==answerRevision(it.getString("dimension")) &&
        store.current(DATASET,"model").groupBy { e -> e.getString("entityId") }.values.any { group -> group.size==1 && group.single().getJSONObject("payload").let { p -> dimension(p)==it.getString("dimension") && p.optString("state")=="active" && p.optString("version")==it.optString("version") } } }
    return store.localRows("desktop-result:",100000).mapNotNull { (key,json) ->
        runCatching {
            val saved=JSONObject(json); val result=JSONObject()
            for(head in heads) {
                val d=head.getString("dimension"); val r=saved.optJSONObject("heads")?.optJSONObject(d) ?: continue
                if(r.optString("version")==head.optString("version") && r.optString("value") in listOf(labels(d).first,labels(d).second)) result.put(d,r)
            }
            if(result.length()==0) null else key.removePrefix("desktop-result:") to JSONObject().put("signature",saved.getString("signature")).put("heads",result)
        }.getOrNull()
    }.toMap()
}
