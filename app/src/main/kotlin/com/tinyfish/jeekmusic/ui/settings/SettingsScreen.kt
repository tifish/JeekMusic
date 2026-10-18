package com.tinyfish.jeekmusic.ui.settings

import androidx.compose.foundation.lazy.rememberLazyListState
import com.tinyfish.jeekmusic.ui.components.verticalScrollbar
import com.tinyfish.jeekmusic.update.AppUpdater
import com.tinyfish.jeekmusic.update.CheckResult
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
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.core.net.toUri
import android.os.Environment
import android.provider.Settings as SystemSettings
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.LifecycleResumeEffect
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import com.tinyfish.jeekmusic.data.backup.BackupManager
import com.tinyfish.jeekmusic.ui.navigation.MessageBus
import com.tinyfish.jeekmusic.ui.navigation.UiMessage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.text.DateFormat
import java.util.Date
import com.tinyfish.jeekmusic.analysis.AnalysisScheduler
import com.tinyfish.jeekmusic.analysis.TagRepository
import com.tinyfish.jeekmusic.analysis.Sensitivity
import androidx.compose.material3.Switch
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import com.tinyfish.jeekmusic.data.settings.AnalysisMode
import com.tinyfish.jeekmusic.data.settings.CoverSaveMode
import com.tinyfish.jeekmusic.data.settings.OnlineSource
import com.tinyfish.jeekmusic.data.online.SourceCheck
import com.tinyfish.jeekmusic.ui.components.sourceName
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import kotlinx.coroutines.flow.MutableStateFlow
import com.tinyfish.jeekmusic.data.settings.FetchMode
import com.tinyfish.jeekmusic.data.online.DownloadKind
import com.tinyfish.jeekmusic.data.online.FetchProgress
import com.tinyfish.jeekmusic.data.online.OnlineFetchScheduler
import com.tinyfish.jeekmusic.data.library.EmbedDownloadedScheduler
import androidx.compose.runtime.LaunchedEffect
import com.tinyfish.jeekmusic.data.settings.LyricsSaveLocation
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
    private val tags: TagRepository,
    private val analysis: AnalysisScheduler,
    private val lyrics: LyricsRepository,
    private val updater: AppUpdater,
    private val onlineFetch: OnlineFetchScheduler,
    private val sourceCheck: SourceCheck,
    private val embedDownloaded: EmbedDownloadedScheduler,
) : ViewModel() {
    /** 勾选 / 取消在线来源；至少保留一个（需求 LY-8、C-3）。 */
    fun toggleLyricsSource(source: OnlineSource) = viewModelScope.launch {
        val current = settings.value.lyricsSources
        val next = if (source in current) current - source else current + source
        if (next.isNotEmpty()) settingsRepository.setLyricsSources(next)
    }

    fun toggleCoverSource(source: OnlineSource) = viewModelScope.launch {
        val current = settings.value.coverSources
        val next = if (source in current) current - source else current + source
        if (next.isNotEmpty()) settingsRepository.setCoverSources(next)
    }

    /** 连接检测：null 表示没在显示；结果为 null 表示正在检测。 */
    private val _sourceCheck = MutableStateFlow<SourceCheckState?>(null)
    val sourceCheckState: StateFlow<SourceCheckState?> = _sourceCheck

    fun checkSources(sources: List<OnlineSource>) {
        _sourceCheck.value = SourceCheckState(null)
        viewModelScope.launch {
            val results = sourceCheck.check(sources)
            // 检测期间关掉了对话框就不再显示
            if (_sourceCheck.value != null) _sourceCheck.value = SourceCheckState(results)
        }
    }

    fun dismissSourceCheck() {
        _sourceCheck.value = null
    }

    fun setAutoUpdateCheck(enabled: Boolean) = viewModelScope.launch { settingsRepository.setAutoUpdateCheck(enabled) }

    /** 手动检查更新；有新版本时由最外层的更新提示接管（需求 U-6）。 */
    fun checkUpdate() = viewModelScope.launch {
        when (updater.checkNow()) {
            CheckResult.UP_TO_DATE -> messages.post(UiMessage(R.string.update_up_to_date))
            CheckResult.FAILED -> messages.post(UiMessage(R.string.update_check_failed))
            CheckResult.UPDATE_AVAILABLE -> Unit
        }
    }

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

    val pendingAnalysis: StateFlow<Int> = tags.pendingCount()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), 0)

    fun setAnalysisMode(mode: AnalysisMode) = viewModelScope.launch {
        settingsRepository.setAnalysisMode(mode)
        analysis.schedule()
    }

    /** 调整灵敏度后立即生效，不需要重新分析音频（需求 M-8）。 */
    fun setSensitivity(sensitivity: Sensitivity) = viewModelScope.launch {
        settingsRepository.setSensitivity(sensitivity)
        tags.recomputeAll()
    }

    fun analyzeNow() = viewModelScope.launch { analysis.runNow() }

    fun setOnlineLyrics(enabled: Boolean) = viewModelScope.launch {
        settingsRepository.setOnlineLyrics(enabled)
        if (enabled) onlineFetch.scheduleAuto() else onlineFetch.cancel(DownloadKind.LYRICS)
    }
    fun setLyricsFetchMode(mode: FetchMode) = viewModelScope.launch {
        settingsRepository.setLyricsFetchMode(mode)
        onlineFetch.onModeChanged()
    }
    fun setLyricsSaveLocation(location: LyricsSaveLocation) = viewModelScope.launch {
        settingsRepository.setLyricsSaveLocation(location)
        embedDownloaded.schedule()
    }
    fun setOnlineCovers(enabled: Boolean) = viewModelScope.launch {
        settingsRepository.setOnlineCovers(enabled)
        if (enabled) onlineFetch.scheduleAuto() else onlineFetch.cancel(DownloadKind.COVERS)
    }
    fun setCoverFetchMode(mode: FetchMode) = viewModelScope.launch {
        settingsRepository.setCoverFetchMode(mode)
        onlineFetch.onModeChanged()
    }
    fun setCoverSaveMode(mode: CoverSaveMode) = viewModelScope.launch {
        settingsRepository.setCoverSaveMode(mode)
        embedDownloaded.schedule()
    }

    /** 选择写入文件后去授予了"所有文件访问权限"：把已下载的写进去（需求 C-7、LY-14）。 */
    fun onAllFilesAccessGranted() = viewModelScope.launch { embedDownloaded.schedule() }

    val coverProgress: StateFlow<FetchProgress?> = onlineFetch.progress(DownloadKind.COVERS)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)
    val lyricsProgress: StateFlow<FetchProgress?> = onlineFetch.progress(DownloadKind.LYRICS)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    /** 正在获取时再点一下是停止；在等网络时点一下是立即用当前网络获取。 */
    fun toggleFetch(kind: DownloadKind) = viewModelScope.launch {
        val progress = if (kind == DownloadKind.COVERS) coverProgress.value else lyricsProgress.value
        if (progress?.running == true) onlineFetch.cancel(kind) else onlineFetch.start(kind)
    }

    fun clearLyricsCache() = viewModelScope.launch {
        lyrics.clearCache()
        messages.post(UiMessage(R.string.lyrics_cache_cleared))
    }
}

