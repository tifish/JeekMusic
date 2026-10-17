package com.tinyfish.jeekmusic.ui.settings

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.toMutableStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.ui.components.SubScreenHeader
import com.tinyfish.jeekmusic.ui.main.MainViewModel
import com.tinyfish.jeekmusic.ui.main.tabTitle
import com.tinyfish.jeekmusic.ui.navigation.Tab
import sh.calvin.reorderable.ReorderableItem
import sh.calvin.reorderable.rememberReorderableLazyListState

/** 标签栏管理（需求 T-5）：拖动排序；固定的艺术家和智能标签可以取消固定，内置标签不能。 */
@Composable
fun TabManagerScreen(viewModel: MainViewModel, onBack: () -> Unit) {
    val tabs by viewModel.tabs.collectAsStateWithLifecycle()
    val items = remember(tabs) { tabs.toMutableStateList() }
    var dragFrom by remember { mutableIntStateOf(-1) }
    val listState = rememberLazyListState()
    val reorderState = rememberReorderableLazyListState(listState) { from, to ->
        items.add(to.index, items.removeAt(from.index))
    }

    Column(Modifier.fillMaxSize().systemBarsPadding().testTag("tab_manager")) {
        SubScreenHeader(title = stringResource(R.string.settings_tabs), onBack = onBack)
        LazyColumn(state = listState) {
            items(items, key = { it.key }) { tab ->
                ReorderableItem(reorderState, key = tab.key) { dragging ->
                    Surface(tonalElevation = if (dragging) 6.dp else 0.dp) {
                        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                            Icon(
                                painterResource(R.drawable.ic_drag_handle),
                                contentDescription = stringResource(R.string.action_reorder),
                                modifier = Modifier.size(48.dp).padding(12.dp).draggableHandle(
                                    onDragStarted = { dragFrom = tabs.indexOfFirst { it.key == tab.key } },
                                    onDragStopped = {
                                        val to = items.indexOfFirst { it.key == tab.key }
                                        if (dragFrom >= 0 && to >= 0 && dragFrom != to) viewModel.moveTab(dragFrom, to)
                                        dragFrom = -1
                                    },
                                ),
                                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            Text(tabTitle(tab), Modifier.weight(1f), style = MaterialTheme.typography.bodyLarge)
                            if (tab is Tab.PinnedArtist) {
                                IconButton(onClick = { viewModel.unpin(tab) }) {
                                    Icon(painterResource(R.drawable.ic_close), contentDescription = stringResource(R.string.menu_unpin_tab))
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
