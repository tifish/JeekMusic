package com.tinyfish.jeekmusic.ui.artists

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.ArtistEntity
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.db.UNKNOWN_ARTIST_KEY
import com.tinyfish.jeekmusic.data.library.LibraryRepository
import com.tinyfish.jeekmusic.playback.MediaItemMapper
import com.tinyfish.jeekmusic.playback.PlayerConnection
import com.tinyfish.jeekmusic.ui.components.Artwork
import com.tinyfish.jeekmusic.ui.components.CenteredMessage
import com.tinyfish.jeekmusic.ui.components.LocateCurrentButton
import com.tinyfish.jeekmusic.ui.components.PlayButtons
import com.tinyfish.jeekmusic.ui.components.SubScreenHeader
import com.tinyfish.jeekmusic.ui.components.TrackRow
import com.tinyfish.jeekmusic.ui.components.songCount
import com.tinyfish.jeekmusic.data.tabs.TabRepository
import com.tinyfish.jeekmusic.ui.navigation.Tab
import com.tinyfish.jeekmusic.ui.navigation.TabRoute
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.ui.res.painterResource
import dagger.assisted.Assisted
import dagger.assisted.AssistedFactory
import dagger.assisted.AssistedInject
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

@Composable
fun artistName(artist: ArtistEntity): String =
    if (artist.nameKey == UNKNOWN_ARTIST_KEY) stringResource(R.string.unknown_artist) else artist.name

data class ArtistSection(val letter: String, val artists: List<ArtistEntity>)

