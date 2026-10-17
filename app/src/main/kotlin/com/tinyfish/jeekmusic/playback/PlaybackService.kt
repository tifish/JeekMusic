package com.tinyfish.jeekmusic.playback

import android.content.Intent
import android.os.Bundle
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.Timeline
import androidx.media3.session.LibraryResult
import androidx.media3.session.MediaLibraryService
import androidx.media3.session.MediaSession
import androidx.annotation.OptIn
import androidx.media3.common.util.UnstableApi
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionError
import androidx.media3.session.SessionResult
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.tinyfish.jeekmusic.data.library.LibraryRepository
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.guava.future
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import javax.inject.Inject

@AndroidEntryPoint
class PlaybackService : MediaLibraryService() {

    @Inject lateinit var playerFactory: PlayerFactory
    @Inject lateinit var library: LibraryRepository
    @Inject lateinit var persistence: QueuePersistence
    @Inject lateinit var settings: SettingsRepository

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var session: MediaLibrarySession? = null
    private lateinit var sleepTimer: SleepTimer
    private lateinit var offload: OffloadController

    /** 用户已经设置过队列时，不再用恢复的队列覆盖。 */
    private var queueTouched = false
    private var saveQueueJob: Job? = null
    private var progressJob: Job? = null

    override fun onCreate() {
        super.onCreate()
        val player = playerFactory.create()
        val session = MediaLibrarySession.Builder(this, player, LibraryCallback()).build()
        this.session = session

        sleepTimer = SleepTimer(player, scope) { status ->
            session.sessionExtras = Bundle().apply {
                putLong(PlaybackCommands.EXTRA_SLEEP_END, status.endElapsedMs)
                putInt(PlaybackCommands.EXTRA_SLEEP_SONGS, status.songsLeft)
                putBoolean(PlaybackCommands.EXTRA_SLEEP_FINISH_SONG, status.finishSong)
            }
        }
        offload = OffloadController(this, player).also { it.start() }
        scope.launch { settings.settings.collect { offload.mode = it.lowPowerMode } }

        player.addListener(PersistenceListener(player))
        scope.launch { restoreQueue(player) }
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaLibrarySession? = session

    override fun onTaskRemoved(rootIntent: Intent?) {
        val player = session?.player
        savePositionBlocking()
        if (player == null || !player.playWhenReady || player.mediaItemCount == 0 ||
            player.playbackState == Player.STATE_ENDED
        ) {
            stopSelf()
        }
    }

    override fun onDestroy() {
        savePositionBlocking()
        sleepTimer.release()
        offload.stop()
        scope.cancel()
        session?.run {
            player.release()
            release()
        }
        session = null
        super.onDestroy()
    }

    /** 恢复上次的队列，但不开始播放、也不准备，用户点播放时再准备（省电，docs/design.md §7.5）。 */
    private suspend fun restoreQueue(player: Player) {
        val restored = persistence.restore() ?: return
        if (queueTouched || player.mediaItemCount > 0) return
        player.setMediaItems(restored.tracks.map(MediaItemMapper::toMediaItem), restored.index, restored.positionMs)
        player.shuffleModeEnabled = restored.shuffle
        player.repeatMode = restored.repeatMode
        queueTouched = false
    }

    private fun trackKeysOf(player: Player): List<String> = List(player.mediaItemCount) { i ->
        player.getMediaItemAt(i).mediaMetadata.extras?.getString(MediaItemMapper.EXTRA_TRACK_KEY).orEmpty()
    }.filter { it.isNotEmpty() }

    private fun saveState(player: Player) {
        val index = player.currentMediaItemIndex
        val position = player.currentPosition
        val shuffle = player.shuffleModeEnabled
        val repeat = player.repeatMode
        scope.launch { persistence.saveState(index, position, shuffle, repeat) }
    }

    private fun savePositionBlocking() {
        val player = session?.player ?: return
        if (player.mediaItemCount == 0) return
        runBlocking { persistence.saveState(player.currentMediaItemIndex, player.currentPosition, player.shuffleModeEnabled, player.repeatMode) }
    }

    /** 队列变化（防抖 1 秒）、暂停、模式变化时保存；播放中每 10 秒保存一次进度。 */
    private inner class PersistenceListener(private val player: Player) : Player.Listener {
        override fun onTimelineChanged(timeline: Timeline, reason: Int) {
            if (reason != Player.TIMELINE_CHANGE_REASON_PLAYLIST_CHANGED) return
            queueTouched = true
            saveQueueJob?.cancel()
            saveQueueJob = scope.launch {
                delay(1_000)
                persistence.saveQueue(trackKeysOf(player))
                saveState(player)
            }
        }

        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) = saveState(player)
        override fun onShuffleModeEnabledChanged(shuffleModeEnabled: Boolean) = saveState(player)
        override fun onRepeatModeChanged(repeatMode: Int) = saveState(player)

        override fun onIsPlayingChanged(isPlaying: Boolean) {
            progressJob?.cancel()
            if (isPlaying) {
                progressJob = scope.launch {
                    while (isActive) {
                        delay(10_000)
                        saveState(player)
                    }
                }
            } else {
                saveState(player)
            }
        }

        /** 某个文件无法播放时自动跳到下一首，并通知界面（需求 §4.2 健壮性）。 */
        override fun onPlayerError(error: PlaybackException) {
            val title = player.currentMediaItem?.mediaMetadata?.title?.toString().orEmpty()
            session?.broadcastCustomCommand(
                SessionCommand(PlaybackCommands.EVENT_PLAYBACK_ERROR, Bundle.EMPTY),
                Bundle().apply { putString(PlaybackCommands.EXTRA_TITLE, title) },
            )
            if (player.hasNextMediaItem()) {
                player.seekToNextMediaItem()
                player.prepare()
                player.play()
            }
        }
    }

