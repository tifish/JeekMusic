package com.tinyfish.jeekmusic.ui.preference

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.foundation.text.selection.SelectionContainer
import android.content.ClipData
import android.content.ClipboardManager
import com.tinyfish.jeekmusic.data.preference.LearningFailure
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
import org.json.JSONObject
import javax.inject.Inject

@HiltViewModel
class PreferenceViewModel @Inject constructor(val service: PreferenceService): ViewModel() {
    val snapshot=MutableStateFlow(JSONObject())
    val error=MutableStateFlow("")
    val busy=MutableStateFlow(false)
    init {
        viewModelScope.launch { service.revision.collect { refresh() } }
        viewModelScope.launch { while(true) { try { withContext(Dispatchers.IO) { service.trainingStatus() }; refresh() } catch(_: Exception) { }; kotlinx.coroutines.delay(5000) } }
    }
    suspend fun refresh()=withContext(Dispatchers.IO) { snapshot.value=service.status() }
    fun run(block: suspend () -> Unit) {
        if(busy.value) return
        viewModelScope.launch { busy.value=true; try { withContext(Dispatchers.IO) { block() }; refresh(); error.value="" } catch(e: Exception) { error.value=if(e is com.tinyfish.jeekmusic.data.sync.LanRemoteException) e.code else e.message.orEmpty() } finally { busy.value=false } }
    }
}

@Composable
fun PreferencePanel(vm: PreferenceViewModel=hiltViewModel()) {
    val data by vm.snapshot.collectAsStateWithLifecycle()
    val error by vm.error.collectAsStateWithLifecycle()
    val busy by vm.busy.collectAsStateWithLifecycle()
    val syncState by vm.service.syncState.collectAsStateWithLifecycle()
    val context=LocalContext.current
    var delete by remember { mutableStateOf(false) }
    var download by remember { mutableStateOf(false) }
    val desktop=data.optJSONObject("desktop") ?: JSONObject()
    val running=desktop.optBoolean("running")
    val export=rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { uri -> if(uri!=null) vm.run { context.contentResolver.openOutputStream(uri)?.use { it.write(vm.service.export().toByteArray(Charsets.UTF_8)) } } }
    fun action(confirmed: Boolean=false) { vm.run { vm.service.learningAction("learn",confirmed) } }
    Column(Modifier.fillMaxWidth().padding(horizontal=16.dp,vertical=8.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text(stringResource(R.string.preference_title),style=MaterialTheme.typography.titleLarge)
        Text(stringResource(R.string.preference_learning_intro))
        Text(stringResource(R.string.preference_on_computer),style=MaterialTheme.typography.bodySmall)
        Text(learningState(desktop.optString("state")))
        FailureReport(desktop.optJSONObject("failure") ?: if(desktop.optString("state")=="failed") JSONObject().put("code","history_unavailable").put("historical",true) else null)
        if(desktop.optInt("total")>0) Text(stringResource(R.string.preference_library_progress,desktop.optInt("processed"),desktop.optInt("total"),desktop.optInt("uncovered")))
        Button(onClick={if(desktop.optString("state")=="download_required") download=true else action()},enabled=!busy && !running) { Text(stringResource(if(desktop.optString("state") in listOf("cancelled","interrupted","incomplete")) R.string.preference_learning_resume else R.string.preference_learning_start)) }
        if(running) OutlinedButton(onClick={vm.run { vm.service.learningAction("cancel") }},enabled=!busy) { Text(stringResource(R.string.preference_learning_cancel)) }
        if(error.isNotEmpty()) Text(stringResource(if(error=="unauthorized") R.string.lan_pairing_revoked else if(listOf("version","protocol","upgrade").any { error.contains(it,ignoreCase=true) }) R.string.preference_upgrade_required else R.string.preference_computer_unavailable),color=MaterialTheme.colorScheme.error)
        Text(stringResource(R.string.preference_local_coverage,data.optInt("matchedSongs"),data.optInt("fallbackSongs")),style=MaterialTheme.typography.bodySmall)
        if(data.optBoolean("resultPagesPending")) Text(stringResource(R.string.preference_result_pages_pending),style=MaterialTheme.typography.bodySmall)
        for(head in PreferenceStore.objects(data.optJSONArray("heads") ?: org.json.JSONArray())) {
            val d=head.getString("dimension")
            Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(12.dp),verticalArrangement=Arrangement.spacedBy(4.dp)) {
                Text(stringResource(when(d) { "MOOD"->R.string.preference_mood; "VOICE"->R.string.preference_voice; "GENRE"->R.string.preference_genre; else->R.string.preference_calmness }),style=MaterialTheme.typography.titleMedium)
                Text(stringResource(R.string.preference_learning_counts,head.optInt("available"),head.optInt("positive"),head.optInt("negative"),head.optInt("annotated")))
                Text(learningState(head.optString("state")))
                FailureReport(head.optJSONObject("failure") ?: if(head.optString("state")=="failed") JSONObject().put("code","history_unavailable").put("historical",true) else null)
                if(head.optInt("missingTotal")>0) Text(stringResource(R.string.preference_learning_missing,head.optInt("missingPositive"),head.optInt("missingNegative"),head.optInt("missingTotal")),style=MaterialTheme.typography.bodySmall)
                val material=head.optJSONObject("computerMaterial")
                if((material?.optInt("missingLocal") ?: 0)>0) Text(stringResource(R.string.preference_learning_missing_files,material!!.optInt("missingLocal")),style=MaterialTheme.typography.bodySmall)
                if((material?.optInt("missingComponents") ?: 0)>0 && head.optInt("available")>0) Text(stringResource(R.string.preference_learning_variety),style=MaterialTheme.typography.bodySmall)
            } }
        }
        Text(stringResource(R.string.preference_result_coverage),style=MaterialTheme.typography.bodySmall)
        Text(stringResource(when(syncState) { "syncing"->R.string.preference_syncing; "synced"->R.string.preference_synced; else->R.string.preference_offline }))
        TextButton(onClick={vm.run { vm.service.synchronize() }},enabled=!busy) { Text(stringResource(R.string.preference_sync)) }
        Row {
            TextButton(onClick={export.launch("personal-preference.json")},enabled=!busy) { Text(stringResource(R.string.preference_export)) }
            TextButton(onClick={delete=true},enabled=!busy) { Text(stringResource(R.string.preference_delete)) }
        }
        if(busy) CircularProgressIndicator()
    }
    if(download) AlertDialog(onDismissRequest={download=false},title={Text(stringResource(R.string.preference_download_title))},text={Text(stringResource(R.string.preference_download_explanation))},confirmButton={TextButton(onClick={download=false;action(true)}) {Text(stringResource(R.string.preference_download_start))}},dismissButton={TextButton(onClick={download=false}) {Text(stringResource(android.R.string.cancel))}})
    if(delete) AlertDialog(onDismissRequest={delete=false},title={Text(stringResource(R.string.preference_delete))},text={Text(stringResource(R.string.preference_delete_confirmation))},confirmButton={TextButton(onClick={delete=false;vm.run { vm.service.delete() }}) { Text(stringResource(R.string.preference_delete)) }},dismissButton={TextButton(onClick={delete=false}) {Text(stringResource(android.R.string.cancel))}})
}

