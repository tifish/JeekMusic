package com.tinyfish.jeekmusic.ui.main

import androidx.activity.compose.ReportDrawnWhen
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.PrimaryScrollableTabRow
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalResources
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.testTagsAsResourceId
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.navigation3.rememberViewModelStoreNavEntryDecorator
import androidx.navigation3.runtime.entryProvider
import androidx.navigation3.runtime.rememberSaveableStateHolderNavEntryDecorator
import androidx.navigation3.ui.NavDisplay
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.scanner.ScanState
import com.tinyfish.jeekmusic.ui.albums.AlbumScreen
import com.tinyfish.jeekmusic.ui.artists.ArtistDetailScreen
import com.tinyfish.jeekmusic.ui.artists.ArtistListScreen
import com.tinyfish.jeekmusic.ui.components.TrackMenuHost
import com.tinyfish.jeekmusic.ui.folders.FolderRootScreen
import com.tinyfish.jeekmusic.ui.folders.FolderScreen
import com.tinyfish.jeekmusic.ui.moods.MoodScreen
import com.tinyfish.jeekmusic.ui.navigation.LocalNavigator
import com.tinyfish.jeekmusic.ui.navigation.Tab
import com.tinyfish.jeekmusic.ui.navigation.TabRoute
import com.tinyfish.jeekmusic.ui.navigation.TopRoute
import com.tinyfish.jeekmusic.ui.nowplaying.MiniPlayer
import com.tinyfish.jeekmusic.ui.nowplaying.NowPlayingScreen
import com.tinyfish.jeekmusic.ui.nowplaying.PlayerViewModel
import com.tinyfish.jeekmusic.ui.nowplaying.SleepTimerCountdown
import com.tinyfish.jeekmusic.ui.nowplaying.SleepTimerDialog
import com.tinyfish.jeekmusic.ui.playlists.PlaylistDetailScreen
import com.tinyfish.jeekmusic.ui.playlists.PlaylistListScreen
import com.tinyfish.jeekmusic.ui.search.SearchScreen
import com.tinyfish.jeekmusic.ui.settings.ArtistSettingsScreen
import com.tinyfish.jeekmusic.ui.settings.SettingsScreen
import com.tinyfish.jeekmusic.ui.settings.TabManagerScreen
import com.tinyfish.jeekmusic.ui.theme.JeekTheme
import kotlinx.coroutines.launch

