package com.tinyfish.jeekmusic.ui.main

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.analysis.AnalysisScheduler
import com.tinyfish.jeekmusic.data.backup.BackupManager
import com.tinyfish.jeekmusic.data.scanner.LibraryObserver
import com.tinyfish.jeekmusic.data.scanner.LibraryScanner
import com.tinyfish.jeekmusic.data.scanner.ScanState
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.data.settings.ThemeMode
import com.tinyfish.jeekmusic.data.tabs.TabRepository
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.startup.StartupSnapshot
import com.tinyfish.jeekmusic.ui.navigation.MessageBus
import com.tinyfish.jeekmusic.ui.navigation.Navigator
import com.tinyfish.jeekmusic.ui.navigation.Tab
import com.tinyfish.jeekmusic.ui.navigation.TabRoute
import com.tinyfish.jeekmusic.ui.navigation.TopRoute
import com.tinyfish.jeekmusic.ui.navigation.UiMessage
import dagger.hilt.android.lifecycle.HiltViewModel
import androidx.compose.runtime.snapshotFlow
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import javax.inject.Inject

@HiltViewModel
class MainViewModel @Inject constructor(
    private val settingsRepository: SettingsRepository,
    private val tabRepository: TabRepository,
    private val snapshot: StartupSnapshot,
    private val scanner: LibraryScanner,
    private val observer: LibraryObserver,
    private val player: PlayerConnection,
    private val backups: BackupManager,
    private val analysis: AnalysisScheduler,
    messageBus: MessageBus,
) : ViewModel(), Navigator {

    /** 启动快照读完之前保持启动画面（docs/design.md §9.2）。 */
    private val _ready = MutableStateFlow(false)
    val ready: StateFlow<Boolean> = _ready.asStateFlow()

    private val _themeMode = MutableStateFlow(ThemeMode.SYSTEM)
    val themeMode: StateFlow<ThemeMode> = _themeMode.asStateFlow()

    private val _tabs = MutableStateFlow<List<Tab>>(emptyList())
    val tabs: StateFlow<List<Tab>> = _tabs.asStateFlow()

    /** 当前选中标签的 key；界面据此计算 Pager 的页码。 */
    var currentTabKey by mutableStateOf<String?>(null)
        private set

    var nowPlayingOpen by mutableStateOf(false)

    val scanState: StateFlow<ScanState> = scanner.state
    val messages: SharedFlow<UiMessage> = messageBus.messages
    val playbackErrors: SharedFlow<String> = player.errors

    /** 顶层返回栈：主界面、设置等全屏页面。 */
    val topStack: SnapshotStateList<TopRoute> = mutableStateListOf(TopRoute.Main)

    /** 每个标签各自的返回栈，切换标签时保留浏览位置。 */
    private val tabStacks = mutableMapOf<String, SnapshotStateList<TabRoute>>()

    init {
        viewModelScope.launch {
            val saved = snapshot.read()
            _themeMode.value = saved.themeMode
            if (saved.tabs.isNotEmpty()) _tabs.value = saved.tabs
            currentTabKey = saved.lastTabKey
            player.applySnapshot(saved)
            _ready.value = true
            restoreFolderStack(saved.folderStack)
        }
        viewModelScope.launch {
            tabRepository.tabs.collect { tabs ->
                _tabs.value = tabs
                _ready.value = true
                snapshot.saveTabs(tabs)
            }
        }
        viewModelScope.launch {
            settingsRepository.settings.map { it.themeMode }.distinctUntilChanged().collect {
                _themeMode.value = it
                snapshot.saveTheme(it)
            }
        }
    }

    /**
     * 恢复"文件夹"标签里上次打开的子文件夹（需求 F-8），之后每次进出文件夹都保存。
     * 滚动位置由各个文件夹页面自己保存和恢复（[StartupSnapshot.folderScroll]）。
     */
    private suspend fun restoreFolderStack(saved: List<TabRoute.Folder>) {
        val stack = stackOf(Tab.Folders)
        if (stack.size == 1 && saved.isNotEmpty()) stack.addAll(saved)
        snapshotFlow { stack.filterIsInstance<TabRoute.Folder>() }
            .distinctUntilChanged()
            .collect { snapshot.saveFolderStack(it) }
    }

    fun stackOf(tab: Tab): SnapshotStateList<TabRoute> = tabStacks.getOrPut(tab.key) { mutableStateListOf(tab.root) }

    fun selectTab(tab: Tab) {
        if (currentTabKey == tab.key) return
        currentTabKey = tab.key
        viewModelScope.launch { snapshot.saveLastTab(tab.key) }
    }

    fun unpin(tab: Tab) = viewModelScope.launch { tabRepository.unpin(tab) }

    fun moveTab(from: Int, to: Int) = viewModelScope.launch { tabRepository.move(from, to) }

    private fun currentTab(): Tab? = _tabs.value.firstOrNull { it.key == currentTabKey } ?: _tabs.value.firstOrNull()

    private fun pushToCurrentTab(route: TabRoute) {
        nowPlayingOpen = false
        while (topStack.size > 1) topStack.removeAt(topStack.lastIndex)
        val tab = currentTab() ?: return
        val stack = stackOf(tab)
        if (stack.lastOrNull() != route) stack.add(route)
    }

    override fun openArtist(nameKey: String) = pushToCurrentTab(TabRoute.ArtistDetail(nameKey))

    override fun openAlbum(albumKey: String) = pushToCurrentTab(TabRoute.Album(albumKey))

    override fun openFolder(folderId: Long, relativePath: String, title: String) =
        pushToCurrentTab(TabRoute.Folder(folderId, relativePath, title))

    override fun open(route: TopRoute) {
        if (topStack.last() != route) topStack.add(route)
    }

    private var startupDone = false

    /**
     * 首帧显示后才做的工作（docs/design.md §9.3）：连接播放服务、注册媒体库监听、增量扫描。
     */
    fun onFirstFrame() {
        if (startupDone) return
        startupDone = true
        viewModelScope.launch {
            delay(500)
            player.connect()
            delay(500)
            observer.register()
            delay(1_000)
            scanner.requestScan()
            backups.startAutoBackup()
            analysis.schedule()
        }
    }
}
