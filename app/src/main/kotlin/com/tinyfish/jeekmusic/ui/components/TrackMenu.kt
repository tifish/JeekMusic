package com.tinyfish.jeekmusic.ui.components

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.material3.FilterChip
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.analysis.Tag
import com.tinyfish.jeekmusic.analysis.TagDimension
import com.tinyfish.jeekmusic.analysis.TagClassifier
import com.tinyfish.jeekmusic.analysis.modelScores
import com.tinyfish.jeekmusic.analysis.TagInfo
import com.tinyfish.jeekmusic.analysis.TagRepository
import com.tinyfish.jeekmusic.data.db.ArtistEntity
import com.tinyfish.jeekmusic.data.db.FolderDao
import com.tinyfish.jeekmusic.data.db.PlaylistEntity
import com.tinyfish.jeekmusic.data.db.SearchDao
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.db.UNKNOWN_ARTIST_KEY
import com.tinyfish.jeekmusic.data.playlist.PlaylistRepository
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.ui.navigation.LocalNavigator
import com.tinyfish.jeekmusic.ui.navigation.MessageBus
import com.tinyfish.jeekmusic.ui.navigation.UiMessage
import dagger.hilt.android.lifecycle.HiltViewModel
import com.tinyfish.jeekmusic.data.cover.CoverCandidate
import com.tinyfish.jeekmusic.data.cover.CoverClient
import com.tinyfish.jeekmusic.data.cover.CoverSource
import com.tinyfish.jeekmusic.data.cover.CoverStore
import com.tinyfish.jeekmusic.data.settings.CoverSaveMode
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

/** 打开某首歌的"更多"菜单（需求 U-2）。 */
val LocalTrackMenu = staticCompositionLocalOf<(TrackEntity) -> Unit> { {} }

/** 按歌曲 id 打开"更多"菜单（播放页使用）。 */
val LocalTrackMenuById = staticCompositionLocalOf<(Long) -> Unit> { {} }

@HiltViewModel
class TrackMenuViewModel @Inject constructor(
    private val player: PlayerConnection,
    private val playlists: PlaylistRepository,
    private val searchDao: SearchDao,
    private val folderDao: FolderDao,
    private val trackDao: TrackDao,
    private val messages: MessageBus,
    private val tags: TagRepository,
    private val settings: SettingsRepository,
    private val coverClient: CoverClient,
    private val coverStore: CoverStore,
) : ViewModel() {

    val onlineCovers: StateFlow<Boolean> = settings.settings.map { it.onlineCovers }
        .stateIn(viewModelScope, SharingStarted.Eagerly, false)

    fun enableOnlineCovers() = viewModelScope.launch { settings.setOnlineCovers(true) }

    suspend fun searchCovers(source: CoverSource, title: String, artist: String?): List<CoverCandidate> =
        runCatching { coverClient.search(source, title, artist) }.getOrDefault(emptyList())

    suspend fun coverThumbnail(candidate: CoverCandidate): ByteArray? = coverClient.thumbnail(candidate)

    /** 下载大图并按设置保存（需求 C-3）；大图下载失败时退而用缩略图。 */
    suspend fun saveCover(track: TrackEntity, candidate: CoverCandidate) {
        val image = coverClient.downloadImage(candidate)
        val saved = image?.let { coverStore.save(track, it, settings.current().coverSaveMode) }
        messages.post(
            UiMessage(
                when (saved) {
                    CoverSaveMode.HIDDEN_FOLDER -> R.string.cover_saved_hidden
                    CoverSaveMode.EMBED -> R.string.cover_saved_embed
                    CoverSaveMode.SIDECAR -> R.string.cover_saved_sidecar
                    null -> R.string.cover_save_failed
                },
            ),
        )
    }

    suspend fun tagInfo(track: TrackEntity): TagInfo = tags.info(track.trackKey)

    /** 手动修正（需求 M-4）：[tag] 为 null 表示"都不是"。 */
    suspend fun setTag(track: TrackEntity, dimension: TagDimension, tag: Tag?) = tags.setManual(track.trackKey, dimension, tag)

    /** 恢复自动识别。 */
    suspend fun resetTag(track: TrackEntity, dimension: TagDimension) = tags.clearManual(track.trackKey, dimension)

    private val _track = MutableStateFlow<TrackEntity?>(null)
    val track = _track.asStateFlow()

    fun show(track: TrackEntity) {
        _track.value = track
    }

    fun showById(trackId: Long) = viewModelScope.launch { trackDao.byId(trackId)?.let(::show) }

    fun dismiss() {
        _track.value = null
    }

    fun playNext(track: TrackEntity) {
        player.playNext(listOf(track))
        messages.post(UiMessage(R.string.added_to_queue))
    }

    fun addToQueue(track: TrackEntity) {
        player.addToQueue(listOf(track))
        messages.post(UiMessage(R.string.added_to_queue))
    }

    suspend fun playlists(): List<PlaylistEntity> = playlists.all()

    fun addToPlaylist(playlist: PlaylistEntity, track: TrackEntity) = viewModelScope.launch {
        playlists.add(playlist.id, listOf(track))
        messages.post(UiMessage(R.string.added_to_playlist, playlist.name))
    }

    fun createPlaylistAndAdd(name: String, track: TrackEntity) = viewModelScope.launch {
        val id = playlists.create(name)
        playlists.add(id, listOf(track))
        messages.post(UiMessage(R.string.added_to_playlist, name.trim()))
    }

    suspend fun artistsOf(track: TrackEntity): List<ArtistEntity> = searchDao.artistsOfTrack(track.id)

    suspend fun folderOf(track: TrackEntity) = folderDao.byId(track.folderId)
}

