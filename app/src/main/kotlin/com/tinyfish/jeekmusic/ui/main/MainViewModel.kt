package com.tinyfish.jeekmusic.ui.main

import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.data.scanner.LibraryObserver
import com.tinyfish.jeekmusic.data.scanner.LibraryScanner
import com.tinyfish.jeekmusic.data.scanner.ScanState
import com.tinyfish.jeekmusic.data.settings.Settings
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.ui.navigation.Tab
import com.tinyfish.jeekmusic.ui.navigation.TabRoute
import com.tinyfish.jeekmusic.ui.navigation.TopRoute
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

@HiltViewModel
class MainViewModel @Inject constructor(
    settingsRepository: SettingsRepository,
    private val scanner: LibraryScanner,
    private val observer: LibraryObserver,
    private val player: PlayerConnection,
) : ViewModel() {

    val settings: StateFlow<Settings?> = settingsRepository.settings
        .stateIn(viewModelScope, SharingStarted.Eagerly, null)

    val scanState: StateFlow<ScanState> = scanner.state

    val tabs: List<Tab> = listOf(Tab.Folders, Tab.Artists)

    /** 顶层返回栈：主界面、设置等全屏页面。 */
    val topStack: SnapshotStateList<TopRoute> = mutableStateListOf(TopRoute.Main)

    /** 每个标签各自的返回栈，切换标签时保留浏览位置。 */
    private val tabStacks = mutableMapOf<Tab, SnapshotStateList<TabRoute>>()

    fun stackOf(tab: Tab): SnapshotStateList<TabRoute> = tabStacks.getOrPut(tab) { mutableStateListOf(tab.root) }

    fun push(tab: Tab, route: TabRoute) {
        val stack = stackOf(tab)
        if (stack.lastOrNull() != route) stack.add(route)
    }

    fun openSettings() {
        if (topStack.last() != TopRoute.Settings) topStack.add(TopRoute.Settings)
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
        }
    }
}
