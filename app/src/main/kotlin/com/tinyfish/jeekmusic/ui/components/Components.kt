package com.tinyfish.jeekmusic.ui.components

import com.tinyfish.jeekmusic.data.settings.MoodFilter
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.lazy.LazyListState
import kotlin.math.abs
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.gestures.animateScrollBy
import androidx.compose.material3.SmallFloatingActionButton
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import kotlinx.coroutines.launch
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.playback.MediaItemMapper

/**
 * 封面；没有封面时显示音符占位图。
 * 占位图始终画在下面、图片叠在上面，避免 SubcomposeAsyncImage 在长列表中的子组合开销（docs/design.md §5.7）。
 */
@Composable
fun Artwork(uri: Uri?, size: Dp, modifier: Modifier = Modifier, cornerRadius: Dp = 6.dp) {
    Box(
        modifier.size(size).clip(RoundedCornerShape(cornerRadius)).background(MaterialTheme.colorScheme.surfaceContainerHighest),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            painterResource(R.drawable.ic_music_note),
            contentDescription = null,
            modifier = Modifier.size(size / 2),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        if (uri != null) {
            AsyncImage(
                model = uri,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        }
    }
}

/**
 * 歌曲列表项（docs/design.md §5.7）。长按打开"更多"菜单。只接收不可变数据，"正在播放"通过 [isCurrent] 单独传入。
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun TrackRow(
    track: TrackEntity,
    isCurrent: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    subtitle: String = trackSubtitle(track),
    showArtwork: Boolean = true,
    leading: (@Composable () -> Unit)? = null,
) {
    val highlight = if (isCurrent) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface
    val openMenu = LocalTrackMenu.current
    Row(
        modifier
            .fillMaxWidth()
            .combinedClickable(onClick = onClick, onLongClick = { openMenu(track) })
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        when {
            leading != null -> leading()
            showArtwork -> Artwork(MediaItemMapper.artworkUri(track.id), 48.dp)
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(
                track.title,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                color = highlight,
                fontWeight = if (isCurrent) FontWeight.SemiBold else FontWeight.Normal,
                style = MaterialTheme.typography.bodyLarge,
            )
            Text(
                subtitle,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Spacer(Modifier.width(8.dp))
        Text(
            formatDuration(track.durationMs),
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
fun trackSubtitle(track: TrackEntity): String {
    val artist = track.artistDisplay.ifEmpty { stringResource(R.string.unknown_artist) }
    return if (track.album.isNullOrEmpty()) artist else "$artist · ${track.album}"
}

/** 标签页内二级页面的标题栏（全局顶栏之下）。 */
@Composable
fun SubScreenHeader(title: String, onBack: () -> Unit, modifier: Modifier = Modifier, subtitle: String? = null, actions: @Composable () -> Unit = {}) {
    Row(
        modifier.fillMaxWidth().padding(start = 4.dp, end = 8.dp, top = 4.dp, bottom = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        IconButton(onClick = onBack) {
            Icon(painterResource(R.drawable.ic_arrow_back), contentDescription = stringResource(R.string.action_back))
        }
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (subtitle != null) {
                Text(subtitle, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        actions()
    }
}

@Composable
fun SectionHeader(text: String, modifier: Modifier = Modifier) {
    Text(
        text,
        modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surface).padding(horizontal = 16.dp, vertical = 8.dp),
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.primary,
    )
}

@Composable
fun songCount(count: Int): String = pluralStringResource(R.plurals.song_count, count, count)

@Composable
fun CenteredMessage(text: String, modifier: Modifier = Modifier) {
    Box(modifier.fillMaxSize().padding(32.dp), contentAlignment = Alignment.Center) {
        Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium)
    }
}

@Composable
fun VerticalSpace(height: Dp) = Spacer(Modifier.height(height))

/**
 * 把第 [index] 项滚动到列表可视区域的正中间（列表开头和结尾处做不到时尽量靠近）。
 * 先按可见条目的平均高度估算偏移量滚过去，到位后再按实际高度微调。
 */
suspend fun LazyListState.scrollToCenter(index: Int, animate: Boolean = true) {
    val info = layoutInfo
    val viewport = info.viewportEndOffset - info.viewportStartOffset
    val itemSize = info.visibleItemsInfo.map { it.size }.average().takeIf { !it.isNaN() }?.toInt() ?: 0
    val estimate = ((viewport - itemSize) / 2).coerceAtLeast(0)
    if (animate) animateScrollToItem(index, -estimate) else scrollToItem(index, -estimate)

    val after = layoutInfo
    val item = after.visibleItemsInfo.firstOrNull { it.index == index } ?: return
    val delta = (item.offset + item.size / 2f) - (after.viewportStartOffset + after.viewportEndOffset) / 2f
    if (abs(delta) < 1f) return
    if (animate) animateScrollBy(delta) else scrollBy(delta)
}

/** 筛选的显示名称：全部 / 运动 / 安静（需求 M-6）。 */
@Composable
fun moodFilterLabel(filter: MoodFilter): String = stringResource(
    when (filter) {
        MoodFilter.ALL -> R.string.filter_all
        MoodFilter.SPORT -> R.string.tab_sport
        MoodFilter.QUIET -> R.string.tab_quiet
    },
)

/** 筛选后列表为空时的提示。 */
@Composable
fun FilterEmptyMessage(filter: MoodFilter) = CenteredMessage(stringResource(R.string.filter_empty, moodFilterLabel(filter)))

fun formatDuration(ms: Long): String {
    val totalSeconds = (ms / 1000).coerceAtLeast(0)
    val hours = totalSeconds / 3600
    val minutes = (totalSeconds % 3600) / 60
    val seconds = totalSeconds % 60
    return if (hours > 0) "%d:%02d:%02d".format(hours, minutes, seconds) else "%d:%02d".format(minutes, seconds)
}

/**
 * "定位到正在播放"按钮（需求 P-10）：当前列表里有正在播放的歌曲、且它不在屏幕上时显示。
 * [index] 是正在播放的歌曲在 LazyColumn 中的位置（包括标题等其他条目），-1 表示不在列表中。
 * 点击后把这首歌滚动到列表正中间。
 */
@Composable
fun LocateCurrentButton(listState: LazyListState, index: Int, modifier: Modifier = Modifier) {
    // 必须在下面的提前 return 之前创建：滚动途中歌曲一进入屏幕按钮就会隐藏，
    // 如果作用域跟着按钮离开组合，滚动动画会被取消，歌曲停在屏幕最下方
    val scope = rememberCoroutineScope()
    if (index < 0) return
    val visible by remember(index) {
        derivedStateOf { listState.layoutInfo.visibleItemsInfo.any { it.index == index } }
    }
    if (visible) return
    SmallFloatingActionButton(
        onClick = { scope.launch { listState.scrollToCenter(index) } },
        modifier = modifier.padding(16.dp),
    ) {
        Icon(painterResource(R.drawable.ic_my_location), contentDescription = stringResource(R.string.action_locate_current))
    }
}
