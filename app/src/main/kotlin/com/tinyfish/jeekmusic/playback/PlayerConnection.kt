package com.tinyfish.jeekmusic.playback

import android.content.ComponentName
import android.content.Context
import android.net.Uri
import androidx.compose.runtime.Immutable
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.tinyfish.jeekmusic.data.db.TrackEntity
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.guava.await
import kotlinx.coroutines.launch
import javax.inject.Inject
import javax.inject.Singleton

@Immutable
data class NowPlaying(
    val trackId: Long,
    val title: String,
    val artist: String?,
    val album: String?,
    val artworkUri: Uri?,
    val trackKey: String?,
    val albumKey: String?,
)

@Immutable
data class QueueItem(val uid: String, val trackId: Long, val title: String, val artist: String?)

enum class RepeatMode { OFF, ALL, ONE }

@Immutable
data class PlayerUiState(
    val nowPlaying: NowPlaying? = null,
    val isPlaying: Boolean = false,
    val durationMs: Long = 0,
    val shuffle: Boolean = false,
    val repeat: RepeatMode = RepeatMode.OFF,
    val queue: List<QueueItem> = emptyList(),
    val currentIndex: Int = -1,
)

/**
 * UI 侧对 MediaController 的封装（docs/design.md §7.7）。
 * 连接是懒建立的：第一次播放操作或首帧之后才连接，避免启动时就创建 PlaybackService 和 ExoPlayer。
 */
@Singleton
class PlayerConnection @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var controllerDeferred: Deferred<MediaController>? = null
    private var controller: MediaController? = null

    private val _state = MutableStateFlow(PlayerUiState())
    val state: StateFlow<PlayerUiState> = _state.asStateFlow()

    fun connect() {
        if (controllerDeferred != null) return
        controllerDeferred = scope.async {
            val token = SessionToken(context, ComponentName(context, PlaybackService::class.java))
            MediaController.Builder(context, token).buildAsync().await().also(::attach)
        }
    }

    /** 播放进度；只有在被收集时才轮询（docs/design.md §7.7）。 */
    fun position(intervalMs: Long): Flow<Long> = flow {
        while (true) {
            emit(controller?.currentPosition ?: 0L)
            delay(intervalMs)
        }
    }.flowOn(Dispatchers.Main.immediate)

    /** 用 [tracks] 替换队列并从 [startIndex] 开始播放。 */
    fun play(tracks: List<TrackEntity>, startIndex: Int = 0, shuffle: Boolean = false) = withController {
        if (tracks.isEmpty()) return@withController
        shuffleModeEnabled = shuffle
        val start = if (shuffle && startIndex == 0) tracks.indices.random() else startIndex
        setMediaItems(tracks.map(MediaItemMapper::toRequest), start, 0)
        prepare()
        play()
    }

    fun playNext(tracks: List<TrackEntity>) = withController {
        if (mediaItemCount == 0) return@withController play(tracks)
        addMediaItems(currentMediaItemIndex + 1, tracks.map(MediaItemMapper::toRequest))
    }

    fun addToQueue(tracks: List<TrackEntity>) = withController {
        if (mediaItemCount == 0) return@withController play(tracks)
        addMediaItems(tracks.map(MediaItemMapper::toRequest))
    }

    fun togglePlayPause() = withController {
        if (isPlaying) {
            pause()
        } else {
            if (playbackState == Player.STATE_ENDED) seekToDefaultPosition()
            play()
        }
    }

    fun next() = withController { seekToNextMediaItem() }

    fun previous() = withController { seekToPrevious() }

    fun seekTo(positionMs: Long) = withController { seekTo(positionMs) }

    fun toggleShuffle() = withController { shuffleModeEnabled = !shuffleModeEnabled }

    fun cycleRepeat() = withController {
        repeatMode = when (repeatMode) {
            Player.REPEAT_MODE_OFF -> Player.REPEAT_MODE_ALL
            Player.REPEAT_MODE_ALL -> Player.REPEAT_MODE_ONE
            else -> Player.REPEAT_MODE_OFF
        }
    }

    fun skipToQueueItem(index: Int) = withController {
        seekToDefaultPosition(index)
        play()
    }

    fun removeQueueItem(index: Int) = withController { removeMediaItem(index) }

    fun moveQueueItem(from: Int, to: Int) = withController { moveMediaItem(from, to) }

    private fun <T> withController(block: MediaController.() -> T) {
        connect()
        scope.launch { controllerDeferred!!.await().block() }
    }

    private fun attach(controller: MediaController) {
        this.controller = controller
        controller.addListener(object : Player.Listener {
            override fun onEvents(player: Player, events: Player.Events) {
                val queueChanged = events.contains(Player.EVENT_TIMELINE_CHANGED)
                update(player, rebuildQueue = queueChanged)
            }
        })
        update(controller, rebuildQueue = true)
    }

    private fun update(player: Player, rebuildQueue: Boolean) {
        val item = player.currentMediaItem
        val metadata = player.mediaMetadata
        val nowPlaying = item?.let {
            NowPlaying(
                trackId = MediaItemMapper.trackId(it.mediaId) ?: -1,
                title = metadata.title?.toString().orEmpty(),
                artist = metadata.artist?.toString(),
                album = metadata.albumTitle?.toString(),
                artworkUri = metadata.artworkUri,
                trackKey = metadata.extras?.getString(MediaItemMapper.EXTRA_TRACK_KEY),
                albumKey = metadata.extras?.getString(MediaItemMapper.EXTRA_ALBUM_KEY),
            )
        }
        val previous = _state.value
        _state.value = PlayerUiState(
            nowPlaying = nowPlaying,
            isPlaying = player.isPlaying,
            durationMs = player.duration.takeIf { it > 0 } ?: metadata.durationMs ?: 0,
            shuffle = player.shuffleModeEnabled,
            repeat = when (player.repeatMode) {
                Player.REPEAT_MODE_ALL -> RepeatMode.ALL
                Player.REPEAT_MODE_ONE -> RepeatMode.ONE
                else -> RepeatMode.OFF
            },
            queue = if (rebuildQueue) buildQueue(player) else previous.queue,
            currentIndex = player.currentMediaItemIndex,
        )
    }

    private fun buildQueue(player: Player): List<QueueItem> = List(player.mediaItemCount) { i ->
        val item: MediaItem = player.getMediaItemAt(i)
        QueueItem(
            uid = "$i:${item.mediaId}",
            trackId = MediaItemMapper.trackId(item.mediaId) ?: -1,
            title = item.mediaMetadata.title?.toString().orEmpty(),
            artist = item.mediaMetadata.artist?.toString(),
        )
    }
}
