package com.tinyfish.jeekmusic.ui.nowplaying

import androidx.compose.foundation.background
import androidx.compose.ui.graphics.Color
import com.tinyfish.jeekmusic.ui.components.currentRowColor
import com.tinyfish.jeekmusic.ui.components.FollowCurrentTrack
import com.tinyfish.jeekmusic.ui.components.verticalScrollbar
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import com.tinyfish.jeekmusic.ui.components.scrollToCenter
import kotlinx.coroutines.flow.first
import androidx.compose.runtime.snapshotFlow
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.toMutableStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.playback.PlayerUiState
import sh.calvin.reorderable.ReorderableItem
import sh.calvin.reorderable.rememberReorderableLazyListState

/**
 * 播放队列（需求 P-6）：查看、点击跳转、移除、拖动排序。
 * 拖动时只改本地列表，松手后再把一次移动提交给播放器。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun QueueSheet(viewModel: PlayerViewModel, state: PlayerUiState, onDismiss: () -> Unit) {
    val items = remember(state.queue) { state.queue.toMutableStateList() }
    var draggingUid by remember { mutableStateOf<String?>(null) }
    val listState = rememberLazyListState()
    val reorderState = rememberReorderableLazyListState(listState) { from, to ->
        items.add(to.index, items.removeAt(from.index))
    }
    LaunchedEffect(Unit) {
        // 等列表完成第一次布局（拿到可视区域高度）后，把当前歌曲放到正中间
        snapshotFlow { listState.layoutInfo.viewportEndOffset }.first { it > 0 }
        if (state.currentIndex > 0) listState.scrollToCenter(state.currentIndex, animate = false)
    }
    // 打开队列时播到下一首，也让当前歌曲留在屏幕上（拖动排序时不跟随）
    FollowCurrentTrack(listState, if (draggingUid == null) state.currentIndex else -1)

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(Modifier.fillMaxHeight(0.85f)) {
            Text(
                stringResource(R.string.queue_title) + "  " + stringResource(R.string.queue_count, state.currentIndex + 1, state.queue.size),
                Modifier.padding(horizontal = 24.dp, vertical = 8.dp),
                style = MaterialTheme.typography.titleMedium,
            )
            LazyColumn(state = listState, modifier = Modifier.verticalScrollbar(listState).testTag("queue_list")) {
                itemsIndexed(items, key = { _, item -> item.uid }) { index, item ->
                    ReorderableItem(reorderState, key = item.uid) { isDragging ->
                        val current = item.uid == state.queue.getOrNull(state.currentIndex)?.uid
                        Surface(tonalElevation = if (isDragging) 6.dp else 0.dp) {
                            Row(
                                Modifier.fillMaxWidth()
                                    .background(if (current) currentRowColor() else Color.Transparent)
                                    .clickable { viewModel.skipTo(state.queue.indexOfFirst { it.uid == item.uid }) }
                                    .padding(start = 8.dp, end = 8.dp, top = 4.dp, bottom = 4.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Icon(
                                    painterResource(R.drawable.ic_drag_handle),
                                    contentDescription = stringResource(R.string.action_reorder),
                                    modifier = Modifier.padding(12.dp).draggableHandle(
                                        onDragStarted = { draggingUid = item.uid },
                                        onDragStopped = {
                                            val uid = draggingUid
                                            draggingUid = null
                                            val from = state.queue.indexOfFirst { it.uid == uid }
                                            val to = items.indexOfFirst { it.uid == uid }
                                            if (from >= 0 && to >= 0 && from != to) viewModel.move(from, to)
                                        },
                                    ),
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                                Column(Modifier.weight(1f)) {
                                    Text(
                                        item.title,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                        color = if (current) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
                                        fontWeight = if (current) FontWeight.Bold else FontWeight.Normal,
                                    )
                                    Text(
                                        item.artist ?: stringResource(R.string.unknown_artist),
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                }
                                IconButton(onClick = { viewModel.remove(state.queue.indexOfFirst { it.uid == item.uid }) }) {
                                    Icon(painterResource(R.drawable.ic_close), contentDescription = stringResource(R.string.action_remove))
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
