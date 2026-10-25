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
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LifecycleResumeEffect
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.sync.LanSyncService
import kotlinx.coroutines.launch
import org.json.JSONObject

@Composable
fun LanSyncSettings(lan: LanSyncService) {
    val status by lan.status.collectAsStateWithLifecycle()
    val revision by lan.revision.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    var mtimeAllowed by remember { mutableStateOf(Environment.isExternalStorageManager()) }
    LifecycleResumeEffect(Unit) { mtimeAllowed = Environment.isExternalStorageManager(); onPauseOrDispose {} }
    val mappings = remember(revision) { lan.store.mappings() }
    val paired = remember(revision) { lan.store.read().has("connection") }
    var pairing by remember { mutableStateOf(false) }
    var invitation by remember { mutableStateOf("") }
    var roots by remember { mutableStateOf<List<JSONObject>?>(null) }
    var editing by remember { mutableStateOf<JSONObject?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    var clean by remember { mutableStateOf<JSONObject?>(null) }
    var forget by remember { mutableStateOf<JSONObject?>(null) }
    var details by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxWidth()) {
        Text(stringResource(R.string.lan_title), style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(16.dp))
        Text(stringResource(R.string.lan_intro), modifier = Modifier.padding(horizontal = 16.dp))
        SettingItem(stringResource(R.string.lan_mtime_title), summary = stringResource(if (mtimeAllowed) R.string.lan_mtime_allowed else R.string.lan_mtime_permission), onClick = {
            context.startActivity(Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:${context.packageName}")))
        })
        SettingItem(stringResource(if (paired) R.string.lan_pair_again else R.string.lan_pair), onClick = { pairing = true })
        if (paired) SettingItem(stringResource(R.string.lan_choose_roots), summary = stringResource(R.string.lan_manual), onClick = {
            if (!busy && !status.running) scope.launch { busy = true; try { roots = lan.roots() } catch (e: Exception) { error = e.message } finally { busy = false } }
        })
        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth().padding(16.dp))
        for (m in mappings) {
            val selected = m.getJSONArray("selection").let { a -> List(a.length()) { a.getString(it).ifEmpty { "/" } }.joinToString(", ") }
            SettingItem(m.getString("name"), summary = "${targetLabel(Uri.parse(m.getString("treeUri")))} / JeekMusic-${m.getString("rootId").take(8)}…\n$selected", onClick = { if (!status.running) editing = m })
            Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp)) {
                TextButton(enabled = !status.running, onClick = { lan.setEnabled(m.getString("rootId"), !m.optBoolean("enabled", true)) }) {
                    Text(stringResource(if (m.optBoolean("enabled", true)) R.string.lan_pause_keep else R.string.lan_enable))
                }
                TextButton(enabled = !status.running, onClick = { clean = m }) { Text(stringResource(R.string.lan_clean)) }
                TextButton(enabled = !status.running, onClick = { forget = m }) { Text(stringResource(R.string.lan_forget)) }
            }
        }
        if (paired) Row(Modifier.padding(horizontal = 12.dp)) {
            TextButton(enabled = !status.running && mappings.any { it.optBoolean("enabled", true) }, onClick = lan::start, modifier = Modifier.testTag("lan_start")) { Text(stringResource(R.string.lan_start)) }
            TextButton(enabled = status.running, onClick = lan::cancel, modifier = Modifier.testTag("lan_cancel")) { Text(stringResource(R.string.cancel)) }
        }
        if (status.running) LinearProgressIndicator(Modifier.fillMaxWidth().padding(horizontal = 16.dp))
        Text(status.text, Modifier.padding(horizontal = 16.dp).testTag("lan_status"))
        if (status.total > 0) Text("${status.done / 1024} / ${status.total / 1024} KiB", Modifier.padding(horizontal = 16.dp))
        if (status.precisionNs > 0) Text(stringResource(R.string.lan_precision_summary, stringResource(when (status.precisionNs) {
            1_000L -> R.string.lan_microsecond; 1_000_000L -> R.string.lan_millisecond; 1_000_000_000L -> R.string.lan_second; else -> R.string.lan_two_seconds
        })), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall)
        if (mappings.isNotEmpty() || status.timeDetails.isNotEmpty()) TextButton(onClick = { details = !details }) { Text(stringResource(if (details) R.string.lan_hide_details else R.string.lan_details)) }
        if (details) {
            for (m in mappings) Text("${m.getString("name")}\n${m.getString("rootId")}\n${m.getString("treeUri")}\n${m.getString("relative")}", Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall)
            for (detail in status.timeDetails) Text(detail, Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall)
        }
        for (problem in status.problems.take(20)) Text(problem, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(horizontal = 16.dp))
    }
    if (pairing) AlertDialog(onDismissRequest = { if (!busy) { pairing = false; invitation = "" } }, title = { Text(stringResource(R.string.lan_pair)) },
        text = { Column { Text(stringResource(R.string.lan_invite_hint)); OutlinedTextField(invitation, { invitation = it }, enabled = !busy, modifier = Modifier.testTag("lan_invitation")) } },
        confirmButton = { TextButton(enabled = !busy && invitation.isNotBlank(), onClick = {
            scope.launch { busy = true; try { lan.pair(invitation); invitation = ""; pairing = false; roots = lan.roots() } catch (e: Exception) { error = e.message } finally { busy = false } }
        }) { Text(stringResource(R.string.lan_connect)) } }, dismissButton = { TextButton(enabled = !busy, onClick = { pairing = false; invitation = "" }) { Text(stringResource(R.string.cancel)) } })
    roots?.let { list -> AlertDialog(onDismissRequest = { roots = null }, title = { Text(stringResource(R.string.lan_choose_roots)) },
        text = { Column(Modifier.heightIn(max = 350.dp).verticalScroll(rememberScrollState())) {
            if (list.isEmpty()) Text(stringResource(R.string.lan_no_roots))
            for (root in list) TextButton(onClick = { editing = mappings.firstOrNull { it.getString("rootId") == root.getString("rootId") } ?: root; roots = null }) { Text(root.optString("name") + (if (list.count { it.optString("name") == root.optString("name") } > 1) " · " + root.getString("rootId").take(8) else "") + if (!root.optBoolean("available")) " (${stringResource(R.string.lan_offline)})" else "") }
        } }, confirmButton = { TextButton(onClick = { roots = null }) { Text(stringResource(R.string.cancel)) } }) }
    editing?.let { root -> LanMappingDialog(lan, root, onDismiss = { editing = null }, onError = { error = it }) }
    clean?.let { m -> AlertDialog(onDismissRequest = { clean = null }, title = { Text(stringResource(R.string.lan_clean)) }, text = { Text(stringResource(R.string.lan_clean_confirm)) },
        confirmButton = { TextButton(onClick = { clean = null; scope.launch { try { lan.clean(m.getString("rootId")) } catch (e: Exception) { error = e.message } } }) { Text(stringResource(R.string.confirm)) } },
        dismissButton = { TextButton(onClick = { clean = null }) { Text(stringResource(R.string.cancel)) } }) }
    forget?.let { m -> AlertDialog(onDismissRequest = { forget = null }, title = { Text(stringResource(R.string.lan_forget)) }, text = { Text(stringResource(R.string.lan_forget_confirm)) },
        confirmButton = { TextButton(onClick = { lan.forget(m.getString("rootId")); forget = null }) { Text(stringResource(R.string.confirm)) } },
        dismissButton = { TextButton(onClick = { forget = null }) { Text(stringResource(R.string.cancel)) } }) }
    error?.let { message -> AlertDialog(onDismissRequest = { error = null }, title = { Text(stringResource(R.string.lan_issues)) }, text = { Text(message) },
        confirmButton = { TextButton(onClick = { error = null }) { Text(stringResource(R.string.confirm)) } }) }
}

