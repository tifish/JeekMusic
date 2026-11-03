package com.tinyfish.jeekmusic.ui.settings

import android.net.Uri
import android.Manifest
import android.os.Build
import android.os.Environment
import android.content.Intent
import android.content.pm.PackageManager
import android.provider.Settings
import android.provider.DocumentsContract
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalResources
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LifecycleResumeEffect
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.testTagsAsResourceId
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.sync.LanMappingTargets
import com.tinyfish.jeekmusic.data.sync.LanSyncService
import com.tinyfish.jeekmusic.data.sync.LanInvitation
import com.tinyfish.jeekmusic.data.sync.LanRemoteException
import kotlinx.coroutines.launch
import kotlinx.coroutines.CancellationException
import org.json.JSONObject

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun LanSyncSettings(lan: LanSyncService) {
    val status by lan.status.collectAsStateWithLifecycle()
    val schedule by lan.schedule.collectAsStateWithLifecycle()
    val revision by lan.revision.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val resources = LocalResources.current
    var mtimeAllowed by remember { mutableStateOf(Environment.isExternalStorageManager()) }
    LifecycleResumeEffect(Unit) { mtimeAllowed = Environment.isExternalStorageManager(); onPauseOrDispose {} }
    val mappings = remember(revision) { lan.store.mappings() }
    val connection = remember(revision) { lan.store.read().optJSONObject("connection") }
    val paired = connection != null
    var scanning by remember { mutableStateOf(false) }
    var cameraDenied by remember { mutableStateOf(false) }
    var candidate by remember { mutableStateOf<LanInvitation?>(null) }
    var notice by remember { mutableStateOf<String?>(null) }
    val cameraPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { allowed ->
        if (allowed) scanning = true else cameraDenied = true
    }
    var pairing by remember { mutableStateOf(false) }
    var invitation by remember { mutableStateOf("") }
    var roots by remember { mutableStateOf<List<JSONObject>?>(null) }
    var choosingRoots by remember { mutableStateOf(false) }
    var rootsError by remember { mutableStateOf<String?>(null) }
    var editing by remember { mutableStateOf<JSONObject?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    var forget by remember { mutableStateOf<JSONObject?>(null) }
    var details by rememberSaveable { mutableStateOf(false) }
    val enabledMappings = mappings.any { LanMappingTargets.isCurrent(it) && it.optBoolean("enabled", true) }
    suspend fun refreshRoots() {
        busy = true; rootsError = null
        try { roots = lan.roots() }
        catch (e: CancellationException) { throw e }
        catch (e: Exception) { rootsError = resources.getString(R.string.lan_roots_failed, e.message.orEmpty()) }
        finally { busy = false }
    }
    Column(Modifier.fillMaxWidth()) {
        SettingsSectionHeader(stringResource(R.string.sync_connection))
        SettingSummary(stringResource(R.string.lan_intro), Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
        SettingSummary(connection?.let { stringResource(R.string.lan_paired_identity, it.optString("name", it.optString("host")), "${it.optString("host")}:${it.optInt("port")}") }
            ?: stringResource(R.string.sync_not_paired), Modifier.padding(horizontal = 16.dp, vertical = 8.dp).testTag("lan_connection"))
        SettingItem(stringResource(R.string.lan_scan), enabled = !busy && !status.running, modifier = Modifier.testTag("lan_scan"), onClick = {
            if (ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) scanning = true
            else cameraPermission.launch(Manifest.permission.CAMERA)
        })
        SettingItem(stringResource(R.string.lan_pair_manual), modifier = Modifier.testTag("lan_pair_manual"),
            enabled = !busy && !status.running, onClick = { pairing = true })
        notice?.let { SettingSummary(it, Modifier.padding(horizontal = 16.dp)) }
        SettingsSectionHeader(stringResource(R.string.sync_folders))
        SettingSummary(stringResource(R.string.sync_folders_help), Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
        SettingItem(stringResource(R.string.lan_files_access), summary = stringResource(if (mtimeAllowed) R.string.lan_files_access_allowed else R.string.lan_files_access_needed), modifier = Modifier.testTag("lan_files_access"), onClick = {
            context.startActivity(Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:${context.packageName}")))
        })
        SettingItem(stringResource(R.string.lan_choose_roots), summary = stringResource(if (paired) R.string.lan_manual else R.string.sync_pair_first), modifier = Modifier.testTag("lan_choose_roots"), enabled = paired && !busy && !status.running, onClick = {
            if (!busy && !status.running) { choosingRoots = true; scope.launch { refreshRoots() } }
        })
        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth().padding(16.dp))
        for (m in mappings) {
            val selected = m.getJSONArray("selection").let { a -> List(a.length()) { a.getString(it).ifEmpty { "/" } }.joinToString(", ") }
            val current=LanMappingTargets.isCurrent(m)
            SettingItem(m.getString("name"), summary = (if(current) targetLabel(Uri.parse(m.getString("treeUri"))) else m.getString("relative")+"\n"+stringResource(R.string.lan_mapping_reselect))+"\n"+selected, enabled = !status.running, onClick = { editing = m })
            SwitchRow(title = stringResource(R.string.lan_enable), summary = stringResource(R.string.lan_pause_keep),
                checked = current && m.optBoolean("enabled", true), enabled = current,
                modifier = Modifier.testTag("lan_mapping_enabled_${m.getString("rootId")}"),
                onChange = { lan.setEnabled(m.getString("rootId"), it) })
            FlowRow(Modifier.fillMaxWidth().padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(enabled = !status.running, onClick = { forget = m }) { Text(stringResource(R.string.lan_forget)) }
            }
        }
        SettingsSectionHeader(stringResource(R.string.sync_automation))
        SettingSummary(stringResource(if(schedule.cancelled) R.string.lan_auto_cancelled else R.string.lan_auto_help), Modifier.padding(horizontal = 16.dp, vertical = 8.dp).testTag("lan_auto_status"))
        SettingSummary(stringResource(when {
            status.running -> R.string.settings_group_sync_running
            !paired -> R.string.sync_pair_first
            !enabledMappings -> R.string.sync_no_enabled_folders
            schedule.cancelled -> R.string.sync_cancelled
            schedule.backoffUntil > System.currentTimeMillis() -> R.string.sync_retry_waiting
            schedule.watchFailed -> R.string.sync_watch_limited
            schedule.foreground && schedule.watchDirectories > 0 -> R.string.sync_watching
            else -> R.string.sync_waiting
        }), Modifier.padding(horizontal = 16.dp, vertical = 8.dp).testTag("lan_schedule"))
        if (schedule.lastSuccess > 0) SettingSummary(stringResource(R.string.sync_last_success,
            java.text.DateFormat.getDateTimeInstance().format(java.util.Date(schedule.lastSuccess))), Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
        FlowRow(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(enabled = paired && !busy && !status.running && enabledMappings, onClick = lan::start, modifier = Modifier.testTag("lan_start")) { Text(stringResource(R.string.lan_start)) }
            OutlinedButton(enabled = status.running || (paired && enabledMappings && !schedule.cancelled), onClick = lan::cancel, modifier = Modifier.testTag("lan_cancel")) { Text(stringResource(R.string.cancel)) }
        }
        if (status.running) LinearProgressIndicator(Modifier.fillMaxWidth().padding(horizontal = 16.dp))
        if (status.text.isNotBlank()) SettingSummary(status.text, Modifier.padding(horizontal = 16.dp, vertical = 8.dp).testTag("lan_status"))
        if(status.fileProcessed>0) SettingSummary(stringResource(R.string.lan_file_summary,status.fileProcessed,status.fileActions.values.sum(),status.fileConflicts.size,status.fileDeferredCount),Modifier.padding(16.dp))
        for(conflict in status.fileConflicts.take(20)) Card(Modifier.fillMaxWidth().padding(horizontal=16.dp,vertical=4.dp)) {
            Column(Modifier.padding(12.dp)) {
                Text(conflict.getString("path"),style=MaterialTheme.typography.titleSmall)
                Text(stringResource(R.string.lan_file_conflict_changes,fileChangeLabel(conflict.optString("localChange")),fileChangeLabel(conflict.optString("remoteChange"))),style=MaterialTheme.typography.bodySmall)
                FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                    TextButton(enabled=!status.running,onClick={ lan.resolveConflict(conflict.getString("id"),"local") }) { Text(stringResource(R.string.lan_use_phone)) }
                    TextButton(enabled=!status.running,onClick={ lan.resolveConflict(conflict.getString("id"),"remote") }) { Text(stringResource(R.string.lan_use_pc)) }
                }
            }
        }
        if (status.total > 0) Text("${status.done / 1024} / ${status.total / 1024} KiB", Modifier.padding(horizontal = 16.dp))
        if (status.precisionNs > 0) Text(stringResource(R.string.lan_precision_summary, stringResource(when (status.precisionNs) {
            1_000L -> R.string.lan_microsecond; 1_000_000L -> R.string.lan_millisecond; 1_000_000_000L -> R.string.lan_second; else -> R.string.lan_two_seconds
        })), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall)
        if (mappings.isNotEmpty() || status.timeDetails.isNotEmpty()) OutlinedButton(onClick = { details = !details }) { Text(stringResource(if (details) R.string.lan_hide_details else R.string.lan_details)) }
        if (details) {
            for (m in mappings) Text("${m.getString("name")}\n${m.getString("rootId")}\n${m.getString("treeUri")}\n${m.getString("relative")}", Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall)
            for (detail in status.timeDetails) Text(detail, Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall)
        }
        for (problem in status.problems.take(20)) Text(problem, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(horizontal = 16.dp))
    }
    if (pairing) AlertDialog(modifier = Modifier.semantics { testTagsAsResourceId = true }, onDismissRequest = { if (!busy) { pairing = false; invitation = "" } }, title = { Text(stringResource(R.string.lan_pair)) },
        text = { Column { Text(stringResource(R.string.lan_invite_hint)); OutlinedTextField(invitation, { invitation = it }, label = { Text(stringResource(R.string.sync_invitation)) }, enabled = !busy, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, autoCorrectEnabled = false), modifier = Modifier.testTag("lan_invitation")) } },
        confirmButton = { TextButton(enabled = !busy && invitation.isNotBlank(), onClick = {
            try { candidate = LanInvitation.parse(invitation); pairing = false } catch (_: Exception) { invitation = ""; error = resources.getString(R.string.lan_invalid_invitation) }
        }) { Text(stringResource(R.string.lan_continue)) } }, dismissButton = { TextButton(enabled = !busy, onClick = { pairing = false; invitation = "" }) { Text(stringResource(R.string.cancel)) } })
    if (scanning) LanQrScanner(onResult = { raw ->
        scanning = false
        try { candidate = LanInvitation.parse(raw); invitation = raw }
        catch (_: Exception) { invitation = ""; error = resources.getString(R.string.lan_invalid_invitation) }
    }, onCancel = { scanning = false; notice = resources.getString(R.string.lan_scan_cancelled) },
        onFailure = { scanning = false; error = resources.getString(R.string.lan_camera_error) })
    if (cameraDenied) AlertDialog(onDismissRequest = { cameraDenied = false }, title = { Text(stringResource(R.string.lan_scan)) },
        text = { Text(stringResource(R.string.lan_camera_denied)) },
        confirmButton = { TextButton(onClick = { cameraDenied = false; context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${context.packageName}"))) }) { Text(stringResource(R.string.lan_open_settings)) } },
        dismissButton = { TextButton(onClick = { cameraDenied = false; pairing = true }) { Text(stringResource(R.string.lan_pair_manual)) } })
    candidate?.let { pc -> AlertDialog(onDismissRequest = { if (!busy) { candidate = null; invitation = "" } },
        title = { Text(stringResource(R.string.lan_pair)) }, text = { Column {
            Text(stringResource(R.string.lan_confirm_pc, pc.endpoint))
            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        } }, confirmButton = { TextButton(enabled = !busy, onClick = { scope.launch {
            busy = true
            var connected = false
            try { lan.pair(invitation); invitation = ""; candidate = null; notice = null; connected = true }
            catch (e: CancellationException) { throw e }
            catch (e: Exception) { invitation = ""; candidate = null; error = resources.getString(pairingError(e)) }
            finally { busy = false }
            if (connected) { roots = null; choosingRoots = true; refreshRoots() }
        } }) { Text(stringResource(R.string.lan_connect)) } },
        dismissButton = { TextButton(enabled = !busy, onClick = { candidate = null; invitation = "" }) { Text(stringResource(R.string.cancel)) } }) }
    if (choosingRoots) AlertDialog(modifier = Modifier.semantics { testTagsAsResourceId = true }, onDismissRequest = { if (!busy) choosingRoots = false }, title = { Text(stringResource(R.string.lan_choose_roots)) },
        text = { Column(Modifier.heightIn(max = 350.dp).verticalScroll(rememberScrollState())) {
            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth().testTag("lan_roots_loading"))
            rootsError?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.testTag("lan_roots_error")) }
            if (!busy && rootsError == null && roots?.isEmpty() == true) Text(stringResource(R.string.lan_no_roots), Modifier.testTag("lan_no_roots"))
            val list = roots.orEmpty()
            for (root in list) SettingItem(
                title = root.optString("name") + if (list.count { it.optString("name") == root.optString("name") } > 1) " · " + root.getString("rootId").take(8) else "",
                summary = if (!root.optBoolean("available")) stringResource(R.string.lan_offline) else null,
                enabled = !busy && rootsError == null,
                onClick = { editing = mappings.firstOrNull { it.getString("rootId") == root.getString("rootId") } ?: root; choosingRoots = false })
        } }, confirmButton = { TextButton(enabled = !busy && !status.running, onClick = { scope.launch { refreshRoots() } }, modifier = Modifier.testTag("lan_refresh_roots")) { Text(stringResource(R.string.lan_refresh_roots)) } },
        dismissButton = { TextButton(enabled = !busy, onClick = { choosingRoots = false }) { Text(stringResource(R.string.cancel)) } })
    editing?.let { root -> LanMappingDialog(lan, root, onDismiss = { editing = null }, onError = { error = it }) }
    forget?.let { m -> AlertDialog(onDismissRequest = { forget = null }, title = { Text(stringResource(R.string.lan_forget)) }, text = { Text(stringResource(R.string.lan_forget_confirm)) },
        confirmButton = { TextButton(onClick = { lan.forget(m.getString("rootId")); forget = null }) { Text(stringResource(R.string.confirm)) } },
        dismissButton = { TextButton(onClick = { forget = null }) { Text(stringResource(R.string.cancel)) } }) }
    error?.let { message -> AlertDialog(onDismissRequest = { error = null }, title = { Text(stringResource(R.string.lan_issues)) }, text = { Text(message) },
        confirmButton = { TextButton(onClick = { error = null }) { Text(stringResource(R.string.confirm)) } }) }
}

@Composable
private fun fileChangeLabel(value: String)=stringResource(when(value) {
    "modified"->R.string.lan_change_modified;"deleted"->R.string.lan_change_deleted;"created"->R.string.lan_change_created
    "unchanged"->R.string.lan_change_unchanged;"untracked_present"->R.string.lan_change_initial;"absent"->R.string.lan_change_absent
    else->R.string.lan_change_unknown
})

@Composable
private fun LanMappingDialog(lan: LanSyncService, root: JSONObject, onDismiss: () -> Unit, onError: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val resources = LocalResources.current
    var path by remember { mutableStateOf("") }
    var children by remember { mutableStateOf<List<String>>(emptyList()) }
    var selection by remember { mutableStateOf(root.optJSONArray("selection")?.let { a -> List(a.length()) { a.getString(it) }.toSet() } ?: emptySet()) }
    var target by remember { mutableStateOf(root.optString("treeUri").takeIf { LanMappingTargets.isCurrent(root) && it.isNotEmpty() }?.let(Uri::parse)) }
    var busy by remember { mutableStateOf(false) }
    var estimate by remember { mutableStateOf<String?>(null) }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { if (it != null) target = it }
    val audioPermission = if (Build.VERSION.SDK_INT >= 33) Manifest.permission.READ_MEDIA_AUDIO else Manifest.permission.READ_EXTERNAL_STORAGE
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { allowed -> if (allowed) picker.launch(target) else onError(resources.getString(R.string.permission_needed)) }
    LaunchedEffect(path) { busy = true; try { children = lan.directories(root.getString("rootId"), path) } catch (e: Exception) { onError(e.message.orEmpty()) } finally { busy = false } }
    fun select(value: String) {
        selection = if (value in selection) selection - value else selection.filterNot { value.isEmpty() || it.startsWith("$value/") || it.isEmpty() || value.startsWith("$it/") }.toSet() + value
        estimate = null
    }
    AlertDialog(onDismissRequest = { if (!busy) onDismiss() }, title = { Text(root.getString("name")) },
        text = { Column(Modifier.heightIn(max = 490.dp).verticalScroll(rememberScrollState())) {
            SettingSummary(stringResource(R.string.lan_folders_hint))
            SettingCheckbox(title = stringResource(R.string.lan_whole_root), checked = "" in selection, onChange = { select("") }, enabled = !busy)
            Text("/${path}")
            if (path.isNotEmpty()) TextButton(onClick = { path = path.substringBeforeLast('/', "") }, enabled = !busy) { Text("← ..") }
            for (child in children) {
                val rel = if (path.isEmpty()) child else "$path/$child"
                Row(Modifier.fillMaxWidth(), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    Checkbox(checked = selection.any { it.isEmpty() || rel == it || rel.startsWith("$it/") }, onCheckedChange = { select(rel) }, enabled = !busy)
                    SettingItem(title = child, enabled = !busy, modifier = Modifier.weight(1f), onClick = { path = rel })
                }
            }
            Text(selection.joinToString("\n") { it.ifEmpty { "/" } })
            OutlinedButton(enabled = !busy && selection.isNotEmpty(), onClick = { scope.launch {
                busy = true; try { val plan = lan.estimate(root.getString("rootId"), selection); estimate = resources.getString(R.string.lan_estimate_format, plan.getInt("total"), plan.getLong("bytes")) }
                catch (e: Exception) { onError(e.message.orEmpty()) } finally { busy = false }
            } }) { Text(stringResource(R.string.lan_estimate)) }
            estimate?.let { Text(it) }
            SettingItem(title = stringResource(R.string.lan_target), summary = target?.let(::targetLabel), enabled = !busy, onClick = {
                if (ContextCompat.checkSelfPermission(context, audioPermission) == PackageManager.PERMISSION_GRANTED) picker.launch(target) else permission.launch(audioPermission)
            }, modifier = Modifier.testTag("lan_pick_target"))
            target?.let { SettingSummary(stringResource(R.string.lan_target_direct, targetLabel(it))) }
            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        } },
        confirmButton = { TextButton(enabled = !busy && selection.isNotEmpty() && target != null, onClick = { scope.launch {
            busy = true; try { lan.saveMapping(root, selection, target!!); onDismiss() } catch (e: Exception) { onError(e.message.orEmpty()) } finally { busy = false }
        } }) { Text(stringResource(R.string.save)) } }, dismissButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

private fun targetLabel(uri: Uri): String = runCatching {
    val id = DocumentsContract.getTreeDocumentId(uri)
    val volume = id.substringBefore(':')
    val path = id.substringAfter(':', "").trimEnd('/')
    if (path.isNotEmpty()) path else volume
}.getOrDefault(uri.lastPathSegment.orEmpty())

private fun pairingError(e: Exception): Int = when {
    e is LanRemoteException -> if (e.code == "invitation_rejected" || e.code == "unauthorized" || e.message?.startsWith("Authorization denied") == true) R.string.lan_invitation_rejected else R.string.lan_pair_refused
    e is javax.net.ssl.SSLException || e is java.security.cert.CertificateException -> R.string.lan_pair_certificate
    e is java.io.IOException -> R.string.lan_pair_network
    e.message == "Remove existing mappings before pairing another PC" -> R.string.lan_pair_mappings
    e.message == "Cancel sync first" -> R.string.lan_pair_busy
    else -> R.string.lan_invalid_invitation
}
