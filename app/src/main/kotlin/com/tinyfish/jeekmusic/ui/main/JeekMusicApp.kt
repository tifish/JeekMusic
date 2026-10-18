package com.tinyfish.jeekmusic.ui.main

import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.foundation.background
import androidx.compose.ui.graphics.Color
import com.tinyfish.jeekmusic.ui.components.selectedChipColors
import com.tinyfish.jeekmusic.ui.components.ToggleIconButton
import com.tinyfish.jeekmusic.data.settings.SongSort
import com.tinyfish.jeekmusic.ui.components.sortLabel
import com.tinyfish.jeekmusic.ui.components.sortName
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.material3.FilterChip
import com.tinyfish.jeekmusic.analysis.Tag
import com.tinyfish.jeekmusic.analysis.TagDimension
import com.tinyfish.jeekmusic.ui.components.dimensionLabel
import com.tinyfish.jeekmusic.ui.components.filterLabel
import com.tinyfish.jeekmusic.ui.components.tagLabel
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.text.TextAutoSize
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.awaitLongPressOrCancellation
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.zIndex
import kotlinx.coroutines.delay
import kotlin.math.abs
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.HorizontalDivider
import androidx.compose.ui.Alignment
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.pluralStringResource
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
    // 提示放在最外层收集：设置、搜索等页面发出的提示（比如"已是最新版本"）也要显示，
    // 以前只在主界面收集，其他页面打开时的提示都丢了
    val snackbar = remember { SnackbarHostState() }
    val resources = LocalResources.current
    LaunchedEffect(Unit) {
        launch { viewModel.messages.collect { snackbar.showSnackbar(resources.getString(it.res, it.arg)) } }
        launch { viewModel.playbackErrors.collect { snackbar.showSnackbar(resources.getString(R.string.playback_error, it)) } }
    }
    JeekTheme(themeMode = themeMode) {
        CompositionLocalProvider(LocalNavigator provides viewModel, LocalSnackbar provides snackbar) {
            Surface(Modifier.fillMaxSize().semantics { testTagsAsResourceId = true }) {
                UpdatePrompt()
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
                // 主界面的 Scaffold 自己显示提示（在迷你播放条上方），其他全屏页面在这里显示
                if (viewModel.topStack.lastOrNull() != TopRoute.Main) {
                    Box(Modifier.fillMaxSize().navigationBarsPadding(), contentAlignment = Alignment.BottomCenter) {
                        SnackbarHost(snackbar)
                    }
                }
            }
        }
    }
}