@HiltViewModel
class ArtistListViewModel @Inject constructor(library: LibraryRepository) : ViewModel() {
    /** 按字母分组，A–Z 在前、"#" 在最后（需求 A-1）。 */
    val sections: StateFlow<List<ArtistSection>?> = library.artists.map { artists ->
        artists.groupBy { it.indexLetter }
            .map { (letter, list) -> ArtistSection(letter, list) }
            .sortedBy { if (it.letter == "#") "￿" else it.letter }
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun ArtistListScreen(
    onOpen: (ArtistEntity) -> Unit,
    viewModel: ArtistListViewModel = hiltViewModel(),
) {
    val sections by viewModel.sections.collectAsStateWithLifecycle()
    val data = sections ?: return
    if (data.isEmpty()) {
        CenteredMessage(stringResource(R.string.empty_library_title))
        return
    }
    val listState = rememberLazyListState()
    // 每个字母分组标题在列表中的位置，供右侧索引跳转
    val headerIndex = remember(data) {
        var index = 0
        data.associate { section -> (section.letter to index).also { index += section.artists.size + 1 } }
    }
    Box(Modifier.fillMaxSize()) {
        LazyColumn(Modifier.fillMaxSize().testTag("artist_list"), state = listState) {
            data.forEach { section ->
                stickyHeader(key = "h:" + section.letter, contentType = "header") {
                    Text(
                        section.letter,
                        Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surface).padding(horizontal = 16.dp, vertical = 4.dp),
                        style = MaterialTheme.typography.labelLarge,
                        color = MaterialTheme.colorScheme.primary,
                    )
                }
                items(section.artists, key = { "a:" + it.nameKey }, contentType = { "artist" }) { artist ->
                    ArtistRow(artist, onClick = { onOpen(artist) })
                }
            }
        }
        LetterIndex(
            letters = data.map { it.letter },
            listState = listState,
            headerIndex = headerIndex,
            modifier = Modifier.align(Alignment.CenterEnd),
        )
    }
}

@Composable
private fun ArtistRow(artist: ArtistEntity, onClick: () -> Unit) {
    val name = artistName(artist)
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(start = 16.dp, end = 32.dp, top = 8.dp, bottom = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier.size(40.dp).clip(CircleShape).background(MaterialTheme.colorScheme.secondaryContainer),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                name.take(1).uppercase(),
                color = MaterialTheme.colorScheme.onSecondaryContainer,
                style = MaterialTheme.typography.titleMedium,
            )
        }
        Spacer(Modifier.width(16.dp))
        Text(name, Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
        Text(songCount(artist.trackCount), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** 右侧字母快速索引：点击或拖动跳到对应分组。 */
@Composable
private fun LetterIndex(letters: List<String>, listState: LazyListState, headerIndex: Map<String, Int>, modifier: Modifier = Modifier) {
    val scope = rememberCoroutineScope()
    fun jump(y: Float, height: Int) {
        if (letters.isEmpty() || height <= 0) return
        val i = (y / height * letters.size).toInt().coerceIn(0, letters.lastIndex)
        headerIndex[letters[i]]?.let { scope.launch { listState.scrollToItem(it) } }
    }
    Column(
        modifier
            .fillMaxHeight()
            .padding(vertical = 8.dp)
            .width(24.dp)
            .testTag("letter_index")
            .pointerInput(letters) { detectTapGestures { jump(it.y, size.height) } }
            .pointerInput(letters) { detectVerticalDragGestures { change, _ -> jump(change.position.y, size.height) } },
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = androidx.compose.foundation.layout.Arrangement.SpaceEvenly,
    ) {
        letters.forEach {
            Text(it, fontSize = 11.sp, fontWeight = FontWeight.Medium, color = MaterialTheme.colorScheme.primary)
        }
    }
}

/** 艺术家详情中按专辑分组的一段。 */
data class AlbumGroup(val albumKey: String?, val album: String?, val year: Int?, val firstTrackId: Long, val tracks: List<IndexedValue<TrackEntity>>)

@HiltViewModel(assistedFactory = ArtistDetailViewModel.Factory::class)
class ArtistDetailViewModel @AssistedInject constructor(
    @Assisted private val nameKey: String,
    library: LibraryRepository,
    private val player: PlayerConnection,
    private val tabs: TabRepository,
) : ViewModel() {

    val pinned: StateFlow<Boolean> = tabs.pinnedArtistKeys.map { nameKey in it }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), false)

    /** 固定到标签栏 / 取消固定（需求 T-3）。 */
    fun togglePin() = viewModelScope.launch {
        if (pinned.value) tabs.unpin(Tab.PinnedArtist(nameKey, "")) else tabs.pinArtist(nameKey)
    }

    @AssistedFactory
    interface Factory {
        fun create(nameKey: String): ArtistDetailViewModel
    }

    val artist: StateFlow<ArtistEntity?> = library.artist(nameKey)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    private val tracks: StateFlow<List<TrackEntity>?> = library.artistTracks(nameKey)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    /** 直接显示全部歌曲，按专辑分组（需求 A-6）。 */
    val groups: StateFlow<List<AlbumGroup>?> = tracks.map { list ->
        list?.withIndex()?.groupBy { it.value.albumKey ?: "none" }?.values?.map { items ->
            val first = items.first().value
            AlbumGroup(first.albumKey, first.album, items.mapNotNull { it.value.year }.minOrNull(), first.id, items)
        }
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    val currentTrackId: StateFlow<Long?> = player.state.map { it.nowPlaying?.trackId }.distinctUntilChanged()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    fun play(index: Int) {
        tracks.value?.let { player.play(it, index) }
    }

    fun playAll(shuffle: Boolean) {
        tracks.value?.let { player.play(it, shuffle = shuffle) }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun ArtistDetailScreen(
    route: TabRoute.ArtistDetail,
    onBack: () -> Unit,
    onOpenAlbum: (String) -> Unit,
    viewModel: ArtistDetailViewModel = hiltViewModel<ArtistDetailViewModel, ArtistDetailViewModel.Factory>(
        key = route.toString(),
        creationCallback = { it.create(route.nameKey) },
    ),
) {
    val artist by viewModel.artist.collectAsStateWithLifecycle()
    val groups by viewModel.groups.collectAsStateWithLifecycle()
    val currentTrackId by viewModel.currentTrackId.collectAsStateWithLifecycle()
    val pinned by viewModel.pinned.collectAsStateWithLifecycle()
    val listState = rememberLazyListState()
    val pinAction = @Composable {
        IconButton(onClick = viewModel::togglePin, modifier = Modifier.testTag("pin_artist")) {
            Icon(
                painterResource(R.drawable.ic_push_pin),
                contentDescription = stringResource(if (pinned) R.string.menu_unpin_tab else R.string.menu_pin_tab),
                tint = if (pinned) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }

    Column(Modifier.fillMaxSize()) {
        val name = artist?.let { artistName(it) }.orEmpty()
        val subtitle = artist?.let { songCount(it.trackCount) }
        if (route.isTabRoot) {
            Row(Modifier.padding(start = 16.dp, end = 8.dp, top = 8.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(name, style = MaterialTheme.typography.titleLarge)
                    if (subtitle != null) Text(subtitle, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                pinAction()
            }
        } else {
            SubScreenHeader(title = name, subtitle = subtitle, onBack = onBack, actions = { pinAction() })
        }
        val data = groups ?: return@Column
        PlayButtons(onPlayAll = { viewModel.playAll(false) }, onShuffle = { viewModel.playAll(true) })
        var row = 0
        var currentRow = -1
        data.forEach { group ->
            row++
            group.tracks.forEach { if (it.value.id == currentTrackId) currentRow = row; row++ }
        }
        Box(Modifier.fillMaxSize()) {
        LazyColumn(Modifier.fillMaxSize().testTag("artist_tracks"), state = listState) {
            data.forEach { group ->
                stickyHeader(key = "album:" + (group.albumKey ?: "none"), contentType = "album") {
                    AlbumHeader(group, onClick = group.albumKey?.let { key -> { onOpenAlbum(key) } })
                }
                items(group.tracks, key = { it.value.id }, contentType = { "track" }) { (index, track) ->
                    TrackRow(
                        track = track,
                        isCurrent = track.id == currentTrackId,
                        onClick = { viewModel.play(index) },
                        subtitle = track.artistDisplay.ifEmpty { stringResource(R.string.unknown_artist) },
                        leading = {
                            Text(
                                track.trackNo?.toString() ?: "–",
                                Modifier.width(32.dp),
                                style = MaterialTheme.typography.labelLarge,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        },
                    )
                }
            }
        }
        LocateCurrentButton(listState, currentRow, Modifier.align(Alignment.BottomEnd))
        }
    }
}

@Composable
private fun AlbumHeader(group: AlbumGroup, onClick: (() -> Unit)?) {
    Row(
        Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface)
            .let { if (onClick != null) it.clickable(onClick = onClick) else it }
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Artwork(MediaItemMapper.artworkUri(group.firstTrackId), 40.dp)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(
                group.album ?: stringResource(R.string.unknown_album),
                style = MaterialTheme.typography.titleSmall,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            val detail = listOfNotNull(group.year?.toString(), songCount(group.tracks.size)).joinToString(" · ")
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
