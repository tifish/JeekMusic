package com.tinyfish.jeekmusic.ui.main

import androidx.activity.compose.ReportDrawnWhen
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.PrimaryScrollableTabRow
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.testTagsAsResourceId
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.navigation3.rememberViewModelStoreNavEntryDecorator
import androidx.navigation3.runtime.entryProvider
import androidx.navigation3.runtime.rememberSaveableStateHolderNavEntryDecorator
import androidx.navigation3.ui.NavDisplay
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.scanner.ScanState
import com.tinyfish.jeekmusic.data.settings.ThemeMode
import com.tinyfish.jeekmusic.ui.artists.ArtistDetailScreen
import com.tinyfish.jeekmusic.ui.artists.ArtistListScreen
import com.tinyfish.jeekmusic.ui.components.CenteredMessage
import com.tinyfish.jeekmusic.ui.folders.FolderRootScreen
import com.tinyfish.jeekmusic.ui.folders.FolderScreen
import com.tinyfish.jeekmusic.ui.navigation.Tab
import com.tinyfish.jeekmusic.ui.navigation.TabRoute
import com.tinyfish.jeekmusic.ui.navigation.TopRoute
import com.tinyfish.jeekmusic.ui.nowplaying.MiniPlayer
import com.tinyfish.jeekmusic.ui.nowplaying.NowPlayingScreen
import com.tinyfish.jeekmusic.ui.nowplaying.PlayerViewModel
import com.tinyfish.jeekmusic.ui.settings.SettingsScreen
import com.tinyfish.jeekmusic.ui.theme.JeekTheme
import kotlinx.coroutines.launch

@Composable
fun JeekMusicApp(viewModel: MainViewModel = hiltViewModel()) {
    val settings by viewModel.settings.collectAsStateWithLifecycle()
    JeekTheme(themeMode = settings?.themeMode ?: ThemeMode.SYSTEM) {
        Surface(Modifier.fillMaxSize().semantics { testTagsAsResourceId = true }) {
            NavDisplay(
                backStack = viewModel.topStack,
                entryDecorators = listOf(
                    rememberSaveableStateHolderNavEntryDecorator(),
                    rememberViewModelStoreNavEntryDecorator(),
                ),
                entryProvider = entryProvider {
                    entry<TopRoute.Main> { MainScreen(viewModel) }
                    entry<TopRoute.Settings> { SettingsScreen(onBack = { viewModel.topStack.removeLastOrNull() }) }
                },
            )
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun MainScreen(viewModel: MainViewModel) {
    val playerViewModel: PlayerViewModel = hiltViewModel()
    val scanState by viewModel.scanState.collectAsStateWithLifecycle()
    val pagerState = rememberPagerState { viewModel.tabs.size }
    val scope = rememberCoroutineScope()
    var nowPlayingOpen by remember { mutableStateOf(false) }
    var firstContentShown by remember { mutableStateOf(false) }

    // TTFD：首屏列表数据到达后上报（docs/design.md §9.2）
    ReportDrawnWhen { firstContentShown }
    LaunchedEffect(firstContentShown) { if (firstContentShown) viewModel.onFirstFrame() }

    Box(Modifier.fillMaxSize()) {
        Scaffold(
            topBar = {
                TopAppBar(
                    title = { Text(stringResource(R.string.app_name)) },
                    actions = {
                        IconButton(onClick = viewModel::openSettings, modifier = Modifier.testTag("open_settings")) {
                            Icon(painterResource(R.drawable.ic_settings), contentDescription = stringResource(R.string.action_settings))
                        }
                    },
                )
            },
            bottomBar = { MiniPlayer(playerViewModel, onExpand = { nowPlayingOpen = true }) },
        ) { padding ->
            Column(Modifier.padding(padding).fillMaxSize()) {
                PrimaryScrollableTabRow(selectedTabIndex = pagerState.currentPage, edgePadding = 8.dp) {
                    viewModel.tabs.forEachIndexed { index, tab ->
                        Tab(
                            selected = pagerState.currentPage == index,
                            onClick = { scope.launch { pagerState.animateScrollToPage(index) } },
                            text = { Text(tabTitle(tab)) },
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
                HorizontalPager(state = pagerState, beyondViewportPageCount = 0, key = { viewModel.tabs[it].toString() }) { page ->
                    val tab = viewModel.tabs[page]
                    TabContent(
                        tab = tab,
                        viewModel = viewModel,
                        onFirstContent = { firstContentShown = true },
                    )
                }
            }
        }

        AnimatedVisibility(
            visible = nowPlayingOpen,
            enter = slideInVertically { it },
            exit = slideOutVertically { it },
        ) {
            NowPlayingScreen(playerViewModel, onCollapse = { nowPlayingOpen = false })
        }
    }
}

@Composable
private fun TabContent(tab: Tab, viewModel: MainViewModel, onFirstContent: () -> Unit) {
    val stack = viewModel.stackOf(tab)
    val push: (TabRoute) -> Unit = { viewModel.push(tab, it) }
    val pop: () -> Unit = { if (stack.size > 1) stack.removeAt(stack.lastIndex) }
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
            entry<TabRoute.ArtistList> {
                LaunchedEffect(Unit) { onFirstContent() }
                ArtistListScreen(onOpen = { push(TabRoute.ArtistDetail(it.nameKey)) })
            }
            entry<TabRoute.ArtistDetail> { route ->
                ArtistDetailScreen(route, onBack = pop, onOpenAlbum = { push(TabRoute.Album(it)) })
            }
            entry<TabRoute.Album> {
                // 专辑页在 M2 实现
                CenteredMessage(stringResource(R.string.unknown_album))
            }
        },
    )
}

@Composable
private fun tabTitle(tab: Tab): String = when (tab) {
    Tab.Folders -> stringResource(R.string.tab_folders)
    Tab.Artists -> stringResource(R.string.tab_artists)
}