@Composable
private fun LanMappingDialog(lan: LanSyncService, root: JSONObject, onDismiss: () -> Unit, onError: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    var path by remember { mutableStateOf("") }
    var children by remember { mutableStateOf<List<String>>(emptyList()) }
    var selection by remember { mutableStateOf(root.optJSONArray("selection")?.let { a -> List(a.length()) { a.getString(it) }.toSet() } ?: emptySet()) }
    var target by remember { mutableStateOf(root.optString("treeUri").takeIf { it.isNotEmpty() }?.let(Uri::parse)) }
    var exclusive by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var estimate by remember { mutableStateOf<String?>(null) }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { if (it != null) target = it }
    val audioPermission = if (Build.VERSION.SDK_INT >= 33) Manifest.permission.READ_MEDIA_AUDIO else Manifest.permission.READ_EXTERNAL_STORAGE
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { allowed -> if (allowed) picker.launch(target) else onError(context.getString(R.string.permission_needed)) }
    LaunchedEffect(path) { busy = true; try { children = lan.directories(root.getString("rootId"), path) } catch (e: Exception) { onError(e.message.orEmpty()) } finally { busy = false } }
    fun select(value: String) {
        selection = if (value in selection) selection - value else selection.filterNot { value.isEmpty() || it.startsWith("$value/") || it.isEmpty() || value.startsWith("$it/") }.toSet() + value
        estimate = null
    }
    AlertDialog(onDismissRequest = { if (!busy) onDismiss() }, title = { Text(root.getString("name")) },
        text = { Column(Modifier.heightIn(max = 490.dp).verticalScroll(rememberScrollState())) {
            Text(stringResource(R.string.lan_folders_hint))
            Row { Checkbox(checked = "" in selection, onCheckedChange = { select("") }, enabled = !busy); Text(stringResource(R.string.lan_whole_root), modifier = Modifier.padding(top = 12.dp)) }
            Text("/${path}")
            if (path.isNotEmpty()) TextButton(onClick = { path = path.substringBeforeLast('/', "") }, enabled = !busy) { Text("← ..") }
            for (child in children) {
                val rel = if (path.isEmpty()) child else "$path/$child"
                Row(Modifier.fillMaxWidth()) {
                    Checkbox(checked = selection.any { it.isEmpty() || rel == it || rel.startsWith("$it/") }, onCheckedChange = { select(rel) }, enabled = !busy)
                    TextButton(enabled = !busy, onClick = { path = rel }) { Text("$child  ›") }
                }
            }
            Text(selection.joinToString("\n") { it.ifEmpty { "/" } })
            TextButton(enabled = !busy && selection.isNotEmpty(), onClick = { scope.launch {
                busy = true; try { val plan = lan.estimate(root.getString("rootId"), selection); estimate = context.getString(R.string.lan_estimate_format, plan.getInt("total"), plan.getLong("bytes")) }
                catch (e: Exception) { onError(e.message.orEmpty()) } finally { busy = false }
            } }) { Text(stringResource(R.string.lan_estimate)) }
            estimate?.let { Text(it) }
            TextButton(enabled = !busy, onClick = {
                if (ContextCompat.checkSelfPermission(context, audioPermission) == PackageManager.PERMISSION_GRANTED) picker.launch(target) else permission.launch(audioPermission)
            }, modifier = Modifier.testTag("lan_pick_target")) { Text(stringResource(R.string.lan_target)) }
            target?.let { Text(targetLabel(it)); Text(stringResource(R.string.lan_target_child, "JeekMusic-${root.getString("rootId").take(8)}…"), style = MaterialTheme.typography.bodySmall) }
            Row { Checkbox(exclusive, { exclusive = it }); Text(stringResource(R.string.lan_exclusive), Modifier.clickable { exclusive = !exclusive }) }
            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        } },
        confirmButton = { TextButton(enabled = !busy && selection.isNotEmpty() && target != null && exclusive, onClick = { scope.launch {
            busy = true; try { lan.saveMapping(root, selection, target!!, exclusive); onDismiss() } catch (e: Exception) { onError(e.message.orEmpty()) } finally { busy = false }
        } }) { Text(stringResource(R.string.save)) } }, dismissButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

private fun targetLabel(uri: Uri): String = runCatching {
    val id = DocumentsContract.getTreeDocumentId(uri)
    val volume = id.substringBefore(':')
    val path = id.substringAfter(':', "").trimEnd('/')
    if (path.isNotEmpty()) path else volume
}.getOrDefault(uri.lastPathSegment.orEmpty())
