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
    val status by lan.status.collectAsStateWithLifecycle()
    var offset by remember { mutableIntStateOf(0) }
    var rows by remember { mutableStateOf(emptyList<JSONObject>()) }
    LaunchedEffect(revision,offset) { if(offset>=count && offset>0)offset=0;rows=lan.conflictPage(offset) }
    LazyColumn(Modifier.fillMaxSize().testTag("sync_conflicts"),contentPadding=PaddingValues(16.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
        item { Text(stringResource(R.string.sync_conflict_help)) }
        if(error)item { Text(stringResource(R.string.sync_conflict_retry),color=MaterialTheme.colorScheme.error) }
        if(rows.isEmpty())item { Text(stringResource(R.string.sync_conflict_empty)) }
        items(rows,key={it.getString("id")}) { row -> SyncConflictCard(row,!status.running) { lan.resolveConflict(row.getString("id"),it) } }
        item { Row {
            TextButton(enabled=offset>0,onClick={offset=(offset-100).coerceAtLeast(0)}) { Text(stringResource(R.string.sync_conflict_previous)) }
            TextButton(enabled=offset+rows.size<count,onClick={offset+=100}) { Text(stringResource(R.string.sync_conflict_next)) }
        } }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun SyncConflictCard(row: JSONObject,enabled: Boolean,onChoose: (String)->Unit) {
    val choice=row.optString("choice").takeIf { it in setOf("local","remote") }
    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(16.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text(row.getString("path"),style=MaterialTheme.typography.titleMedium)
        for((side,label) in listOf("local" to R.string.sync_conflict_phone,"remote" to R.string.sync_conflict_pc)) {
            val token=row.optString(side+"Version").takeIf { it.startsWith("m2:") }?.split(':')
            Text(stringResource(label),style=MaterialTheme.typography.labelLarge)
            Text(if(token==null)stringResource(R.string.sync_conflict_missing) else {
                val time=runCatching { val ns=token[2].toLong();DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss.SSSSSSSSS").withZone(ZoneId.systemDefault()).format(Instant.ofEpochSecond(Math.floorDiv(ns,1_000_000_000L),Math.floorMod(ns,1_000_000_000L))) }.getOrDefault("—")
                stringResource(R.string.sync_conflict_version,token[1],time)
            })
        }
        if(choice!=null)Text(stringResource(R.string.sync_conflict_pending,stringResource(if(choice=="local")R.string.lan_use_phone else R.string.lan_use_pc)))
        FlowRow {
            TextButton(enabled=enabled && choice==null,onClick={onChoose("local")}) { Text(stringResource(R.string.lan_use_phone)) }
            TextButton(enabled=enabled && choice==null,onClick={onChoose("remote")}) { Text(stringResource(R.string.lan_use_pc)) }
        }
    } }
}