@Composable
fun JeekMusicApp(viewModel: MainViewModel) {
    val themeMode by viewModel.themeMode.collectAsStateWithLifecycle()
    JeekTheme(themeMode = themeMode) {
        CompositionLocalProvider(LocalNavigator provides viewModel) {
            Surface(Modifier.fillMaxSize().semantics { testTagsAsResourceId = true }) {
                TrackMenuHost {
                    val back: () -> Unit = { viewModel.topStack.removeLastOrNull() }
                    NavDisplay(
                        backStack = viewModel.topStack,
                        entryDecorators = listOf(
                            rememberSaveableStateHolderNavEntryDecorator(),
                            rememberViewModelStoreNavEntryDecorator(),
                        ),
                        entryProvider = entryProvider {
                            entry<TopRoute.Main> { MainScreen(viewModel) }
                            entry<TopRoute.Settings> { SettingsScreen(onBack = back) }
                            entry<TopRoute.Search> { SearchScreen(onBack = back) }
                            entry<TopRoute.TabManager> { TabManagerScreen(viewModel, onBack = back) }
                            entry<TopRoute.ArtistSettings> { ArtistSettingsScreen(onBack = back) }
                        },
                    )
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun MainScreen(viewModel: MainViewModel) {
    val playerViewModel: PlayerViewModel = hiltViewModel()
    val playerState by playerViewModel.state.collectAsStateWithLifecycle()
    var showSleepTimer by remember { mutableStateOf(false) }
    val tabs by viewModel.tabs.collectAsStateWithLifecycle()
    val scanState by viewModel.scanState.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }
    val resources = LocalResources.current
    var firstContentShown by remember { mutableStateOf(false) }

    // TTFD：首屏列表数据到达后上报（docs/design.md §9.2）
    ReportDrawnWhen { firstContentShown }
    LaunchedEffect(firstContentShown) { if (firstContentShown) viewModel.onFirstFrame() }
    LaunchedEffect(Unit) {
        launch { viewModel.messages.collect { snackbar.showSnackbar(resources.getString(it.res, it.arg)) } }
        launch { viewModel.playbackErrors.collect { snackbar.showSnackbar(resources.getString(R.string.playback_error, it)) } }
    }

    RestorePrompt()
    if (tabs.isEmpty()) return
    val initialPage = tabs.indexOfFirst { it.key == viewModel.currentTabKey }.coerceAtLeast(0)
    val pagerState = rememberPagerState(initialPage = initialPage) { tabs.size }

    // Pager 页码 → 当前标签；标签列表变化（固定、取消固定、排序）后，保持选中同一个标签
    LaunchedEffect(pagerState) {
        snapshotFlow { pagerState.currentPage }.collect { page -> tabs.getOrNull(page)?.let(viewModel::selectTab) }
    }
    LaunchedEffect(tabs) {
        val index = tabs.indexOfFirst { it.key == viewModel.currentTabKey }
        if (index >= 0 && index != pagerState.currentPage) pagerState.scrollToPage(index)
    }

    Box(Modifier.fillMaxSize()) {
        Scaffold(
            topBar = {
                TopAppBar(
                    title = { Text(stringResource(R.string.app_name)) },
                    actions = {
                        // 随机 / 顺序切换和睡眠定时，在列表界面直接可用（需求 P-5、P-8）
                        IconButton(onClick = playerViewModel::toggleShuffleAndAnnounce, modifier = Modifier.testTag("top_shuffle")) {
                            Icon(
                                painterResource(R.drawable.ic_shuffle),
                                contentDescription = stringResource(if (playerState.shuffle) R.string.mode_shuffle else R.string.mode_sequential),
                                tint = if (playerState.shuffle) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        SleepTimerCountdown(playerState.sleepTimer)
                        IconButton(onClick = { showSleepTimer = true }, modifier = Modifier.testTag("top_sleep_timer")) {
                            Icon(
                                painterResource(R.drawable.ic_bedtime),
                                contentDescription = stringResource(R.string.menu_sleep_timer),
                                tint = if (playerState.sleepTimer.active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        IconButton(onClick = { viewModel.open(TopRoute.Search) }, modifier = Modifier.testTag("open_search")) {
                            Icon(painterResource(R.drawable.ic_search), contentDescription = stringResource(R.string.action_search))
                        }
                        IconButton(onClick = { viewModel.open(TopRoute.Settings) }, modifier = Modifier.testTag("open_settings")) {
                            Icon(painterResource(R.drawable.ic_settings), contentDescription = stringResource(R.string.action_settings))
                        }
                    },
                )
            },
            bottomBar = { MiniPlayer(playerViewModel, onExpand = { viewModel.nowPlayingOpen = true }) },
            snackbarHost = { SnackbarHost(snackbar) },
        ) { padding ->
            Column(Modifier.padding(padding).fillMaxSize()) {
                PrimaryScrollableTabRow(selectedTabIndex = pagerState.currentPage.coerceAtMost(tabs.lastIndex), edgePadding = 8.dp) {
                    tabs.forEachIndexed { index, tab ->
                        TabWithMenu(
                            tab = tab,
                            selected = pagerState.currentPage == index,
                            canMoveLeft = index > 0,
                            canMoveRight = index < tabs.lastIndex,
                            onSelect = { scope.launch { pagerState.animateScrollToPage(index) } },
                            onUnpin = { viewModel.unpin(tab) },
                            onMove = { delta -> viewModel.moveTab(index, index + delta) },
                            modifier = Modifier.testTag("tab_$index"),
                        )
                    }
                }
                (scanState as? ScanState.Running)?.let { running ->
                    LinearProgressIndicator(
                        progress = { if (running.total > 0) running.done.toFloat() / running.total else 0f },
                        modifier = Modifier.fillMaxWidth().testTag("scan_progress"),
                    )
                }
                // 只组合当前页，避免固定标签很多时拖慢启动和滑动（docs/design.md §5.1）
                HorizontalPager(state = pagerState, beyondViewportPageCount = 0, key = { tabs.getOrNull(it)?.key ?: it }) { page ->
                    val tab = tabs.getOrNull(page) ?: return@HorizontalPager
                    TabContent(tab = tab, viewModel = viewModel, onFirstContent = { firstContentShown = true })
                }
            }
        }

        AnimatedVisibility(
            visible = viewModel.nowPlayingOpen,
            enter = slideInVertically { it },
            exit = slideOutVertically { it },
        ) {
            NowPlayingScreen(playerViewModel, onCollapse = { viewModel.nowPlayingOpen = false })
        }
    }
    if (showSleepTimer) SleepTimerDialog(playerViewModel, playerState.sleepTimer, onDismiss = { showSleepTimer = false })
}

/** 标签：点击切换；长按弹出"取消固定 / 左移 / 右移"（需求 T-5）。 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun TabWithMenu(
    tab: Tab,
    selected: Boolean,
    canMoveLeft: Boolean,
    canMoveRight: Boolean,
    onSelect: () -> Unit,
    onUnpin: () -> Unit,
    onMove: (Int) -> Unit,
    modifier: Modifier = Modifier,
) {
    var menuOpen by remember { mutableStateOf(false) }
    Tab(
        selected = selected,
        onClick = onSelect,
        modifier = modifier,
        text = {
            Box {
                Text(tabTitle(tab), Modifier.combinedClickable(onClick = onSelect, onLongClick = { menuOpen = true }))
                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    if (tab is Tab.PinnedArtist || tab is Tab.Mood) {
                        DropdownMenuItem(text = { Text(stringResource(R.string.menu_unpin_tab)) }, onClick = { menuOpen = false; onUnpin() })
                    }
                    if (canMoveLeft) DropdownMenuItem(text = { Text(stringResource(R.string.menu_move_left)) }, onClick = { menuOpen = false; onMove(-1) })
                    if (canMoveRight) DropdownMenuItem(text = { Text(stringResource(R.string.menu_move_right)) }, onClick = { menuOpen = false; onMove(1) })
                }
            }
        },
    )
}

@Composable
private fun TabContent(tab: Tab, viewModel: MainViewModel, onFirstContent: () -> Unit) {
    val stack = viewModel.stackOf(tab)
    val push: (TabRoute) -> Unit = { if (stack.lastOrNull() != it) stack.add(it) }
    val pop: () -> Unit = { if (stack.size > 1) stack.removeAt(stack.lastIndex) }
    LaunchedEffect(Unit) { if (tab !is Tab.Folders) onFirstContent() }
    NavDisplay(
        backStack = stack,
        onBack = pop,
        entryDecorators = listOf(
            rememberSaveableStateHolderNavEntryDecorator(),
            rememberViewModelStoreNavEntryDecorator(),
        ),
        entryProvider = entryProvider {
            entry<TabRoute.FolderRoot> { FolderRootScreen(onOpen = push, onLoaded = onFirstContent) }
            entry<TabRoute.Folder> { route -> FolderScreen(route, onOpen = push, onBack = pop) }
            entry<TabRoute.ArtistList> { ArtistListScreen(onOpen = { push(TabRoute.ArtistDetail(it.nameKey)) }) }
            entry<TabRoute.ArtistDetail> { route -> ArtistDetailScreen(route, onBack = pop) }
            entry<TabRoute.Album> { route -> AlbumScreen(route, onBack = pop) }
            entry<TabRoute.PlaylistList> { PlaylistListScreen(onOpen = { push(TabRoute.PlaylistDetail(it)) }) }
            entry<TabRoute.PlaylistDetail> { route -> PlaylistDetailScreen(route, onBack = pop) }
            entry<TabRoute.MoodTracks> { route -> MoodScreen(route) }
        },
    )
}

@Composable
fun tabTitle(tab: Tab): String = when (tab) {
    Tab.Folders -> stringResource(R.string.tab_folders)
    Tab.Artists -> stringResource(R.string.tab_artists)
    Tab.Playlists -> stringResource(R.string.tab_playlists)
    is Tab.PinnedArtist -> tab.name.ifEmpty { stringResource(R.string.unknown_artist) }
    is Tab.Mood -> stringResource(if (tab.mood == "SPORT") R.string.tab_sport else R.string.tab_quiet)
}