    @OptIn(UnstableApi::class)
    private inner class LibraryCallback : MediaLibrarySession.Callback {
        /** 自定义命令（睡眠定时）只开放给本 App 的界面；系统和其他 App 使用 Media3 的默认命令集。 */
        override fun onConnect(session: MediaSession, controller: MediaSession.ControllerInfo): MediaSession.ConnectionResult {
            val builder = MediaSession.ConnectionResult.AcceptedResultBuilder(session, controller)
            if (controller.packageName == packageName) {
                builder.setAvailableSessionCommands(
                    MediaSession.ConnectionResult.DEFAULT_SESSION_AND_LIBRARY_COMMANDS.buildUpon()
                        .add(SessionCommand(PlaybackCommands.SLEEP_MINUTES, Bundle.EMPTY))
                        .add(SessionCommand(PlaybackCommands.SLEEP_SONGS, Bundle.EMPTY))
                        .add(SessionCommand(PlaybackCommands.SLEEP_CANCEL, Bundle.EMPTY))
                        .build(),
                )
            }
            return builder.build()
        }

        override fun onCustomCommand(
            session: MediaSession,
            controller: MediaSession.ControllerInfo,
            customCommand: SessionCommand,
            args: Bundle,
        ): ListenableFuture<SessionResult> {
            when (customCommand.customAction) {
                PlaybackCommands.SLEEP_MINUTES -> sleepTimer.startMinutes(
                    args.getInt(PlaybackCommands.EXTRA_MINUTES),
                    args.getBoolean(PlaybackCommands.EXTRA_FINISH_SONG),
                )
                PlaybackCommands.SLEEP_SONGS -> sleepTimer.startSongs(args.getInt(PlaybackCommands.EXTRA_COUNT))
                PlaybackCommands.SLEEP_CANCEL -> sleepTimer.cancel()
                else -> return Futures.immediateFuture(SessionResult(SessionError.ERROR_NOT_SUPPORTED))
            }
            return Futures.immediateFuture(SessionResult(SessionResult.RESULT_SUCCESS))
        }

        override fun onGetLibraryRoot(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            params: LibraryParams?,
        ): ListenableFuture<LibraryResult<MediaItem>> {
            val root = MediaItem.Builder()
                .setMediaId(ROOT_ID)
                .setMediaMetadata(MediaMetadata.Builder().setIsBrowsable(true).setIsPlayable(false).build())
                .build()
            return Futures.immediateFuture(LibraryResult.ofItem(root, params))
        }

        /**
         * Controller 只传 mediaId（"track:<id>"），这里从数据库补全播放 URI 和元数据。
         * 找不到的歌曲（已被删除）直接丢弃。
         */
        override fun onAddMediaItems(
            mediaSession: MediaSession,
            controller: MediaSession.ControllerInfo,
            mediaItems: List<MediaItem>,
        ): ListenableFuture<List<MediaItem>> = scope.future {
            queueTouched = true
            val ids = mediaItems.mapNotNull { MediaItemMapper.trackId(it.mediaId) }
            library.tracksByIds(ids).map(MediaItemMapper::toMediaItem)
        }

        /** 系统媒体控件的"恢复播放"（需求 P-7）。 */
        override fun onPlaybackResumption(
            mediaSession: MediaSession,
            controller: MediaSession.ControllerInfo,
            isForPlayback: Boolean,
        ): ListenableFuture<MediaSession.MediaItemsWithStartPosition> = scope.future {
            val restored = persistence.restore() ?: throw UnsupportedOperationException("nothing to resume")
            MediaSession.MediaItemsWithStartPosition(
                restored.tracks.map(MediaItemMapper::toMediaItem),
                restored.index,
                restored.positionMs,
            )
        }
    }

    private companion object {
        const val ROOT_ID = "root"
    }
}

/** 界面与播放服务之间的自定义命令和事件。 */
object PlaybackCommands {
    const val SLEEP_MINUTES = "jeek.sleep.minutes"
    const val SLEEP_SONGS = "jeek.sleep.songs"
    const val SLEEP_CANCEL = "jeek.sleep.cancel"
    const val EVENT_PLAYBACK_ERROR = "jeek.event.playback_error"

    const val EXTRA_MINUTES = "minutes"
    const val EXTRA_FINISH_SONG = "finish_song"
    const val EXTRA_COUNT = "count"
    const val EXTRA_TITLE = "title"
    const val EXTRA_SLEEP_END = "sleep_end_elapsed"
    const val EXTRA_SLEEP_SONGS = "sleep_songs_left"
    const val EXTRA_SLEEP_FINISH_SONG = "sleep_finish_song"
}
