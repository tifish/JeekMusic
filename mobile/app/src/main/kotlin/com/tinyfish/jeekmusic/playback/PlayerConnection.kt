package com.tinyfish.jeekmusic.playback

import android.content.ComponentName
import android.content.Context
import android.net.Uri
import android.os.Bundle
import androidx.compose.runtime.Immutable
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionResult
import androidx.media3.session.SessionToken
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.tinyfish.jeekmusic.startup.Snapshot
import com.tinyfish.jeekmusic.startup.StartupSnapshot
import com.tinyfish.jeekmusic.data.db.TrackEntity
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
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
data class QueueItem(val uid: String, val trackId: Long, val title: String, val artist: String?, val manual: Boolean = false)

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
    val sleepTimer: SleepTimerState = SleepTimerState(),
    /** 队列来自的列表的名称（需求 P-15）；null 表示没有。 */
    val sourceTitle: String? = null,
)

/**
 * 睡眠定时状态（需求 P-8）：[endElapsedMs] 是 SystemClock.elapsedRealtime() 时间，0 表示不按时间；
 * [songsLeft] 是还要播完的歌曲数，0 表示不按歌曲数；[finishSong] 表示时间到后播完当前这首再停止。
 */
@Immutable
data class SleepTimerState(val endElapsedMs: Long = 0, val songsLeft: Int = 0, val finishSong: Boolean = false) {
    val active: Boolean get() = endElapsedMs > 0 || songsLeft > 0
}

/**
 * UI 侧对 MediaController 的封装（docs/design.md §7.7）。
 * 连接是懒建立的：第一次播放操作或首帧之后才连接，避免启动时就创建 PlaybackService 和 ExoPlayer。
 */
