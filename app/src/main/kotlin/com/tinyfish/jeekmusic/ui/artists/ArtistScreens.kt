package com.tinyfish.jeekmusic.ui.artists

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.offset
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.unit.IntOffset
import kotlinx.coroutines.delay
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.lazy.itemsIndexed
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
import com.tinyfish.jeekmusic.playback.PlayerConnection
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

/**
 * 右侧字母快速索引：点击或拖动跳到对应分组。
 * 拖动索引或滚动列表时，索引左侧浮动显示当前字母，停止后淡出。
 */
@Composable
private fun LetterIndex(letters: List<String>, listState: LazyListState, headerIndex: Map<String, Int>, modifier: Modifier = Modifier) {
    val scope = rememberCoroutineScope()
    // 手指按在索引上时指向的字母；没按时为 -1
    var touched by remember { mutableIntStateOf(-1) }
    var indexHeight by remember { mutableIntStateOf(0) }
    // 列表第一个可见项所在的分组
    val starts = remember(letters, headerIndex) { letters.map { headerIndex[it] ?: 0 } }
    val firstVisible by remember(starts) {
        derivedStateOf {
            val first = listState.firstVisibleItemIndex
            starts.indexOfLast { it <= first }.coerceAtLeast(0)
        }
    }
    val shown = if (touched >= 0) touched else firstVisible

    val active = touched >= 0 || listState.isScrollInProgress
    var bubbleVisible by remember { mutableStateOf(false) }
    LaunchedEffect(active) {
        if (active) bubbleVisible = true else { delay(800); bubbleVisible = false }
    }

    fun touch(y: Float, height: Int) {
        if (letters.isEmpty() || height <= 0) return
        val i = (y / height * letters.size).toInt().coerceIn(0, letters.lastIndex)
        if (i == touched) return
        touched = i
        headerIndex[letters[i]]?.let { scope.launch { listState.scrollToItem(it) } }
    }

    Box(modifier.fillMaxHeight()) {
        Column(
            Modifier
                .align(Alignment.CenterEnd)
                .fillMaxHeight()
                .padding(vertical = IndexPadding)
                .width(24.dp)
                .onSizeChanged { indexHeight = it.height }
                .testTag("letter_index")
                .pointerInput(letters) {
                    awaitEachGesture {
                        val down = awaitFirstDown()
                        down.consume()
                        touch(down.position.y, size.height)
                        while (true) {
                            val change = awaitPointerEvent().changes.firstOrNull { it.id == down.id } ?: break
                            if (!change.pressed) break
                            change.consume()
                            touch(change.position.y, size.height)
                        }
                        touched = -1
                    }
                },
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            // 每个字母占相同高度，和 touch() 中按高度均分的换算一致，气泡也能对准字母
            letters.forEach {
                Box(Modifier.weight(1f), contentAlignment = Alignment.Center) {
                    Text(it, fontSize = 11.sp, fontWeight = FontWeight.Medium, color = MaterialTheme.colorScheme.primary)
                }
            }
        }
        AnimatedVisibility(
            visible = bubbleVisible && letters.isNotEmpty(),
            enter = fadeIn(),
            exit = fadeOut(),
            modifier = Modifier
                .align(Alignment.TopEnd)
                .padding(end = 24.dp + 8.dp)
                .offset {
                    val i = shown.coerceIn(0, (letters.size - 1).coerceAtLeast(0))
                    val center = IndexPadding.roundToPx() + (i + 0.5f) * indexHeight / letters.size.coerceAtLeast(1)
                    IntOffset(0, (center - BubbleSize.toPx() / 2).toInt().coerceAtLeast(0))
                },
        ) {
            Box(
                Modifier
                    .size(BubbleSize)
                    .clip(CircleShape)
                    .background(MaterialTheme.colorScheme.primaryContainer)
                    .testTag("letter_bubble"),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    letters.getOrElse(shown) { "" },
                    style = MaterialTheme.typography.headlineMedium,
                    color = MaterialTheme.colorScheme.onPrimaryContainer,
                )
            }
        }
    }
}

private val IndexPadding = 8.dp
private val BubbleSize = 56.dp

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

    /** 直接显示全部歌曲，不按专辑分组（需求 A-6）。 */
    val tracks: StateFlow<List<TrackEntity>?> = library.artistTracks(nameKey)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    val currentTrackId: StateFlow<Long?> = player.state.map { it.nowPlaying?.trackId }.distinctUntilChanged()
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    fun play(index: Int) {
        tracks.value?.let { player.play(it, index) }
    }

    fun playAll(shuffle: Boolean) {
        tracks.value?.let { player.play(it, shuffle = shuffle) }
    }
}

@Composable
fun ArtistDetailScreen(
    route: TabRoute.ArtistDetail,
    onBack: () -> Unit,
    viewModel: ArtistDetailViewModel = hiltViewModel<ArtistDetailViewModel, ArtistDetailViewModel.Factory>(
        key = route.toString(),
        creationCallback = { it.create(route.nameKey) },
    ),
) {
    val artist by viewModel.artist.collectAsStateWithLifecycle()
    val tracks by viewModel.tracks.collectAsStateWithLifecycle()
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
        val data = tracks ?: return@Column
        PlayButtons(onPlayAll = { viewModel.playAll(false) }, onShuffle = { viewModel.playAll(true) })
        val currentRow = data.indexOfFirst { it.id == currentTrackId }
        Box(Modifier.fillMaxSize()) {
            LazyColumn(Modifier.fillMaxSize().testTag("artist_tracks"), state = listState) {
                itemsIndexed(data, key = { _, track -> track.id }, contentType = { _, _ -> "track" }) { index, track ->
                    TrackRow(
                        track = track,
                        isCurrent = track.id == currentTrackId,
                        onClick = { viewModel.play(index) },
                    )
                }
            }
            LocateCurrentButton(listState, currentRow, Modifier.align(Alignment.BottomEnd))
        }
    }
}
