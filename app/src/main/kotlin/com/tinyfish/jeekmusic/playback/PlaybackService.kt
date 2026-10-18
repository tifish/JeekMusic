package com.tinyfish.jeekmusic.playback

import android.content.Intent
import android.os.Bundle
import androidx.media3.common.C
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
import androidx.media3.session.CommandButton
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionError
import androidx.media3.session.SessionResult
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.online.AutoFetcher
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.library.QueueSourceRepository
import com.tinyfish.jeekmusic.data.library.LibraryRepository
import com.tinyfish.jeekmusic.data.library.EmbedDownloadedScheduler
import com.tinyfish.jeekmusic.data.library.PlayingFiles
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
    @Inject lateinit var trackDao: TrackDao
    @Inject lateinit var autoFetcher: AutoFetcher
    @Inject lateinit var sources: QueueSourceRepository
    @Inject lateinit var playingFiles: PlayingFiles
    @Inject lateinit var embedDownloaded: EmbedDownloadedScheduler
    @Inject lateinit var playbackLog: PlaybackLog

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var session: MediaLibrarySession? = null
    private lateinit var sleepTimer: SleepTimer
    private lateinit var offload: OffloadController
    private lateinit var diagnostics: PlaybackDiagnostics
    private lateinit var queue: QueueController
    private var sleepStatus = SleepTimer.Status()

    /** 上一次出错的歌和时间：同一首歌短时间内再出错才跳到下一首（需求 P-14）。 */
    private var lastErrorIndex = -1
    private var lastErrorAt = 0L

    /** 用户已经设置过队列时，不再用恢复的队列覆盖。 */
    private var queueTouched = false
    private var saveQueueJob: Job? = null
    private var progressJob: Job? = null
    private var fetchJob: Job? = null
    private var playingFilesJob: Job? = null

    override fun onCreate() {
        super.onCreate()
        val player = playerFactory.create()
        // 交给会话的播放器包一层：队列最后一首时"下一首"也可用（跳回第一首），通知和锁屏上总有这个按钮
        val session = MediaLibrarySession.Builder(this, NextAlwaysPlayer(player), LibraryCallback()).build()
        this.session = session

        sleepTimer = SleepTimer(player, scope) { status ->
            sleepStatus = status
            publishExtras()
        }
        queue = QueueController(player, scope, sources) { context ->
            publishExtras()
            scope.launch { persistence.saveContext(context) }
        }
        offload = OffloadController(this, player).also { it.start() }
        diagnostics = PlaybackDiagnostics(player, offload, playbackLog).also { it.start() }
        scope.launch { settings.settings.collect { offload.mode = it.lowPowerMode } }

        player.addListener(PersistenceListener(player))
        player.addListener(FetchListener(player))
        player.addListener(object : Player.Listener {
            override fun onEvents(player: Player, events: Player.Events) {
                if (events.containsAny(
                        Player.EVENT_MEDIA_ITEM_TRANSITION, Player.EVENT_TIMELINE_CHANGED, Player.EVENT_PLAYBACK_STATE_CHANGED,
                        Player.EVENT_SHUFFLE_MODE_ENABLED_CHANGED, Player.EVENT_REPEAT_MODE_CHANGED,
                    )
                ) {
                    updatePlayingFiles(player)
                }
            }
        })
        // 通知栏、锁屏上的随机播放开关跟着当前状态换图标（需求 P-13）
        player.addListener(object : Player.Listener {
            @OptIn(UnstableApi::class)
            override fun onShuffleModeEnabledChanged(shuffleModeEnabled: Boolean) {
                session.setMediaButtonPreferences(listOf(shuffleButton(shuffleModeEnabled)))
            }
        })
        // 当前歌曲的封面换了（播放时获取、批量获取、手动选择都会）：通知栏、锁屏按地址缓存图片，换个带版本号的地址才会重新加载
        scope.launch { ArtworkVersions.versions.collect { refreshArtwork(player) } }
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
        diagnostics.stop()
        embedDownloaded.scheduleFor(playingFiles.update(emptySet()))
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
        player.setMediaItems(restored.mediaItems(), restored.index, restored.positionMs)
        queue.restoreShuffle(restored.shuffle)
        player.repeatMode = restored.repeatMode
        queue.restoreContext(restored.context)
        queueTouched = false
    }

    private fun RestoredQueue.mediaItems(): List<MediaItem> = tracks.mapIndexed { i, track -> MediaItemMapper.toMediaItem(track, manual = i in manual) }

    /** 睡眠定时状态和队列来自的列表名称，界面通过会话的 extras 读取。 */
    private fun publishExtras() {
        val status = sleepStatus
        session?.sessionExtras = Bundle().apply {
            putLong(PlaybackCommands.EXTRA_SLEEP_END, status.endElapsedMs)
            putInt(PlaybackCommands.EXTRA_SLEEP_SONGS, status.songsLeft)
            putBoolean(PlaybackCommands.EXTRA_SLEEP_FINISH_SONG, status.finishSong)
            queue.context?.let { putString(PlaybackCommands.EXTRA_SOURCE_TITLE, it.title) }
        }
    }

    /** 当前歌曲的封面地址加上版本号；只改元数据，Media3 不会打断播放。 */
    /**
     * 播放器正在用的文件：准备好（或正在缓冲）时的当前这首和下一首（无缝播放会提前加载）。
     * 不再用的文件如果有推迟的写入，现在写进去（需求 P-14，[PlayingFiles]）。
     */
    private fun updatePlayingFiles(player: Player) {
        val active = player.playbackState == Player.STATE_READY || player.playbackState == Player.STATE_BUFFERING
        val ids = if (!active) emptyList() else listOf(player.currentMediaItemIndex, player.nextMediaItemIndex)
            .filter { it in 0 until player.mediaItemCount }
            .mapNotNull { MediaItemMapper.trackId(player.getMediaItemAt(it).mediaId) }
        playingFilesJob?.cancel()
        playingFilesJob = scope.launch {
            val keys = ids.mapNotNull { trackDao.byId(it)?.fileKey }.toSet()
            embedDownloaded.scheduleFor(playingFiles.update(keys))
        }
    }

    private fun refreshArtwork(player: Player) {
        val index = player.currentMediaItemIndex
        if (index !in 0 until player.mediaItemCount) return
        val item = player.getMediaItemAt(index)
        val trackId = MediaItemMapper.trackId(item.mediaId) ?: return
        val uri = ArtworkVersions.withVersion(MediaItemMapper.artworkUri(trackId, large = true), ArtworkVersions.versions.value)
        if (item.mediaMetadata.artworkUri == uri) return
        player.replaceMediaItem(index, item.buildUpon().setMediaMetadata(item.mediaMetadata.buildUpon().setArtworkUri(uri).build()).build())
    }

    /**
     * 换歌时：先确保封面地址是最新的；自动获取打开时，为这首歌获取缺少的封面和歌词（需求 C-3、LY-8，[AutoFetcher]）。
     * 稍等一下再查，快速连续切歌时只查最后停下的那首；已经开始的获取不因切歌而中断（另起一个协程）。
     */
    private inner class FetchListener(private val player: Player) : Player.Listener {
        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            refreshArtwork(player)
            val trackId = mediaItem?.mediaId?.let(MediaItemMapper::trackId)
            fetchJob?.cancel()
            if (trackId == null) return
            fetchJob = scope.launch {
                delay(FETCH_DELAY_MS)
                val track = trackDao.byId(trackId) ?: return@launch
                scope.launch { autoFetcher.fetchFor(track) }
            }
        }
    }

    /**
     * 通知栏、锁屏上的随机播放开关（需求 P-13）：图标表示当前状态，点一下切换。
     * 关闭时和其他按钮一样（Media3 自带的关闭图标是灰的，像不能点），开启时图标下面多一个圆点。
     */
    @OptIn(UnstableApi::class)
    private fun shuffleButton(enabled: Boolean): CommandButton =
        CommandButton.Builder(if (enabled) CommandButton.ICON_SHUFFLE_ON else CommandButton.ICON_SHUFFLE_OFF)
            .setCustomIconResId(if (enabled) R.drawable.ic_shuffle_on_dot else R.drawable.ic_shuffle)
            .setDisplayName(getString(if (enabled) R.string.shuffle_on else R.string.shuffle_off))
            .setSessionCommand(SessionCommand(PlaybackCommands.TOGGLE_SHUFFLE, Bundle.EMPTY))
            .setSlots(CommandButton.SLOT_OVERFLOW)
            .build()

    private fun savedItemsOf(player: Player): List<SavedQueueItem> = List(player.mediaItemCount) { i ->
        val item = player.getMediaItemAt(i)
        SavedQueueItem(item.mediaMetadata.extras?.getString(MediaItemMapper.EXTRA_TRACK_KEY).orEmpty(), MediaItemMapper.isManual(item))
    }.filter { it.trackKey.isNotEmpty() }

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
                persistence.saveQueue(savedItemsOf(player))
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

        /**
         * 出错时（需求 P-14）：先从出错的位置重试这首歌，并暂时不用 offload——低功耗播放的音频输出偶尔会出错，
         * 以前一出错就跳到下一首，听起来就是"歌没放完就换了"。同一首歌 30 秒内再出错（文件坏了、格式不支持等）
         * 才跳到下一首，并通知界面（需求 §4.2 健壮性）。
         */
        override fun onPlayerError(error: PlaybackException) {
            val title = player.currentMediaItem?.mediaMetadata?.title?.toString().orEmpty()
            val index = player.currentMediaItemIndex
            val position = player.currentPosition
            val now = android.os.SystemClock.elapsedRealtime()
            val repeated = index == lastErrorIndex && now - lastErrorAt < ERROR_RETRY_WINDOW_MS
            lastErrorIndex = index
            lastErrorAt = now
            playbackLog.add("error ${error.errorCodeName}: $title at ${position / 1000}s, offload=${diagnostics.offloaded}, ${if (repeated) "skip" else "retry"}")
            if (!repeated && index != C.INDEX_UNSET) {
                offload.suspendUntilNextItem()
                player.seekTo(index, position)
                player.prepare()
                player.play()
                return
            }
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
        /**
         * 自定义命令：睡眠定时只开放给本 App 的界面；随机播放开关开放给本 App 和通知栏（锁屏、系统媒体控件跟着通知栏）。
         * 其他 App 使用 Media3 的默认命令集。
         */
        override fun onConnect(session: MediaSession, controller: MediaSession.ControllerInfo): MediaSession.ConnectionResult {
            val builder = MediaSession.ConnectionResult.AcceptedResultBuilder(session, controller)
            val own = controller.packageName == packageName
            val notification = session.isMediaNotificationController(controller)
            if (own || notification) {
                val commands = MediaSession.ConnectionResult.DEFAULT_SESSION_AND_LIBRARY_COMMANDS.buildUpon()
                    .add(SessionCommand(PlaybackCommands.TOGGLE_SHUFFLE, Bundle.EMPTY))
                if (own) {
                    commands.add(SessionCommand(PlaybackCommands.SLEEP_MINUTES, Bundle.EMPTY))
                        .add(SessionCommand(PlaybackCommands.SLEEP_SONGS, Bundle.EMPTY))
                        .add(SessionCommand(PlaybackCommands.SLEEP_CANCEL, Bundle.EMPTY))
                        .add(SessionCommand(PlaybackCommands.PLAY_SOURCE, Bundle.EMPTY))
                        .add(SessionCommand(PlaybackCommands.PLAY_NEXT, Bundle.EMPTY))
                        .add(SessionCommand(PlaybackCommands.ADD_TO_QUEUE, Bundle.EMPTY))
                }
                builder.setAvailableSessionCommands(commands.build())
            }
            if (notification) builder.setMediaButtonPreferences(listOf(shuffleButton(session.player.shuffleModeEnabled)))
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
                PlaybackCommands.TOGGLE_SHUFFLE -> session.player.shuffleModeEnabled = !session.player.shuffleModeEnabled
                PlaybackCommands.PLAY_SOURCE -> {
                    val source = QueueSource.decode(args.getString(PlaybackCommands.EXTRA_SOURCE))
                        ?: return Futures.immediateFuture(SessionResult(SessionError.ERROR_BAD_VALUE))
                    val title = args.getString(PlaybackCommands.EXTRA_SOURCE_TITLE).orEmpty()
                    val start = args.getInt(PlaybackCommands.EXTRA_START, -1).takeIf { it >= 0 }
                    val shuffle = if (args.containsKey(PlaybackCommands.EXTRA_SHUFFLE)) args.getBoolean(PlaybackCommands.EXTRA_SHUFFLE) else null
                    withTracks(args) { queue.play(source, title, it, start, shuffle) }
                }
                PlaybackCommands.PLAY_NEXT -> withTracks(args) { queue.playNext(it) }
                PlaybackCommands.ADD_TO_QUEUE -> withTracks(args) { queue.addToQueue(it) }
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
            queue.restoreShuffle(restored.shuffle)
            queue.restoreContext(restored.context)
            MediaSession.MediaItemsWithStartPosition(
                restored.mediaItems(),
                restored.index,
                restored.positionMs,
            )
        }
    }

    /** 命令参数里的歌曲 id 换成歌曲（按原来的顺序，找不到的跳过）再交给 [block]。 */
    private fun withTracks(args: Bundle, block: (List<TrackEntity>) -> Unit) {
        val ids = args.getLongArray(PlaybackCommands.EXTRA_TRACK_IDS)?.toList().orEmpty()
        queueTouched = true
        scope.launch { block(library.tracksByIds(ids)) }
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
    const val TOGGLE_SHUFFLE = "jeek.shuffle.toggle"
    const val PLAY_SOURCE = "jeek.queue.play_source"
    const val PLAY_NEXT = "jeek.queue.play_next"
    const val ADD_TO_QUEUE = "jeek.queue.add"
    const val EVENT_PLAYBACK_ERROR = "jeek.event.playback_error"

    const val EXTRA_MINUTES = "minutes"
    const val EXTRA_FINISH_SONG = "finish_song"
    const val EXTRA_COUNT = "count"
    const val EXTRA_TITLE = "title"
    const val EXTRA_SLEEP_END = "sleep_end_elapsed"
    const val EXTRA_SLEEP_SONGS = "sleep_songs_left"
    const val EXTRA_SLEEP_FINISH_SONG = "sleep_finish_song"
    const val EXTRA_SOURCE = "source"
    const val EXTRA_SOURCE_TITLE = "source_title"
    const val EXTRA_TRACK_IDS = "track_ids"
    const val EXTRA_START = "start"
    const val EXTRA_SHUFFLE = "shuffle"
}

/** 换歌后等这么久再获取封面和歌词：快速连续切歌时只查最后停下的那首。 */
private const val FETCH_DELAY_MS = 2_000L

/** 同一首歌在这段时间内第二次出错才跳到下一首。 */
private const val ERROR_RETRY_WINDOW_MS = 30_000L
