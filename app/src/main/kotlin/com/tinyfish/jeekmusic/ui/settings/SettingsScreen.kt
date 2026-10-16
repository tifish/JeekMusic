package com.tinyfish.jeekmusic.ui.settings

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.BuildConfig
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.FolderWithCount
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.library.LibraryRepository
import com.tinyfish.jeekmusic.data.scanner.LibraryScanner
import com.tinyfish.jeekmusic.data.scanner.ScanState
import com.tinyfish.jeekmusic.data.settings.Settings
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import com.tinyfish.jeekmusic.data.backup.BackupManager
import com.tinyfish.jeekmusic.ui.navigation.MessageBus
import com.tinyfish.jeekmusic.ui.navigation.UiMessage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.text.DateFormat
import java.util.Date
import com.tinyfish.jeekmusic.data.settings.LowPowerMode
import com.tinyfish.jeekmusic.data.settings.ThemeMode
import com.tinyfish.jeekmusic.ui.navigation.LocalNavigator
import com.tinyfish.jeekmusic.ui.navigation.TopRoute
import com.tinyfish.jeekmusic.ui.components.SectionHeader
import com.tinyfish.jeekmusic.ui.components.SubScreenHeader
import com.tinyfish.jeekmusic.ui.components.songCount
import com.tinyfish.jeekmusic.ui.folders.rememberAddFolder
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

@HiltViewModel
class SettingsViewModel @Inject constructor(
    private val library: LibraryRepository,
    private val scanner: LibraryScanner,
    private val settingsRepository: SettingsRepository,
    private val backups: BackupManager,
    private val messages: MessageBus,
) : ViewModel() {
    val lastBackup: StateFlow<Long?> = backups.lastBackupTime
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    fun backupNow() = viewModelScope.launch {
        val written = backups.backupNow(force = true)
        messages.post(UiMessage(if (written > 0) R.string.backup_done else R.string.backup_failed))
    }

    /** "从备份恢复"：用户选择任意备份文件（需求 B-4）。 */
    fun restoreFrom(uri: Uri) = viewModelScope.launch {
        val backup = withContext(Dispatchers.IO) { backups.read(uri) }
        if (backup == null) {
            messages.post(UiMessage(R.string.restore_invalid))
            return@launch
        }
        val report = backups.restore(backup)
        messages.post(UiMessage(R.string.restore_done, "${report.playlists} / ${report.items} / ${report.missing}"))
    }

    val folders: StateFlow<List<FolderWithCount>> = library.folders
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())
    val settings: StateFlow<Settings> = settingsRepository.settings
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), Settings())
    val scanState: StateFlow<ScanState> = scanner.state

    fun removeFolder(folder: LibraryFolderEntity) = viewModelScope.launch { library.removeFolder(folder) }
    fun rescan() = scanner.requestScan(full = false, recheckCue = true)
    fun rebuild() = scanner.requestScan(full = true)
    fun setTheme(mode: ThemeMode) = viewModelScope.launch { settingsRepository.setThemeMode(mode) }
    fun setLowPower(mode: LowPowerMode) = viewModelScope.launch { settingsRepository.setLowPowerMode(mode) }
}

