package com.tinyfish.jeekmusic.ui.preference

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.preference.PreferenceService
import com.tinyfish.jeekmusic.data.preference.PreferenceStore
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import javax.inject.Inject

@HiltViewModel
class PreferenceViewModel @Inject constructor(val service: PreferenceService): ViewModel() {
    val snapshot=MutableStateFlow(JSONObject())
    val error=MutableStateFlow("")
    val busy=MutableStateFlow(false)
    val training=MutableStateFlow("")
    init { viewModelScope.launch { service.revision.collect { try { refresh() } catch(e: Exception) { error.value=e.message.orEmpty() } } } }
    suspend fun refresh()=withContext(Dispatchers.IO) {
        snapshot.value=JSONObject().put("profiles",JSONArray(service.profiles())).put("status",service.status()).put("batch",if(service.activeProfile.isNotEmpty() && !service.deleted(service.activeProfile)) service.batch() else JSONObject().put("items",JSONArray()))
    }
    fun run(block: suspend () -> Unit) { if(busy.value) return; viewModelScope.launch { busy.value=true; try { withContext(Dispatchers.IO) { block() }; refresh(); error.value="" } catch(e: Exception) { error.value=e.message.orEmpty() } finally { busy.value=false } } }
}

/** Predictions and scores deliberately absent. This panel never sends playback commands. */
@Composable
fun PreferencePanel(vm: PreferenceViewModel=hiltViewModel()) {
    val data by vm.snapshot.collectAsStateWithLifecycle()
    val error by vm.error.collectAsStateWithLifecycle()
    val busy by vm.busy.collectAsStateWithLifecycle()
    val training by vm.training.collectAsStateWithLifecycle()
    val syncState by vm.service.syncState.collectAsStateWithLifecycle()
    val context=LocalContext.current
    val matchResultFormat=stringResource(R.string.preference_match_result)
    var name by remember { mutableStateOf("") }
    var delete by remember { mutableStateOf(false) }
    val export=rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { uri -> if(uri!=null) vm.run { context.contentResolver.openOutputStream(uri)?.use { it.write(vm.service.export().toByteArray(Charsets.UTF_8)) } } }
    Column(Modifier.fillMaxWidth().padding(vertical=8.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text(stringResource(R.string.preference_title),style=MaterialTheme.typography.titleLarge)
        val dimension=data.optJSONObject("status")?.optString("dimension") ?: "MOOD"
        Row { for(d in PreferenceStore.dimensions) TextButton(onClick={vm.run { vm.service.selectDimension(d) }},enabled=!busy) { Text((if(d==dimension) "✓ " else "")+stringResource(when(d) { "MOOD"->R.string.preference_mood; "VOICE"->R.string.preference_voice; "GENRE"->R.string.preference_genre; else->R.string.preference_calmness })) } }
        Text(stringResource(R.string.preference_intro),style=MaterialTheme.typography.bodySmall)
        OutlinedTextField(name,{ name=it.take(80) },label={Text(stringResource(R.string.preference_profile_name))},modifier=Modifier.fillMaxWidth())
        TextButton(onClick={vm.run { vm.service.create(name) }},enabled=!busy && name.isNotBlank()) { Text(stringResource(R.string.preference_create)) }
        val profiles=PreferenceStore.objects(data.optJSONArray("profiles") ?: JSONArray()).filterNot { it.optBoolean("deleted") }
        for(p in profiles) TextButton(onClick={vm.run { vm.service.select(p.getString("profileId")) }},enabled=!busy) { Text((if(p.getString("profileId")==data.optJSONObject("status")?.optString("profileId")) "✓ " else "")+p.optString("name")+" · "+p.getString("profileId").take(8)) }
        Text(stringResource(R.string.preference_progress,data.optJSONObject("status")?.optInt("answered") ?: 0,data.optJSONObject("status")?.optInt("conflicts") ?: 0))
        Row {
            TextButton(onClick={vm.run { vm.service.prepare() }},enabled=!busy) { Text(stringResource(R.string.preference_prepare)) }
            TextButton(onClick={vm.run { vm.service.synchronize() }},enabled=!busy) { Text(stringResource(R.string.preference_sync)) }
        }
        TextButton(onClick={vm.run { val result=vm.service.matchLocalBatch(); vm.training.value=String.format(matchResultFormat,result.getInt("checkedFiles"),result.getInt("matched")) }},enabled=!busy) { Text(stringResource(R.string.preference_match)) }
        Text(stringResource(when(syncState) { "idle"->R.string.preference_idle; "syncing"->R.string.preference_syncing; "synced"->R.string.preference_synced; else->R.string.preference_offline }))
        Text(stringResource(R.string.preference_identity_warning),style=MaterialTheme.typography.bodySmall)
        val rows=PreferenceStore.objects(data.optJSONObject("batch")?.optJSONArray("items") ?: JSONArray())
        var currentIndex by remember(data.optJSONObject("batch")?.optString("manifestId")) { mutableIntStateOf(0) }
        if(rows.isNotEmpty()) {
            currentIndex=currentIndex.coerceIn(0,rows.lastIndex)
            Row(horizontalArrangement=Arrangement.SpaceBetween,modifier=Modifier.fillMaxWidth()) {
                TextButton(onClick={currentIndex--},enabled=currentIndex>0) { Text(stringResource(R.string.preference_previous)) }
                Text("${currentIndex+1} / ${rows.size}")
                TextButton(onClick={currentIndex++},enabled=currentIndex<rows.lastIndex) { Text(stringResource(R.string.preference_next)) }
            }
        }
        for(item in rows.drop(currentIndex).take(1)) key(dimension+":"+item.getString("recordingId")) {
            var basis by remember { mutableIntStateOf(3) }
            var reason by remember { mutableStateOf("") }
            var chooseBasis by remember { mutableStateOf(false) }
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(12.dp),verticalArrangement=Arrangement.spacedBy(6.dp)) {
                    Text(item.optString("title")+" — "+item.optString("artist"),style=MaterialTheme.typography.titleMedium)
                    Text(item.optString("album")+" · "+item.optLong("durationMs")+" ms")
                    Text(stringResource(when(dimension) { "MOOD"->R.string.preference_question; "VOICE"->R.string.preference_question_voice; "GENRE"->R.string.preference_question_genre; else->R.string.preference_question_calmness }))
                    if(item.optString("answer").isNotEmpty()) Text(stringResource(R.string.preference_saved,item.optString("answer")))
                    val labels=listOf(R.string.preference_familiar,R.string.preference_full,R.string.preference_excerpts,R.string.preference_unclear)
                    TextButton(onClick={chooseBasis=true}) { Text(stringResource(labels[basis])) }
                    if(chooseBasis) AlertDialog(onDismissRequest={chooseBasis=false},title={Text(stringResource(R.string.preference_basis))},text={Column { for(i in labels.indices) Row { RadioButton(basis==i,{basis=i;chooseBasis=false}); TextButton(onClick={basis=i;chooseBasis=false}) { Text(stringResource(labels[i])) } } }},confirmButton={TextButton(onClick={chooseBasis=false}) {Text(stringResource(android.R.string.cancel))}})
                    OutlinedTextField(reason,{reason=it.take(500)},label={Text(stringResource(R.string.preference_reason))},modifier=Modifier.fillMaxWidth())
                    val conflict=item.optBoolean("conflict")
                    if(conflict) { Text(stringResource(R.string.preference_conflict)); for(r in vm.service.conflicts(item.getString("recordingId"))) Text(r) }
                    val pair=PreferenceStore.labels(dimension)
                    val choices=listOf(pair.first to when(dimension) { "MOOD"->R.string.preference_yes; "VOICE"->R.string.preference_vocal; "GENRE"->R.string.preference_classical; else->R.string.preference_calm },pair.second to when(dimension) { "MOOD"->R.string.preference_no; "VOICE"->R.string.preference_instrumental; "GENRE"->R.string.preference_non_classical; else->R.string.preference_non_calm },"UNCERTAIN" to R.string.preference_uncertain,"UNRESOLVED" to R.string.preference_wrong,"SKIP" to R.string.preference_skip,"WITHDRAWN" to R.string.preference_withdraw)
                    for(pair in choices.chunked(3)) Row { for((answer,label) in pair) TextButton(onClick={vm.run { vm.service.answer(item.getString("recordingId"),answer,PreferenceStore.bases[basis],reason,conflict) }},enabled=!busy) { Text(stringResource(label)) } }
                }
            }
        }
        Text(stringResource(R.string.preference_training_note),style=MaterialTheme.typography.bodySmall)
        TextButton(onClick={vm.run { vm.training.value=vm.service.trainingStatus().optString("state") }},enabled=!busy) { Text(stringResource(R.string.preference_training_status)) }
        if(training.isNotEmpty()) Text(when(training) {
            "insufficient_material"->stringResource(R.string.preference_insufficient)
            "identity_incomplete"->stringResource(R.string.preference_identity_incomplete)
            "components_required"->stringResource(R.string.preference_components_required)
            "running","checking_material","preparing_components"->stringResource(R.string.preference_running)
            "cancelled","interrupted"->stringResource(R.string.preference_cancelled)
            "failed","incomplete","validation_failed","inconclusive"->stringResource(R.string.preference_failed)
            "synthetic_validation_complete"->stringResource(R.string.preference_synthetic_complete)
            "development_required","regression_required","independent_test_required","training_gate_passed","pass_research_gate"->stringResource(R.string.preference_candidate)
            "invalidated"->stringResource(R.string.preference_invalidated)
            "idle"->stringResource(R.string.preference_idle)
            else->training
        })
        Row {
            TextButton(onClick={export.launch("personal-preference.json")},enabled=!busy) { Text(stringResource(R.string.preference_export)) }
            TextButton(onClick={delete=true},enabled=!busy) { Text(stringResource(R.string.preference_delete)) }
        }
        if(busy) CircularProgressIndicator()
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
    }
    if(delete) AlertDialog(onDismissRequest={delete=false},title={Text(stringResource(R.string.preference_delete))},text={Text(stringResource(R.string.preference_delete_confirmation))},confirmButton={TextButton(onClick={delete=false;vm.run { vm.service.delete() }}) { Text(stringResource(R.string.preference_delete)) }},dismissButton={TextButton(onClick={delete=false}) {Text(stringResource(android.R.string.cancel))}})
}