@Singleton
class PlayerConnection @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val snapshot: StartupSnapshot,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var controllerDeferred: Deferred<MediaController>? = null
    private var controller: MediaController? = null

    private val _state = MutableStateFlow(PlayerUiState())
    val state: StateFlow<PlayerUiState> = _state.asStateFlow()

    /** 播放出错时的歌曲标题，界面据此弹出提示。 */
    private val _errors = MutableSharedFlow<String>(extraBufferCapacity = 4)
    val errors: SharedFlow<String> = _errors.asSharedFlow()

    /** 连接建立前，用启动快照里的数据填充迷你播放条（docs/design.md §9.2）。 */
    fun applySnapshot(value: Snapshot) {
        if (controller != null || _state.value.nowPlaying != null || value.nowPlayingTrackId < 0) return
        _state.value = _state.value.copy(
            nowPlaying = NowPlaying(
                trackId = value.nowPlayingTrackId,
                title = value.nowPlayingTitle.orEmpty(),
                artist = value.nowPlayingArtist,
                album = null,
                artworkUri = MediaItemMapper.artworkUri(value.nowPlayingTrackId, large = true),
                trackKey = null,
                albumKey = null,
            ),
        )
    }

    fun connect() {
        if (controllerDeferred != null) return
        controllerDeferred = scope.async {
            val token = SessionToken(context, ComponentName(context, PlaybackService::class.java))
            MediaController.Builder(context, token)
                .setListener(object : MediaController.Listener {
                    override fun onExtrasChanged(controller: MediaController, extras: Bundle) = updateExtras(extras)

                    override fun onCustomCommand(controller: MediaController, command: SessionCommand, args: Bundle): ListenableFuture<SessionResult> {
                        if (command.customAction == PlaybackCommands.EVENT_PLAYBACK_ERROR) {
                            _errors.tryEmit(args.getString(PlaybackCommands.EXTRA_TITLE).orEmpty())
                        }
                        return Futures.immediateFuture(SessionResult(SessionResult.RESULT_SUCCESS))
                    }
                })
                .buildAsync().await().also(::attach)
        }
    }

    /** 播放进度；只有在被收集时才轮询（docs/design.md §7.7）。 */
    fun position(intervalMs: Long): Flow<Long> = flow {
        while (true) {
            emit(controller?.currentPosition ?: 0L)
            delay(intervalMs)
        }
    }.flowOn(Dispatchers.Main.immediate)

    /**
     * 播放某个列表（需求 P-15）：[tracks] 是列表里的歌（按显示顺序），[title] 是列表名称，播放队列里显示"来自 …"。
     * 从 [startIndex] 开始；为 null 时从头开始，随机时随机挑一首。[shuffle] 为 null 时保持当前的随机模式。
     * 之后筛选、排序或曲库变了，播放服务按 [source] 重新生成队列。
     */
    fun play(source: QueueSource, title: String, tracks: List<TrackEntity>, startIndex: Int? = null, shuffle: Boolean? = null) = withController {
        if (tracks.isEmpty()) return@withController
        sendCustomCommand(
            SessionCommand(PlaybackCommands.PLAY_SOURCE, Bundle.EMPTY),
            Bundle().apply {
                putString(PlaybackCommands.EXTRA_SOURCE, source.encode())
                putString(PlaybackCommands.EXTRA_SOURCE_TITLE, title)
                putLongArray(PlaybackCommands.EXTRA_TRACK_IDS, tracks.map { it.id }.toLongArray())
                putInt(PlaybackCommands.EXTRA_START, startIndex ?: -1)
                if (shuffle != null) putBoolean(PlaybackCommands.EXTRA_SHUFFLE, shuffle)
            },
        )
    }

    /** "下一首播放"：插到当前歌曲后面，作为手动添加的歌（需求 P-15）。 */
    fun playNext(tracks: List<TrackEntity>) = sendTracks(PlaybackCommands.PLAY_NEXT, tracks)

    /** "添加到队列"：加到手动添加的歌的最后，在来自列表的歌前面（需求 P-15）。 */
    fun addToQueue(tracks: List<TrackEntity>) = sendTracks(PlaybackCommands.ADD_TO_QUEUE, tracks)

    private fun sendTracks(action: String, tracks: List<TrackEntity>) = withController {
        if (tracks.isEmpty()) return@withController
        sendCustomCommand(
            SessionCommand(action, Bundle.EMPTY),
            Bundle().apply { putLongArray(PlaybackCommands.EXTRA_TRACK_IDS, tracks.map { it.id }.toLongArray()) },
        )
    }

    fun togglePlayPause() = withController {
        if (isPlaying) {
            pause()
        } else {
            // 恢复的队列没有准备过（省电），第一次播放时再准备
            if (playbackState == Player.STATE_IDLE) prepare()
            if (playbackState == Player.STATE_ENDED) seekToDefaultPosition()
            play()
        }
    }

    fun sleepAfterMinutes(minutes: Int, finishSong: Boolean) = withController {
        sendCustomCommand(
            SessionCommand(PlaybackCommands.SLEEP_MINUTES, Bundle.EMPTY),
            Bundle().apply {
                putInt(PlaybackCommands.EXTRA_MINUTES, minutes)
                putBoolean(PlaybackCommands.EXTRA_FINISH_SONG, finishSong)
            },
        )
    }

    fun sleepAfterSongs(count: Int) = withController {
        sendCustomCommand(SessionCommand(PlaybackCommands.SLEEP_SONGS, Bundle.EMPTY), Bundle().apply { putInt(PlaybackCommands.EXTRA_COUNT, count) })
    }

    fun cancelSleepTimer() = withController {
        sendCustomCommand(SessionCommand(PlaybackCommands.SLEEP_CANCEL, Bundle.EMPTY), Bundle.EMPTY)
    }

    fun next() = withController { seekToNextMediaItem() }

    fun previous() = withController { seekToPrevious() }

    /**
     * 跳转到当前歌曲的某个位置。重启后恢复的队列还没准备（省电），这时"在歌曲内跳转"的命令不可用、
     * 会被直接忽略，改用"跳到这首歌的这个位置"（这个命令在未准备时也可用），第一次播放就从这里开始。
     */
    fun seekTo(positionMs: Long) = withController {
        if (isCommandAvailable(Player.COMMAND_SEEK_IN_CURRENT_MEDIA_ITEM)) seekTo(positionMs) else seekTo(currentMediaItemIndex, positionMs)
    }

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
        if (playbackState == Player.STATE_IDLE) prepare()
        play()
    }

    fun removeQueueItem(index: Int) = withController { removeMediaItem(index) }

    /** 从队列里去掉这些歌（文件被删除后，需求 F-10）。 */
    fun removeTracks(trackIds: Set<Long>) = withController {
        for (i in mediaItemCount - 1 downTo 0) {
            if (MediaItemMapper.trackId(getMediaItemAt(i).mediaId) in trackIds) removeMediaItem(i)
        }
    }

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
        updateExtras(controller.sessionExtras)
    }

    private fun updateExtras(extras: Bundle) {
        _state.value = _state.value.copy(
            sleepTimer = SleepTimerState(
                endElapsedMs = extras.getLong(PlaybackCommands.EXTRA_SLEEP_END),
                songsLeft = extras.getInt(PlaybackCommands.EXTRA_SLEEP_SONGS),
                finishSong = extras.getBoolean(PlaybackCommands.EXTRA_SLEEP_FINISH_SONG),
            ),
            sourceTitle = extras.getString(PlaybackCommands.EXTRA_SOURCE_TITLE),
        )
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
            sleepTimer = previous.sleepTimer,
            sourceTitle = previous.sourceTitle,
        )
        if (nowPlaying?.trackId != previous.nowPlaying?.trackId) {
            scope.launch { snapshot.saveNowPlaying(nowPlaying?.trackId ?: -1, nowPlaying?.title, nowPlaying?.artist) }
        }
    }

    private fun buildQueue(player: Player): List<QueueItem> = List(player.mediaItemCount) { i ->
        val item: MediaItem = player.getMediaItemAt(i)
        QueueItem(
            uid = "$i:${item.mediaId}",
            trackId = MediaItemMapper.trackId(item.mediaId) ?: -1,
            title = item.mediaMetadata.title?.toString().orEmpty(),
            artist = item.mediaMetadata.artist?.toString(),
            manual = MediaItemMapper.isManual(item),
        )
    }
}