@Composable
private fun learningState(state: String)=stringResource(when(state) {
    "active"->R.string.preference_learning_active
    "updated"->R.string.preference_learning_updated
    "updated_with_uncovered"->R.string.preference_learning_partial
    "updating_library"->R.string.preference_learning_updating
    "learning","running","checking_material","preparing_components"->R.string.preference_running
    "waiting_playback"->R.string.preference_learning_playback
    "insufficient_material"->R.string.preference_insufficient
    "waiting_checks","check_material_required","self_selected_candidate","development_required","regression_required","independent_test_required","training_gate_passed"->R.string.preference_learning_checks
    "validation_failed"->R.string.preference_learning_failed_check
    "cancelled","interrupted"->R.string.preference_cancelled
    "failed","incomplete"->R.string.preference_failed
    "download_required"->R.string.preference_download_title
    "invalidated"->R.string.preference_invalidated
    else->R.string.preference_idle
})

@Composable
internal fun FailureReport(raw: JSONObject?) {
    val safe=LearningFailure.sanitize(raw) ?: return
    val ids=LearningFailure.resources.getValue(safe.getString("code"))
    val context=LocalContext.current
    var expanded by remember(safe.toString()) { mutableStateOf(false) }
    Text(stringResource(ids.first),color=MaterialTheme.colorScheme.error)
    Text(stringResource(ids.second),style=MaterialTheme.typography.bodySmall)
    TextButton(onClick={expanded=!expanded}) { Text(stringResource(R.string.learning_failure_advanced)) }
    if(expanded) {
        val text=LearningFailure.diagnostics(safe)
        SelectionContainer { Text(text,style=MaterialTheme.typography.bodySmall) }
        TextButton(onClick={ (context.getSystemService(ClipboardManager::class.java)).setPrimaryClip(ClipData.newPlainText("JeekMusic diagnostics",text)) }) { Text(stringResource(R.string.learning_failure_copy)) }
    }
}