private sealed interface MenuDialog {
    data object Playlist : MenuDialog
    data class Artists(val artists: List<ArtistEntity>) : MenuDialog
    data object Info : MenuDialog
    data object Tags : MenuDialog
    data object Cover : MenuDialog
}

/**
 * 在主界面根部放置一次；子页面通过 [LocalTrackMenu] 打开菜单。
 * [extraItems] 用于播放页追加"睡眠定时"等条目。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TrackMenuHost(
    viewModel: TrackMenuViewModel = hiltViewModel(),
    content: @Composable () -> Unit,
) {
    val track by viewModel.track.collectAsStateWithLifecycle()
    val onlineCovers by viewModel.onlineCovers.collectAsStateWithLifecycle()
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    var dialog by remember { mutableStateOf<MenuDialog?>(null) }
    var dialogTrack by remember { mutableStateOf<TrackEntity?>(null) }

    CompositionLocalProvider(
        LocalTrackMenu provides viewModel::show,
        LocalTrackMenuById provides { id: Long -> viewModel.showById(id) },
    ) { content() }

    val current = track
    if (current != null) {
        // 菜单项较多，直接完全展开，避免下面的选项被截掉
        ModalBottomSheet(
            onDismissRequest = viewModel::dismiss,
            sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
            modifier = Modifier.testTag("track_menu"),
        ) {
            Column(Modifier.navigationBarsPadding().verticalScroll(rememberScrollState())) {
                Text(current.title, Modifier.padding(horizontal = 24.dp), style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(trackSubtitle(current), Modifier.padding(horizontal = 24.dp, vertical = 4.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                MenuItem(R.drawable.ic_queue_play_next, R.string.menu_play_next) { viewModel.playNext(current); viewModel.dismiss() }
                MenuItem(R.drawable.ic_queue, R.string.menu_add_to_queue) { viewModel.addToQueue(current); viewModel.dismiss() }
                MenuItem(R.drawable.ic_playlist_add, R.string.menu_add_to_playlist) {
                    dialogTrack = current; dialog = MenuDialog.Playlist; viewModel.dismiss()
                }
                MenuItem(R.drawable.ic_person, R.string.menu_go_to_artist) {
                    scope.launch {
                        val artists = viewModel.artistsOf(current)
                        viewModel.dismiss()
                        when (artists.size) {
                            0 -> Unit
                            1 -> navigator.openArtist(artists.first().nameKey)
                            else -> { dialogTrack = current; dialog = MenuDialog.Artists(artists) }
                        }
                    }
                }
                current.albumKey?.let { key ->
                    MenuItem(R.drawable.ic_album, R.string.menu_go_to_album) { navigator.openAlbum(key); viewModel.dismiss() }
                }
                MenuItem(R.drawable.ic_folder, R.string.menu_open_folder) {
                    scope.launch {
                        val folder = viewModel.folderOf(current)
                        viewModel.dismiss()
                        if (folder != null) {
                            val name = current.relativePath.trimEnd('/').substringAfterLast('/').ifEmpty { folder.displayName }
                            navigator.openFolder(folder.id, current.relativePath, name)
                        }
                    }
                }
                MenuItem(R.drawable.ic_image, R.string.menu_search_cover) { dialogTrack = current; dialog = MenuDialog.Cover; viewModel.dismiss() }
                MenuItem(R.drawable.ic_bolt, R.string.menu_edit_tags) { dialogTrack = current; dialog = MenuDialog.Tags; viewModel.dismiss() }
                MenuItem(R.drawable.ic_info, R.string.menu_song_info) { dialogTrack = current; dialog = MenuDialog.Info; viewModel.dismiss() }
            }
        }
    }

    val target = dialogTrack
    when (val d = dialog) {
        MenuDialog.Playlist -> if (target != null) AddToPlaylistDialog(viewModel, target) { dialog = null }
        is MenuDialog.Artists -> AlertDialog(
            onDismissRequest = { dialog = null },
            title = { Text(stringResource(R.string.choose_artist)) },
            text = {
                LazyColumn {
                    items(d.artists) { artist ->
                        Text(
                            if (artist.nameKey == UNKNOWN_ARTIST_KEY) stringResource(R.string.unknown_artist) else artist.name,
                            Modifier.fillMaxWidth().clickable { navigator.openArtist(artist.nameKey); dialog = null }.padding(vertical = 12.dp),
                        )
                    }
                }
            },
            confirmButton = {},
            dismissButton = { TextButton(onClick = { dialog = null }) { Text(stringResource(R.string.cancel)) } },
        )
        MenuDialog.Info -> if (target != null) SongInfoDialog(target, viewModel) { dialog = null }
        MenuDialog.Tags -> if (target != null) TagEditDialog(target, viewModel) { dialog = null }
        MenuDialog.Cover -> if (target != null) CoverSearchDialog(target, viewModel, onlineCovers) { dialog = null }
        null -> Unit
    }
}

@Composable
private fun MenuItem(icon: Int, label: Int, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 24.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(painterResource(icon), contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(20.dp))
        Text(stringResource(label), style = MaterialTheme.typography.bodyLarge)
    }
}

@Composable
private fun AddToPlaylistDialog(viewModel: TrackMenuViewModel, track: TrackEntity, onDismiss: () -> Unit) {
    var lists by remember { mutableStateOf<List<PlaylistEntity>?>(null) }
    var newName by remember { mutableStateOf("") }
    LaunchedEffect(Unit) { lists = viewModel.playlists() }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.menu_add_to_playlist)) },
        text = {
            Column {
                LazyColumn(Modifier.heightIn(max = 280.dp)) {
                    items(lists.orEmpty(), key = { it.id }) { playlist ->
                        Text(
                            playlist.name,
                            Modifier.fillMaxWidth().clickable { viewModel.addToPlaylist(playlist, track); onDismiss() }.padding(vertical = 12.dp),
                        )
                    }
                }
                OutlinedTextField(
                    value = newName,
                    onValueChange = { newName = it },
                    label = { Text(stringResource(R.string.playlist_new)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth().testTag("new_playlist_name"),
                )
            }
        },
        confirmButton = {
            TextButton(enabled = newName.isNotBlank(), onClick = { viewModel.createPlaylistAndAdd(newName, track); onDismiss() }) {
                Text(stringResource(R.string.create))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}

/**
 * 修正智能标签（需求 M-4）：三个维度各一行，"自动"表示用识别结果（括号里是识别出的标签），
 * 其余选项是手动指定；手动结果优先，重新扫描或重新分析后依然保留。
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun TagEditDialog(track: TrackEntity, viewModel: TrackMenuViewModel, onDismiss: () -> Unit) {
    val scope = rememberCoroutineScope()
    var info by remember(track.id) { mutableStateOf<TagInfo?>(null) }
    LaunchedEffect(track.id) { info = viewModel.tagInfo(track) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.menu_edit_tags)) },
        text = {
            val current = info ?: return@AlertDialog
            Column(Modifier.verticalScroll(rememberScrollState())) {
                TagDimension.entries.forEach { dimension ->
                    val manual = dimension in current.manual
                    val auto = current.auto[dimension]
                    Text(dimensionLabel(dimension), Modifier.padding(top = 8.dp), style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        fun apply(block: suspend () -> Unit) = scope.launch { block(); info = viewModel.tagInfo(track) }
                        FilterChip(
                            selected = !manual,
                            onClick = { apply { viewModel.resetTag(track, dimension) } },
                            label = { Text(stringResource(R.string.tag_auto, auto?.let { tagLabel(it) } ?: stringResource(R.string.tag_none))) },
                        )
                        Tag.of(dimension).forEach { tag ->
                            FilterChip(
                                selected = manual && current.tags[dimension] == tag,
                                onClick = { apply { viewModel.setTag(track, dimension, tag) } },
                                label = { Text(tagLabel(tag)) },
                            )
                        }
                        // 人声 / 纯音乐总有一个，不提供"都不是"
                        if (dimension != TagDimension.VOICE) {
                            FilterChip(
                                selected = manual && current.tags[dimension] == null,
                                onClick = { apply { viewModel.setTag(track, dimension, null) } },
                                label = { Text(stringResource(R.string.tag_none)) },
                            )
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.confirm)) } },
    )
}

/** 歌曲信息（需求 U-3、M-7）。 */
@Composable
private fun SongInfoDialog(track: TrackEntity, viewModel: TrackMenuViewModel, onDismiss: () -> Unit) {
    val manualLabel = stringResource(R.string.tag_manual)
    var tags by remember(track.id) { mutableStateOf<TagInfo?>(null) }
    LaunchedEffect(track.id) { tags = viewModel.tagInfo(track) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(track.title) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                InfoRow(R.string.info_path, track.relativePath + track.fileName)
                InfoRow(R.string.info_album, track.album)
                InfoRow(R.string.info_track, listOfNotNull(track.discNo?.let { "$it-" }, track.trackNo?.toString()).joinToString("").ifEmpty { null })
                InfoRow(R.string.info_year, track.year?.toString())
                InfoRow(R.string.info_genre, track.genre)
                InfoRow(R.string.info_duration, formatDuration(track.durationMs))
                InfoRow(R.string.info_format, track.mimeType)
                InfoRow(R.string.info_bitrate, track.bitrate?.let { "$it kbps" })
                InfoRow(R.string.info_sample_rate, track.sampleRate?.let { "$it Hz" })
                if (track.artistInferred) {
                    Text(stringResource(R.string.info_artist_inferred), Modifier.padding(top = 8.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
                }
                tags?.let { t ->
                    // 智能标签：最终结果，手动修正的标注出来（需求 M-7）
                    val labels = TagDimension.entries.map { d ->
                        (t.tags[d]?.let { tagLabel(it) } ?: "–") + if (d in t.manual) "（$manualLabel）" else ""
                    }
                    InfoRow(R.string.info_tags, labels.joinToString(" · "))
                }
                tags?.features?.takeIf { !it.failed }?.let { f ->
                    val m = f.modelScores()
                    InfoRow(
                        R.string.info_analysis,
                        listOfNotNull(
                            f.bpm?.let { "BPM %.0f (×%.1f)".format(it, f.bpmConfidence) },
                            "%.1f LUFS".format(f.loudness),
                            "LRA %.1f".format(f.loudnessRange),
                            "%.1f onsets/s".format(f.onsetRate),
                            "%.0f Hz".format(f.centroidHz),
                            "low %.0f%%".format(f.lowRatio * 100),
                        ).joinToString(" · ") + (
                            m?.let {
                                "\nvocal %.2f · classical %.2f · sport %.2f · quiet %.2f · orchestra %.2f".format(
                                    it.vocal, it.classical, TagClassifier.sportScore(it), TagClassifier.quietScore(it), it.orchestra,
                                )
                            } ?: ""
                        ),
                    )
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.confirm)) } },
    )
}

@Composable
private fun InfoRow(label: Int, value: String?) {
    if (value.isNullOrEmpty()) return
    Column(Modifier.padding(vertical = 4.dp)) {
        Text(stringResource(label), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodyMedium)
    }
}
