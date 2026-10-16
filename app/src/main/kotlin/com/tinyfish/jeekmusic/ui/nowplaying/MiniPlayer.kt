package com.tinyfish.jeekmusic.ui.nowplaying

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.ui.components.Artwork

/**
 * 底部常驻迷你播放条（需求 T-7）：封面、标题、艺术家、上一首 / 播放暂停 / 下一首，点击展开播放页。
 */
@Composable
fun MiniPlayer(viewModel: PlayerViewModel, onExpand: () -> Unit) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val nowPlaying = state.nowPlaying ?: return
    // 细进度条每秒刷新一次；App 在后台时停止收集（docs/design.md §7.7）
    val position by remember { viewModel.position(1_000) }.collectAsStateWithLifecycle(0L)

    Surface(tonalElevation = 3.dp, modifier = Modifier.testTag("mini_player")) {
        Column(Modifier.navigationBarsPadding()) {
            LinearProgressIndicator(
                progress = { if (state.durationMs > 0) (position.toFloat() / state.durationMs).coerceIn(0f, 1f) else 0f },
                modifier = Modifier.fillMaxWidth().height(2.dp),
                drawStopIndicator = {},
                gapSize = 0.dp,
            )
            Row(
                Modifier.fillMaxWidth().clickable(onClick = onExpand).padding(start = 12.dp, end = 4.dp, top = 6.dp, bottom = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Artwork(nowPlaying.artworkUri, 44.dp)
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(nowPlaying.title, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
                    Text(
                        nowPlaying.artist ?: stringResource(R.string.unknown_artist),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                IconButton(onClick = viewModel::previous) {
                    Icon(painterResource(R.drawable.ic_skip_previous), contentDescription = stringResource(R.string.action_previous))
                }
                IconButton(onClick = viewModel::togglePlayPause, modifier = Modifier.testTag("mini_play_pause")) {
                    Icon(
                        painterResource(if (state.isPlaying) R.drawable.ic_pause else R.drawable.ic_play),
                        contentDescription = stringResource(if (state.isPlaying) R.string.action_pause else R.string.action_play),
                    )
                }
                IconButton(onClick = viewModel::next) {
                    Icon(painterResource(R.drawable.ic_skip_next), contentDescription = stringResource(R.string.action_next))
                }
            }
        }
    }
}