@Composable
fun SettingsScreen(onBack: () -> Unit, viewModel: SettingsViewModel = hiltViewModel()) {
    val folders by viewModel.folders.collectAsStateWithLifecycle()
    val settings by viewModel.settings.collectAsStateWithLifecycle()
    val scanState by viewModel.scanState.collectAsStateWithLifecycle()
    val addFolder = rememberAddFolder()
    val navigator = LocalNavigator.current
    val lastBackup by viewModel.lastBackup.collectAsStateWithLifecycle()
    val pendingAnalysis by viewModel.pendingAnalysis.collectAsStateWithLifecycle()
    val coverProgress by viewModel.coverProgress.collectAsStateWithLifecycle()
    val lyricsProgress by viewModel.lyricsProgress.collectAsStateWithLifecycle()
    val sourceCheckState by viewModel.sourceCheckState.collectAsStateWithLifecycle()
    val pickBackup = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) viewModel.restoreFrom(uri)
    }
    var removing by remember { mutableStateOf<LibraryFolderEntity?>(null) }
    var confirmOnline by remember { mutableStateOf(false) }
    var confirmCovers by remember { mutableStateOf(false) }
    // 对整个曲库获取缺失的封面 / 歌词前先确认（需求 C-3、LY-8）
    var confirmFetch by remember { mutableStateOf<DownloadKind?>(null) }
    fun fetchClicked(kind: DownloadKind, progress: FetchProgress?) {
        if (progress?.running == true) viewModel.toggleFetch(kind) else confirmFetch = kind
    }
    // 选择"写入音乐文件"但还没有"所有文件访问权限"时：说明后去授权，[askAllFiles] 是确认后要应用的选择
    var askAllFiles by remember { mutableStateOf<(() -> Unit)?>(null) }
    val allFilesAccess = rememberAllFilesAccess()
    LaunchedEffect(allFilesAccess) { if (allFilesAccess) viewModel.onAllFilesAccessGranted() }
    val context = LocalContext.current
    var showLicenses by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxSize().systemBarsPadding().testTag("settings")) {
        SubScreenHeader(title = stringResource(R.string.settings_title), onBack = onBack)
        val listState = rememberLazyListState()
        LazyColumn(Modifier.fillMaxSize().verticalScrollbar(listState, draggable = true), state = listState) {
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

            item { SectionHeader(stringResource(R.string.settings_lyrics)) }
            item {
                SwitchRow(
                    title = stringResource(R.string.online_lyrics),
                    summary = stringResource(R.string.online_lyrics_summary),
                    checked = settings.onlineLyrics,
                    // 开启时先说明会发送哪些信息、发给谁（需求 LY-9）
                    onChange = { if (it) confirmOnline = true else viewModel.setOnlineLyrics(false) },
                    modifier = Modifier.testTag("online_lyrics"),
                )
            }
            if (settings.onlineLyrics) {
                item { Text(stringResource(R.string.online_sources), Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodyLarge) }
                items(OnlineSource.FOR_LYRICS) { source ->
                    SourceRow(source, checked = source in settings.lyricsSources, onClick = { viewModel.toggleLyricsSource(source) }, modifier = Modifier.testTag("lyrics_source_${source.name}"))
                }
                item {
                    SettingItem(
                        title = stringResource(R.string.source_check),
                        summary = stringResource(R.string.source_check_summary),
                        onClick = { viewModel.checkSources(OnlineSource.FOR_LYRICS) },
                        modifier = Modifier.testTag("check_lyrics_sources"),
                    )
                }
                item {
                    val progress = lyricsProgress
                    SettingItem(
                        title = stringResource(R.string.lyrics_fetch_missing),
                        summary = when {
                            progress == null -> stringResource(R.string.lyrics_fetch_summary)
                            progress.waiting -> stringResource(R.string.cover_fetch_waiting)
                            progress.checking -> stringResource(R.string.lyrics_fetch_checking)
                            progress.running -> stringResource(R.string.cover_fetch_progress, progress.done, progress.total, progress.found)
                            else -> stringResource(R.string.lyrics_fetch_done, progress.found, progress.missing)
                        },
                        onClick = { fetchClicked(DownloadKind.LYRICS, progress) },
                        modifier = Modifier.testTag("fetch_lyrics"),
                    )
                }
                item { Text(stringResource(R.string.lyrics_fetch_mode), Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodyLarge) }
                items(FetchMode.entries) { mode ->
                    RadioRow(
                        selected = settings.lyricsFetchMode == mode,
                        text = stringResource(
                            when (mode) {
                                FetchMode.MANUAL -> R.string.lyrics_fetch_manual
                                FetchMode.AUTO -> R.string.lyrics_fetch_auto
                                FetchMode.WIFI -> R.string.lyrics_fetch_wifi
                            },
                        ),
                        summary = if (mode == FetchMode.MANUAL) null else stringResource(R.string.auto_fetch_lyrics_summary),
                        onClick = { viewModel.setLyricsFetchMode(mode) },
                        modifier = Modifier.testTag("lyrics_fetch_${mode.name}"),
                    )
                }
                item { Text(stringResource(R.string.lyrics_save_location), Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodyLarge) }
                items(LyricsSaveLocation.entries) { location ->
                    val (title, summary) = when (location) {
                        LyricsSaveLocation.SIDECAR -> R.string.lyrics_save_folder to R.string.lyrics_save_folder_summary
                        LyricsSaveLocation.HIDDEN_FOLDER -> R.string.lyrics_save_hidden to R.string.lyrics_save_hidden_summary
                        LyricsSaveLocation.EMBED -> R.string.lyrics_save_embed to R.string.lyrics_save_embed_summary
                        LyricsSaveLocation.APP_CACHE -> R.string.lyrics_save_app to null
                    }
                    val embed = location == LyricsSaveLocation.EMBED
                    val missing = embed && settings.lyricsSaveLocation == location && !allFilesAccess
                    RadioRow(
                        selected = settings.lyricsSaveLocation == location,
                        text = stringResource(title),
                        summary = if (missing) stringResource(R.string.all_files_missing) else summary?.let { stringResource(it) },
                        warning = missing,
                        onClick = {
                            // 写入文件要"所有文件访问权限"才能保持修改时间不变，先说明再去授权
                            if (embed && !allFilesAccess) askAllFiles = { viewModel.setLyricsSaveLocation(location) } else viewModel.setLyricsSaveLocation(location)
                        },
                        modifier = Modifier.testTag("lyrics_save_${location.name}"),
                    )
                }
                item { SettingItem(title = stringResource(R.string.lyrics_clear_cache), onClick = viewModel::clearLyricsCache) }
            }

            item { SectionHeader(stringResource(R.string.settings_covers)) }
            item {
                SwitchRow(
                    title = stringResource(R.string.online_covers),
                    summary = stringResource(R.string.online_covers_summary),
                    checked = settings.onlineCovers,
                    // 开启时先说明会发送哪些信息、发给谁（需求 C-3）
                    onChange = { if (it) confirmCovers = true else viewModel.setOnlineCovers(false) },
                    modifier = Modifier.testTag("online_covers"),
                )
            }
            if (settings.onlineCovers) {
                item { Text(stringResource(R.string.online_sources), Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodyLarge) }
                items(OnlineSource.FOR_COVERS) { source ->
                    SourceRow(source, checked = source in settings.coverSources, onClick = { viewModel.toggleCoverSource(source) }, modifier = Modifier.testTag("cover_source_${source.name}"))
                }
                item {
                    SettingItem(
                        title = stringResource(R.string.source_check),
                        summary = stringResource(R.string.source_check_summary),
                        onClick = { viewModel.checkSources(OnlineSource.FOR_COVERS) },
                        modifier = Modifier.testTag("check_cover_sources"),
                    )
                }
                item {
                    val progress = coverProgress
                    SettingItem(
                        title = stringResource(R.string.cover_fetch_missing),
                        summary = when {
                            progress == null -> stringResource(R.string.cover_fetch_summary)
                            progress.waiting -> stringResource(R.string.cover_fetch_waiting)
                            progress.checking -> stringResource(R.string.cover_fetch_checking)
                            progress.running -> stringResource(R.string.cover_fetch_progress, progress.done, progress.total, progress.found)
                            else -> stringResource(R.string.cover_fetch_done, progress.found, progress.missing)
                        },
                        onClick = { fetchClicked(DownloadKind.COVERS, progress) },
                        modifier = Modifier.testTag("fetch_covers"),
                    )
                }
                item { Text(stringResource(R.string.cover_fetch_mode), Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodyLarge) }
                items(FetchMode.entries) { mode ->
                    RadioRow(
                        selected = settings.coverFetchMode == mode,
                        text = stringResource(
                            when (mode) {
                                FetchMode.MANUAL -> R.string.cover_fetch_manual
                                FetchMode.AUTO -> R.string.cover_fetch_auto
                                FetchMode.WIFI -> R.string.cover_fetch_wifi
                            },
                        ),
                        onClick = { viewModel.setCoverFetchMode(mode) },
                        modifier = Modifier.testTag("cover_fetch_${mode.name}"),
                    )
                }
                item { Text(stringResource(R.string.cover_save_mode), Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodyLarge) }
                items(CoverSaveMode.entries) { mode ->
                    val (title, summary) = when (mode) {
                        CoverSaveMode.HIDDEN_FOLDER -> R.string.cover_save_hidden to R.string.cover_save_hidden_summary
                        CoverSaveMode.EMBED -> R.string.cover_save_embed to R.string.cover_save_embed_summary
                        CoverSaveMode.SIDECAR -> R.string.cover_save_sidecar to R.string.cover_save_sidecar_summary
                    }
                    val embed = mode == CoverSaveMode.EMBED
                    val missing = embed && settings.coverSaveMode == mode && !allFilesAccess
                    RadioRow(
                        selected = settings.coverSaveMode == mode,
                        text = stringResource(title),
                        summary = stringResource(if (missing) R.string.all_files_missing else summary),
                        warning = missing,
                        onClick = { if (embed && !allFilesAccess) askAllFiles = { viewModel.setCoverSaveMode(mode) } else viewModel.setCoverSaveMode(mode) },
                        modifier = Modifier.testTag("cover_mode_${mode.name}"),
                    )
                }
            }

            item { SectionHeader(stringResource(R.string.settings_moods)) }
            item {
                SettingItem(
                    title = stringResource(R.string.mood_analyze_now),
                    summary = pluralStringResource(R.plurals.mood_pending, pendingAnalysis, pendingAnalysis),
                    onClick = viewModel::analyzeNow,
                    modifier = Modifier.testTag("settings_analyze_now"),
                )
            }
            item { Text(stringResource(R.string.settings_analysis_mode), Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodyLarge) }
            items(AnalysisMode.entries) { mode ->
                RadioRow(
                    selected = settings.analysisMode == mode,
                    text = stringResource(if (mode == AnalysisMode.CHARGING) R.string.analysis_charging else R.string.analysis_anytime),
                    onClick = { viewModel.setAnalysisMode(mode) },
                )
            }
            item { Text(stringResource(R.string.settings_sensitivity), Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodyLarge) }
            items(Sensitivity.entries) { s ->
                RadioRow(
                    selected = settings.sensitivity == s,
                    text = stringResource(
                        when (s) {
                            Sensitivity.LOOSE -> R.string.sensitivity_loose
                            Sensitivity.NORMAL -> R.string.sensitivity_normal
                            Sensitivity.STRICT -> R.string.sensitivity_strict
                        },
                    ),
                    onClick = { viewModel.setSensitivity(s) },
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
                SettingItem(
                    title = stringResource(R.string.update_check_now),
                    summary = stringResource(R.string.settings_version, BuildConfig.VERSION_NAME),
                    onClick = viewModel::checkUpdate,
                    modifier = Modifier.testTag("check_update"),
                )
            }
            item {
                SwitchRow(
                    title = stringResource(R.string.update_auto_check),
                    summary = stringResource(R.string.update_auto_check_summary),
                    checked = settings.autoUpdateCheck,
                    onChange = viewModel::setAutoUpdateCheck,
                    modifier = Modifier.testTag("auto_update"),
                )
            }
            item { SettingItem(title = stringResource(R.string.settings_licenses), onClick = { showLicenses = true }) }
            item { SettingItem(title = stringResource(R.string.settings_source), summary = "github.com/tifish/JeekMusic") }
        }
    }

    if (showLicenses) LicensesDialog(onDismiss = { showLicenses = false })

    sourceCheckState?.let { state ->
        AlertDialog(
            onDismissRequest = viewModel::dismissSourceCheck,
            title = { Text(stringResource(R.string.source_check)) },
            text = {
                val results = state.results
                if (results == null) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(24.dp))
                        Spacer(Modifier.width(16.dp))
                        Text(stringResource(R.string.source_checking))
                    }
                } else {
                    Column {
                        results.forEach { r ->
                            Row(Modifier.fillMaxWidth().padding(vertical = 4.dp)) {
                                Text(
                                    if (r.imageHost) stringResource(R.string.source_images, sourceName(r.source)) else sourceName(r.source),
                                    Modifier.weight(1f),
                                )
                                Text(
                                    r.millis?.let { stringResource(R.string.source_ok, it) } ?: stringResource(R.string.source_failed),
                                    color = if (r.millis == null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
                                )
                            }
                        }
                        Text(stringResource(R.string.source_check_hint), Modifier.padding(top = 8.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            },
            confirmButton = { TextButton(onClick = viewModel::dismissSourceCheck) { Text(stringResource(R.string.confirm)) } },
        )
    }

    askAllFiles?.let { apply ->
        AlertDialog(
            onDismissRequest = { askAllFiles = null },
            title = { Text(stringResource(R.string.all_files_title)) },
            text = { Text(stringResource(R.string.all_files_message)) },
            confirmButton = {
                TextButton(onClick = {
                    apply()
                    askAllFiles = null
                    openAllFilesSettings(context)
                }) { Text(stringResource(R.string.all_files_grant)) }
            },
            dismissButton = { TextButton(onClick = { askAllFiles = null }) { Text(stringResource(R.string.cancel)) } },
        )
    }

    confirmFetch?.let { kind ->
        val covers = kind == DownloadKind.COVERS
        val embed = if (covers) settings.coverSaveMode == CoverSaveMode.EMBED else settings.lyricsSaveLocation == LyricsSaveLocation.EMBED
        AlertDialog(
            onDismissRequest = { confirmFetch = null },
            title = { Text(stringResource(if (covers) R.string.cover_fetch_confirm_title else R.string.lyrics_fetch_confirm_title)) },
            text = {
                Text(
                    stringResource(if (covers) R.string.cover_fetch_confirm else R.string.lyrics_fetch_confirm) +
                        if (embed) "\n\n" + stringResource(R.string.fetch_confirm_embed) else "",
                )
            },
            confirmButton = {
                TextButton(onClick = { viewModel.toggleFetch(kind); confirmFetch = null }, modifier = Modifier.testTag("confirm_fetch")) {
                    Text(stringResource(R.string.fetch_confirm_start))
                }
            },
            dismissButton = { TextButton(onClick = { confirmFetch = null }) { Text(stringResource(R.string.cancel)) } },
        )
    }

    if (confirmCovers) {
        AlertDialog(
            onDismissRequest = { confirmCovers = false },
            title = { Text(stringResource(R.string.online_covers_privacy_title)) },
            text = { Text(stringResource(R.string.online_covers_privacy)) },
            confirmButton = { TextButton(onClick = { viewModel.setOnlineCovers(true); confirmCovers = false }) { Text(stringResource(R.string.confirm)) } },
            dismissButton = { TextButton(onClick = { confirmCovers = false }) { Text(stringResource(R.string.cancel)) } },
        )
    }

    if (confirmOnline) {
        AlertDialog(
            onDismissRequest = { confirmOnline = false },
            title = { Text(stringResource(R.string.online_lyrics_privacy_title)) },
            text = { Text(stringResource(R.string.online_lyrics_privacy)) },
            confirmButton = { TextButton(onClick = { viewModel.setOnlineLyrics(true); confirmOnline = false }) { Text(stringResource(R.string.confirm)) } },
            dismissButton = { TextButton(onClick = { confirmOnline = false }) { Text(stringResource(R.string.cancel)) } },
        )
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
private fun SwitchRow(title: String, summary: String?, checked: Boolean, onChange: (Boolean) -> Unit, modifier: Modifier = Modifier) {
    Row(
        modifier.fillMaxWidth().clickable { onChange(!checked) }.padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            if (summary != null) Text(summary, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Switch(checked = checked, onCheckedChange = onChange)
    }
}

@Composable
private fun RadioRow(
    selected: Boolean,
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    summary: String? = null,
    warning: Boolean = false,
) {
    val context = LocalContext.current
    // 有警告（缺少权限）时点一下去授权
    val click = if (warning) ({ openAllFilesSettings(context) }) else onClick
    Row(
        modifier.fillMaxWidth().clickable(onClick = click).padding(horizontal = 16.dp, vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(selected = selected, onClick = click)
        Column {
            Text(text)
            if (summary != null) {
                Text(
                    summary,
                    style = MaterialTheme.typography.bodySmall,
                    color = if (warning) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/** 一个在线来源：勾选框、名称和国内访问情况的说明。 */
@Composable
private fun SourceRow(source: OnlineSource, checked: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier) {
    Row(
        modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Checkbox(checked = checked, onCheckedChange = { onClick() })
        Column {
            Text(sourceName(source))
            Text(
                stringResource(
                    when (source) {
                        OnlineSource.NETEASE -> R.string.source_netease_summary
                        OnlineSource.QQ -> R.string.source_qq_summary
                        OnlineSource.ITUNES -> R.string.source_itunes_summary
                        OnlineSource.MUSICBRAINZ -> R.string.source_musicbrainz_summary
                        OnlineSource.LRCLIB -> R.string.source_lrclib_summary
                    },
                ),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** 连接检测的状态：[results] 为 null 表示正在检测。 */
data class SourceCheckState(val results: List<SourceCheck.Result>?)

/** 是否有"所有文件访问权限"；从系统设置返回时重新检查。 */
@Composable
private fun rememberAllFilesAccess(): Boolean {
    var granted by remember { mutableStateOf(Environment.isExternalStorageManager()) }
    LifecycleResumeEffect(Unit) {
        granted = Environment.isExternalStorageManager()
        onPauseOrDispose {}
    }
    return granted
}

private fun openAllFilesSettings(context: Context) {
    val intent = Intent(SystemSettings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, "package:${context.packageName}".toUri())
    runCatching { context.startActivity(intent) }
        .onFailure { runCatching { context.startActivity(Intent(SystemSettings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION)) } }
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