@Composable
fun SettingsScreen(onBack: () -> Unit, viewModel: SettingsViewModel = hiltViewModel()) {
    val folders by viewModel.folders.collectAsStateWithLifecycle()
    val settings by viewModel.settings.collectAsStateWithLifecycle()
    val scanState by viewModel.scanState.collectAsStateWithLifecycle()
    val addFolder = rememberAddFolder()
    val navigator = LocalNavigator.current
    val lastBackup by viewModel.lastBackup.collectAsStateWithLifecycle()
    val pickBackup = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) viewModel.restoreFrom(uri)
    }
    var removing by remember { mutableStateOf<LibraryFolderEntity?>(null) }

    Column(Modifier.fillMaxSize().systemBarsPadding().testTag("settings")) {
        SubScreenHeader(title = stringResource(R.string.settings_title), onBack = onBack)
        LazyColumn(Modifier.fillMaxSize()) {
            item { SectionHeader(stringResource(R.string.settings_library)) }
            items(folders, key = { it.folder.id }) { (folder, count) ->
                Row(
                    Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp, top = 4.dp, bottom = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Icon(painterResource(R.drawable.ic_folder), contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                    Spacer(Modifier.width(16.dp))
                    Column(Modifier.weight(1f)) {
                        Text(folder.displayName, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            "${folder.relativePath.ifEmpty { "/" }} · ${songCount(count)}",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    IconButton(onClick = { removing = folder }) {
                        Icon(painterResource(R.drawable.ic_delete), contentDescription = stringResource(R.string.action_remove))
                    }
                }
            }
            item {
                SettingItem(
                    title = stringResource(R.string.action_add_folder),
                    icon = R.drawable.ic_add,
                    onClick = addFolder,
                    modifier = Modifier.testTag("settings_add_folder"),
                )
            }
            item {
                val running = scanState as? ScanState.Running
                SettingItem(
                    title = stringResource(R.string.settings_rescan),
                    summary = running?.let { stringResource(R.string.scan_progress, it.done, it.total) }
                        ?: stringResource(R.string.settings_rescan_summary),
                    icon = R.drawable.ic_refresh,
                    onClick = viewModel::rescan,
                )
            }
            item {
                SettingItem(
                    title = stringResource(R.string.settings_rebuild),
                    summary = stringResource(R.string.settings_rebuild_summary),
                    onClick = viewModel::rebuild,
                )
            }
            if (settings.unsupportedFileCount > 0) {
                item {
                    Text(
                        pluralStringResource(R.plurals.settings_unsupported_files, settings.unsupportedFileCount, settings.unsupportedFileCount),
                        Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            item {
                SettingItem(
                    title = stringResource(R.string.settings_artists),
                    summary = stringResource(R.string.settings_artists_summary),
                    icon = R.drawable.ic_person,
                    onClick = { navigator.open(TopRoute.ArtistSettings) },
                    modifier = Modifier.testTag("open_artist_settings"),
                )
            }

            item { SectionHeader(stringResource(R.string.settings_backup)) }
            item {
                SettingItem(
                    title = stringResource(R.string.backup_now),
                    summary = lastBackup?.let {
                        stringResource(R.string.backup_last, DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT).format(Date(it)))
                    } ?: stringResource(R.string.backup_summary),
                    onClick = viewModel::backupNow,
                    modifier = Modifier.testTag("backup_now"),
                )
            }
            item {
                SettingItem(
                    title = stringResource(R.string.restore_from_file),
                    onClick = { pickBackup.launch(arrayOf("application/json", "application/octet-stream", "*/*")) },
                    modifier = Modifier.testTag("restore_from_file"),
                )
            }

            item { SectionHeader(stringResource(R.string.settings_playback)) }
            item { Text(stringResource(R.string.settings_low_power), Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodyLarge) }
            items(LowPowerMode.entries) { mode ->
                Row(
                    Modifier.fillMaxWidth().clickable { viewModel.setLowPower(mode) }.padding(horizontal = 16.dp, vertical = 2.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    RadioButton(selected = settings.lowPowerMode == mode, onClick = { viewModel.setLowPower(mode) })
                    Column {
                        Text(
                            stringResource(
                                when (mode) {
                                    LowPowerMode.BACKGROUND -> R.string.low_power_background
                                    LowPowerMode.ALWAYS -> R.string.low_power_always
                                    LowPowerMode.OFF -> R.string.low_power_off
                                },
                            ),
                        )
                        if (mode == LowPowerMode.ALWAYS) {
                            Text(stringResource(R.string.low_power_always_summary), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }

            item { SectionHeader(stringResource(R.string.settings_appearance)) }
            item {
                SettingItem(
                    title = stringResource(R.string.settings_tabs),
                    summary = stringResource(R.string.settings_tabs_summary),
                    icon = R.drawable.ic_push_pin,
                    onClick = { navigator.open(TopRoute.TabManager) },
                    modifier = Modifier.testTag("open_tab_manager"),
                )
            }
            items(ThemeMode.entries) { mode ->
                Row(
                    Modifier.fillMaxWidth().clickable { viewModel.setTheme(mode) }.padding(horizontal = 16.dp, vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    RadioButton(selected = settings.themeMode == mode, onClick = { viewModel.setTheme(mode) })
                    Text(
                        stringResource(
                            when (mode) {
                                ThemeMode.SYSTEM -> R.string.theme_system
                                ThemeMode.LIGHT -> R.string.theme_light
                                ThemeMode.DARK -> R.string.theme_dark
                            },
                        ),
                    )
                }
            }

            item { SectionHeader(stringResource(R.string.settings_about)) }
            item {
                SettingItem(title = stringResource(R.string.app_name), summary = stringResource(R.string.settings_version, BuildConfig.VERSION_NAME))
            }
        }
    }

    removing?.let { folder ->
        AlertDialog(
            onDismissRequest = { removing = null },
            title = { Text(stringResource(R.string.settings_remove_folder_title)) },
            text = { Text(stringResource(R.string.settings_remove_folder_message, folder.displayName)) },
            confirmButton = {
                TextButton(onClick = { viewModel.removeFolder(folder); removing = null }) { Text(stringResource(R.string.action_remove)) }
            },
            dismissButton = { TextButton(onClick = { removing = null }) { Text(stringResource(R.string.cancel)) } },
        )
    }
}

@Composable
fun SettingItem(
    title: String,
    modifier: Modifier = Modifier,
    summary: String? = null,
    icon: Int? = null,
    onClick: (() -> Unit)? = null,
) {
    Row(
        modifier
            .fillMaxWidth()
            .let { if (onClick != null) it.clickable(onClick = onClick) else it }
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (icon != null) {
            Icon(painterResource(icon), contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.width(16.dp))
        }
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            if (summary != null) {
                Text(summary, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}
