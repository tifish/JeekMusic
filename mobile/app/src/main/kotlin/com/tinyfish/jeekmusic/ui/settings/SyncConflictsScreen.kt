package com.tinyfish.jeekmusic.ui.settings

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.sync.LanSyncService
import org.json.JSONObject
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@OptIn(ExperimentalMaterial3Api::class)
@Composable fun SyncConflictsScreen(lan: LanSyncService,onBack: ()->Unit) {
    Scaffold(topBar={ TopAppBar(title={Text(stringResource(R.string.sync_conflict_title))},navigationIcon={TextButton(onClick=onBack) { Text(stringResource(R.string.sync_conflict_back)) }}) }) { padding ->
        Box(Modifier.padding(padding)) { SyncConflictList(lan) }
    }
}

@Composable fun SyncConflictList(lan: LanSyncService) {
    val revision by lan.revision.collectAsStateWithLifecycle()
    val count by lan.conflictCount.collectAsStateWithLifecycle()
    val error by lan.conflictError.collectAsStateWithLifecycle()
    val pending by lan.pendingConflictCount.collectAsStateWithLifecycle()
    val status by lan.status.collectAsStateWithLifecycle()
    var offset by remember { mutableIntStateOf(0) }
    var rows by remember { mutableStateOf(emptyList<JSONObject>()) }
    LaunchedEffect(revision,offset) { if(offset>=count && offset>0)offset=0;rows=lan.conflictPage(offset) }
    LazyColumn(Modifier.fillMaxSize().testTag("sync_conflicts"),contentPadding=PaddingValues(16.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
        item { Text(stringResource(R.string.sync_conflict_help)) }
        if(count>0)item {
            SyncConflictSummary(count,pending)
            if(pending>0) {
                Text(stringResource(R.string.sync_conflict_waiting_help))
                TextButton(enabled=!status.running,onClick={lan.start()},modifier=Modifier.testTag("sync_conflict_resume")) {
                    Text(stringResource(if(status.running) R.string.sync_conflict_syncing else R.string.sync_conflict_resume))
                }
            }
        }
        if(error)item { Text(stringResource(R.string.sync_conflict_retry),color=MaterialTheme.colorScheme.error) }
        if(rows.isEmpty())item { Text(stringResource(R.string.sync_conflict_empty)) }
        items(rows,key={it.getString("id")}) { row -> SyncConflictCard(row) { lan.resolveConflict(row.getString("id"),it) } }
        item { Row {
            TextButton(enabled=offset>0,onClick={offset=(offset-100).coerceAtLeast(0)}) { Text(stringResource(R.string.sync_conflict_previous)) }
            TextButton(enabled=offset+rows.size<count,onClick={offset+=100}) { Text(stringResource(R.string.sync_conflict_next)) }
        } }
    }
}

@Composable internal fun SyncConflictSummary(total: Int,pending: Int) {
    val waiting=pending.coerceIn(0,total)
    val unchosen=total-waiting
    Text(when {
        waiting==0 -> stringResource(R.string.sync_conflict_notice,unchosen)
        unchosen==0 -> stringResource(R.string.sync_conflict_waiting_notice,waiting)
        else -> stringResource(R.string.sync_conflict_mixed_notice,unchosen,waiting)
    })
}

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun SyncConflictCard(row: JSONObject,onChoose: (String)->Unit) {
    val choice=row.optString("choice").takeIf { it in setOf("local","remote") }
    val queued=row.optString("queuedChoice").takeIf { it in setOf("local","remote") }
    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(16.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text(row.getString("path"),style=MaterialTheme.typography.titleMedium)
        for((side,label) in listOf("local" to R.string.sync_conflict_phone,"remote" to R.string.sync_conflict_pc)) {
            val token=row.optString(side+"Version").takeIf { it.startsWith("m2:") }?.split(':')
            val other=row.optString((if(side=="local")"remote" else "local")+"Version").takeIf { it.startsWith("m2:") }?.split(':')
            Text(stringResource(label),style=MaterialTheme.typography.labelLarge)
            Text(if(token==null)stringResource(R.string.sync_conflict_missing) else {
                val time=runCatching { val ns=token[2].toLong();DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss.SSSSSSSSS").withZone(ZoneId.systemDefault()).format(Instant.ofEpochSecond(Math.floorDiv(ns,1_000_000_000L),Math.floorMod(ns,1_000_000_000L))) }.getOrDefault("—")
                val sizeHint=if(other==null)"" else stringResource(when(token[1].toLong().compareTo(other[1].toLong())) { 1->R.string.sync_conflict_larger; -1->R.string.sync_conflict_smaller; else->R.string.sync_conflict_equal_size })
                val timeHint=if(other==null)"" else stringResource(when(token[2].toLong().compareTo(other[2].toLong())) { 1->R.string.sync_conflict_newer; -1->R.string.sync_conflict_older; else->R.string.sync_conflict_equal_time })
                if(other==null)stringResource(R.string.sync_conflict_version,token[1],time)
                else stringResource(R.string.sync_conflict_compared_version,token[1],sizeHint,time,timeHint)
            })
        }
        if(choice!=null)Text(stringResource(R.string.sync_conflict_pending,stringResource(if(choice=="local")R.string.lan_use_phone else R.string.lan_use_pc)))
        else if(queued!=null)Text(stringResource(R.string.sync_conflict_queued,stringResource(if(queued=="local")R.string.lan_use_phone else R.string.lan_use_pc)))
        if(choice==null && queued==null)FlowRow {
            TextButton(onClick={onChoose("local")}) { Text(stringResource(R.string.lan_use_phone)) }
            TextButton(onClick={onChoose("remote")}) { Text(stringResource(R.string.lan_use_pc)) }
        }
    } }
}