/** 全局提示（[JeekMusicApp] 里收集 [MainViewModel.messages]）。 */
private val LocalSnackbar = staticCompositionLocalOf { SnackbarHostState() }

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun MainScreen(viewModel: MainViewModel) {
    val playerViewModel: PlayerViewModel = hiltViewModel()
    val playerState by playerViewModel.state.collectAsStateWithLifecycle()
    var showSleepTimer by remember { mutableStateOf(false) }
    val tabs by viewModel.tabs.collectAsStateWithLifecycle()
    val scanState by viewModel.scanState.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    val snackbar = LocalSnackbar.current
    var firstContentShown by remember { mutableStateOf(false) }

    // TTFD：首屏列表数据到达后上报（docs/design.md §9.2）
    ReportDrawnWhen { firstContentShown }
    LaunchedEffect(firstContentShown) { if (firstContentShown) viewModel.onFirstFrame() }

    RestorePrompt()
    if (tabs.isEmpty()) return
    var dragOrder by remember { mutableStateOf<List<Tab>?>(null) }
    var draggingKey by remember { mutableStateOf<String?>(null) }
    var dragOffset by remember { mutableFloatStateOf(0f) }
    val tabWidths = remember { mutableStateMapOf<String, Int>() }
    // 新顺序写入数据库并回到界面后，不再需要拖动时的临时顺序；万一写入失败，1 秒后也恢复
    LaunchedEffect(tabs, dragOrder, draggingKey) {
        val pending = dragOrder ?: return@LaunchedEffect
        if (draggingKey != null) return@LaunchedEffect
        if (pending.map { it.key } == tabs.map { it.key }) dragOrder = null else { delay(1_000); dragOrder = null }
    }
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
                    // 右侧按钮多（筛选中、睡眠定时中、系统字体大）时，标题保持一行，放不下就缩小字号；
                    // 缩到最小还放不下就不显示，不显示半截的"JeekM…"
                    title = {
                        var titleFits by remember { mutableStateOf(true) }
                        Text(
                            stringResource(R.string.app_name),
                            Modifier.graphicsLayer { alpha = if (titleFits) 1f else 0f },
                            maxLines = 1,
                            softWrap = false,
                            autoSize = TextAutoSize.StepBased(minFontSize = 14.sp, maxFontSize = MaterialTheme.typography.titleLarge.fontSize),
                            onTextLayout = { titleFits = !it.hasVisualOverflow },
                        )
                    },
                    actions = {
                        TagFilterButton(viewModel)
                        // 随机 / 顺序切换和睡眠定时，在列表界面直接可用（需求 P-5、P-8）
                        ToggleIconButton(
                            checked = playerState.shuffle,
                            onClick = playerViewModel::toggleShuffleAndAnnounce,
                            icon = R.drawable.ic_shuffle,
                            contentDescription = stringResource(if (playerState.shuffle) R.string.mode_shuffle else R.string.mode_sequential),
                            modifier = Modifier.testTag("top_shuffle"),
                        )
                        SleepTimerCountdown(playerState.sleepTimer)
                        ToggleIconButton(
                            checked = playerState.sleepTimer.active,
                            onClick = { showSleepTimer = true },
                            icon = R.drawable.ic_bedtime,
                            contentDescription = stringResource(R.string.menu_sleep_timer),
                            modifier = Modifier.testTag("top_sleep_timer"),
                        )
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
                // 拖动排序（需求 T-5）：拖动时先在界面上调整顺序，松手后一次写入数据库；
                // 数据库的新顺序到达之前继续显示拖动后的顺序，避免闪回
                val shown = dragOrder ?: tabs
                val currentKey = tabs.getOrNull(pagerState.currentPage)?.key
                PrimaryScrollableTabRow(
                    selectedTabIndex = shown.indexOfFirst { it.key == currentKey }.coerceAtLeast(0),
                    edgePadding = 8.dp,
                ) {
                    shown.forEachIndexed { index, tab ->
                        key(tab.key) {
                            TabWithMenu(
                                tab = tab,
                                selected = tab.key == currentKey,
                                canMoveLeft = index > 0,
                                canMoveRight = index < shown.lastIndex,
                                dragOffset = if (draggingKey == tab.key) dragOffset else 0f,
                                onSelect = { tabs.indexOfFirst { it.key == tab.key }.takeIf { it >= 0 }?.let { scope.launch { pagerState.animateScrollToPage(it) } } },
                                onUnpin = { viewModel.unpin(tab) },
                                onMove = { delta ->
                                    val from = tabs.indexOfFirst { it.key == tab.key }
                                    if (from >= 0) viewModel.moveTab(from, from + delta)
                                },
                                onDragStart = {
                                    dragOrder = shown
                                    draggingKey = tab.key
                                    dragOffset = 0f
                                },
                                onDrag = { dx ->
                                    val order = dragOrder ?: return@TabWithMenu
                                    dragOffset += dx
                                    val i = order.indexOfFirst { it.key == tab.key }
                                    // 越过相邻标签宽度的一半就交换，偏移量减去它的宽度，标签保持在手指下面
                                    val right = order.getOrNull(i + 1)?.let { tabWidths[it.key] }
                                    val left = order.getOrNull(i - 1)?.let { tabWidths[it.key] }
                                    if (right != null && dragOffset > right / 2f) {
                                        dragOrder = order.toMutableList().apply { add(i + 1, removeAt(i)) }
                                        dragOffset -= right
                                    } else if (left != null && dragOffset < -left / 2f) {
                                        dragOrder = order.toMutableList().apply { add(i - 1, removeAt(i)) }
                                        dragOffset += left
                                    }
                                },
                                onDragEnd = {
                                    val from = tabs.indexOfFirst { it.key == tab.key }
                                    val to = dragOrder?.indexOfFirst { it.key == tab.key } ?: from
                                    draggingKey = null
                                    dragOffset = 0f
                                    if (from >= 0 && to >= 0 && from != to) viewModel.moveTab(from, to) else dragOrder = null
                                },
                                modifier = Modifier
                                    .onSizeChanged { tabWidths[tab.key] = it.width }
                                    .testTag("tab_$index"),
                            )
                        }
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

/**
 * 标题栏上的智能标签筛选（需求 M-6）：筛选中图标高亮并显示选中的标签（比如"非运动·纯音乐"）。
 * 菜单分三组（氛围 / 人声 / 曲风），每组点一下选中、再点一下取消，可以组合；
 * 菜单里还显示未分析的歌曲数，可以立即分析。
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun TagFilterButton(viewModel: MainViewModel) {
    val filter by viewModel.tagFilter.collectAsStateWithLifecycle()
    val sort by viewModel.songSort.collectAsStateWithLifecycle()
    var open by remember { mutableStateOf(false) }
    val active = filter.isActive || sort != SongSort.FILE_NAME
    // 有筛选或排序时整个按钮用主题色填充（需求 U-11）
    val color = if (active) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant
    Box {
        Row(
            Modifier
                .clip(CircleShape)
                .background(if (active) MaterialTheme.colorScheme.primary else Color.Transparent)
                .clickable { open = true }
                .testTag("top_filter")
                .padding(horizontal = 8.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(painterResource(R.drawable.ic_filter_list), contentDescription = stringResource(R.string.action_filter), tint = color)
            if (active) {
                // 比如"非运动·纯音乐·按歌名"
                val label = listOfNotNull(filterLabel(filter).takeIf { filter.isActive }, sortLabel(sort).takeIf { sort != SongSort.FILE_NAME })
                Text(label.joinToString("·"), Modifier.padding(start = 2.dp), style = MaterialTheme.typography.labelMedium, color = color, maxLines = 1)
            }
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) {
                TagDimension.entries.forEach { dimension ->
                    Text(
                        dimensionLabel(dimension),
                        Modifier.padding(top = 8.dp),
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Tag.of(dimension).forEach { tag ->
                            FilterChip(
                                selected = filter[dimension] == tag,
                                onClick = { viewModel.toggleFilter(tag) },
                                label = { Text(tagLabel(tag)) },
                                colors = selectedChipColors(),
                                modifier = Modifier.testTag("filter_" + tag.name.lowercase()),
                            )
                        }
                    }
                }
                // 排序（需求 U-8）：单选
                Text(
                    stringResource(R.string.sort_group),
                    Modifier.padding(top = 8.dp),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    SongSort.entries.forEach { option ->
                        FilterChip(
                            selected = sort == option,
                            onClick = { viewModel.setSort(option) },
                            label = { Text(sortName(option)) },
                            colors = selectedChipColors(),
                            modifier = Modifier.testTag("sort_" + option.name.lowercase()),
                        )
                    }
                }
            }
            if (filter.isActive) {
                HorizontalDivider()
                DropdownMenuItem(text = { Text(stringResource(R.string.filter_clear)) }, onClick = { viewModel.clearFilter(); open = false })
            }
            val pending by viewModel.pendingAnalysis.collectAsStateWithLifecycle()
            if (pending > 0) {
                HorizontalDivider()
                DropdownMenuItem(text = { Text(pluralStringResource(R.plurals.mood_pending, pending, pending)) }, onClick = {}, enabled = false)
                DropdownMenuItem(text = { Text(stringResource(R.string.mood_analyze_now)) }, onClick = { viewModel.analyzeNow(); open = false })
            }
        }
    }
}

/**
 * 标签：点击切换；长按后左右拖动可以调整顺序；长按不拖动弹出"取消固定 / 左移 / 右移"（需求 T-5）。
 */
@Composable
private fun TabWithMenu(
    tab: Tab,
    selected: Boolean,
    canMoveLeft: Boolean,
    canMoveRight: Boolean,
    dragOffset: Float,
    onSelect: () -> Unit,
    onUnpin: () -> Unit,
    onMove: (Int) -> Unit,
    onDragStart: () -> Unit,
    onDrag: (Float) -> Unit,
    onDragEnd: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var menuOpen by remember { mutableStateOf(false) }
    val haptics = LocalHapticFeedback.current
    val dragging = dragOffset != 0f
    // 回调在拖动过程中会变（顺序变了），手势里总是调用最新的
    val currentOnDrag by rememberUpdatedState(onDrag)
    val currentOnDragEnd by rememberUpdatedState(onDragEnd)
    val currentOnDragStart by rememberUpdatedState(onDragStart)
    Tab(
        selected = selected,
        onClick = onSelect,
        // 选中的标签：主题色加粗；其他：灰色常规（需求 U-11）
        selectedContentColor = MaterialTheme.colorScheme.primary,
        unselectedContentColor = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = modifier
            // 手势放在 graphicsLayer 之前：坐标不受拖动时的平移影响
            .pointerInput(tab.key) {
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    // 长按之前移动（横向滚动标签栏）或抬起（普通点击）都不进入拖动
                    awaitLongPressOrCancellation(down.id) ?: return@awaitEachGesture
                    haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                    currentOnDragStart()
                    var moved = 0f
                    while (true) {
                        // Initial 阶段先处理，抬起事件也在这里消费掉，标签自己的点击就不会再触发
                        val change = awaitPointerEvent(PointerEventPass.Initial).changes.firstOrNull { it.id == down.id } ?: break
                        if (!change.pressed) {
                            change.consume()
                            break
                        }
                        val dx = change.positionChange().x
                        if (dx != 0f) {
                            change.consume()
                            moved += abs(dx)
                            currentOnDrag(dx)
                        }
                    }
                    currentOnDragEnd()
                    // 长按后没有拖动：弹出菜单
                    if (moved < viewConfiguration.touchSlop) menuOpen = true
                }
            }
            .zIndex(if (dragging) 1f else 0f)
            .graphicsLayer {
                translationX = dragOffset
                if (dragging) {
                    scaleX = 1.08f
                    scaleY = 1.08f
                }
            },
        text = {
            Box {
                Text(tabTitle(tab), fontWeight = if (selected) FontWeight.Bold else FontWeight.Normal)
                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    if (tab is Tab.PinnedArtist) {
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
            // 启动时文件夹标签可能恢复到某个子文件夹（需求 F-8），这时首屏是子文件夹：它也要报告首屏内容，否则推迟的启动任务不会执行
            entry<TabRoute.Folder> { route -> FolderScreen(route, onOpen = push, onBack = pop, onLoaded = onFirstContent) }
            entry<TabRoute.ArtistList> { ArtistListScreen(onOpen = { push(TabRoute.ArtistDetail(it.nameKey)) }) }
            entry<TabRoute.ArtistDetail> { route -> ArtistDetailScreen(route, onBack = pop) }
            entry<TabRoute.Album> { route -> AlbumScreen(route, onBack = pop) }
            entry<TabRoute.PlaylistList> { PlaylistListScreen(onOpen = { push(TabRoute.PlaylistDetail(it)) }) }
            entry<TabRoute.PlaylistDetail> { route -> PlaylistDetailScreen(route, onBack = pop) }
        },
    )
}

@Composable
fun tabTitle(tab: Tab): String = when (tab) {
    Tab.Folders -> stringResource(R.string.tab_folders)
    Tab.Artists -> stringResource(R.string.tab_artists)
    Tab.Playlists -> stringResource(R.string.tab_playlists)
    is Tab.PinnedArtist -> tab.name.ifEmpty { stringResource(R.string.unknown_artist) }
}
