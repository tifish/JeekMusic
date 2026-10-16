package com.tinyfish.jeekmusic.dev

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.ReportDrawnWhen
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material3.BottomAppBar
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.testTagsAsResourceId
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.playback.PlayerUiState

private val audioPermission =
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) Manifest.permission.READ_MEDIA_AUDIO
    else Manifest.permission.READ_EXTERNAL_STORAGE

/**
 * M0 开发验证页面：列出 MediaStore 音频、点击播放、运行 TagLib 测试。M1 起删除。
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalComposeUiApi::class)
@Composable
fun DevScreen(viewModel: DevViewModel = hiltViewModel()) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val playerState by viewModel.playerState.collectAsStateWithLifecycle()
    val context = LocalContext.current

    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) { result -> if (result[audioPermission] == true) viewModel.load() }

    LaunchedEffect(Unit) {
        if (state.loaded) return@LaunchedEffect
        val granted = ContextCompat.checkSelfPermission(context, audioPermission) == PackageManager.PERMISSION_GRANTED
        if (granted) viewModel.load() else viewModel.markLoadedWithoutPermission()
    }
    ReportDrawnWhen { state.loaded }

    Scaffold(
        modifier = Modifier.semantics { testTagsAsResourceId = true },
        topBar = { TopAppBar(title = { Text("JeekMusic · M0 验证") }) },
        bottomBar = { MiniPlayer(playerState, viewModel::togglePlayPause) },
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                OutlinedButton(onClick = {
                    val permissions = buildList {
                        add(audioPermission)
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                            add(Manifest.permission.POST_NOTIFICATIONS)
                        }
                    }
                    permissionLauncher.launch(permissions.toTypedArray())
                }) { Text("授权并刷新") }
                Button(
                    onClick = viewModel::runTagLibProbe,
                    enabled = state.tracks.isNotEmpty() && !state.probing,
                ) {
                    Text(if (state.probing) "测试中 ${state.probeProgress}/${state.tracks.size}" else "TagLib 测试")
                }
            }
            state.report?.let { ReportCard(it) }
            Text(
                "共 ${state.tracks.size} 首",
                Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                style = MaterialTheme.typography.labelMedium,
            )
            LazyColumn(
                Modifier.fillMaxSize().testTag("track_list"),
                contentPadding = PaddingValues(bottom = 8.dp),
            ) {
                itemsIndexed(state.tracks, key = { _, t -> t.id }) { index, track ->
                    Column(
                        Modifier
                            .fillMaxWidth()
                            .clickable { viewModel.play(index) }
                            .padding(horizontal = 16.dp, vertical = 8.dp),
                    ) {
                        Text(track.title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            "${track.artist ?: "未知艺术家"} · ${track.relativePath}",
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun ReportCard(report: TagLibReport) {
    Card(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp)) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text("TagLib：${report.succeeded}/${report.total} 成功，失败 ${report.failed}")
            Text("耗时 ${report.elapsedMs} ms，平均 ${"%.2f".format(report.avgMs)} ms/首（4 路并发）")
            Text("多值艺术家 ${report.multiArtist} · 乱码修复 ${report.mojibakeFixed} · 内嵌歌词 ${report.withLyrics} · 内嵌 CUE ${report.withCueSheet}")
            report.samples.forEach {
                Text(it, style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

@Composable
private fun MiniPlayer(state: PlayerUiState, onTogglePlayPause: () -> Unit) {
    if (!state.hasMedia) return
    BottomAppBar {
        Column(Modifier.weight(1f).padding(start = 16.dp)) {
            Text(state.title.orEmpty(), maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(
                state.artist.orEmpty(),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.bodySmall,
            )
        }
        IconButton(onClick = onTogglePlayPause) {
            Icon(
                painterResource(if (state.isPlaying) R.drawable.ic_pause else R.drawable.ic_play),
                contentDescription = if (state.isPlaying) "暂停" else "播放",
            )
        }
    }
}
