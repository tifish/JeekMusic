package com.tinyfish.jeekmusic.playback

import android.content.ComponentName
import android.content.Context
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.guava.await
import kotlinx.coroutines.launch
import javax.inject.Inject
import javax.inject.Singleton

data class PlayerUiState(
    val title: String? = null,
    val artist: String? = null,
    val isPlaying: Boolean = false,
    val hasMedia: Boolean = false,
)

/**
 * UI 侧对 MediaController 的封装。连接是懒建立的：第一次调用播放相关方法时才连接，
 * 避免启动时就创建 PlaybackService 和 ExoPlayer（docs/design.md §7.7、§9.3）。
 */
@Singleton
class PlayerConnection @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var controller: Deferred<MediaController>? = null

    private val _state = MutableStateFlow(PlayerUiState())
    val state: StateFlow<PlayerUiState> = _state.asStateFlow()

    fun connect() {
        if (controller != null) return
        controller = scope.async {
            val token = SessionToken(context, ComponentName(context, PlaybackService::class.java))
            MediaController.Builder(context, token).buildAsync().await().also(::attach)
        }
    }

    fun play(items: List<MediaItem>, startIndex: Int) = withController {
        setMediaItems(items, startIndex, /* startPositionMs = */ 0)
        prepare()
        play()
    }

    fun togglePlayPause() = withController {
        if (isPlaying) pause() else play()
    }

    private fun withController(block: MediaController.() -> Unit) {
        connect()
        scope.launch { controller!!.await().block() }
    }

    private fun attach(controller: MediaController) {
        controller.addListener(object : Player.Listener {
            override fun onEvents(player: Player, events: Player.Events) = update(player)
        })
        update(controller)
    }

    private fun update(player: Player) {
        val metadata = player.mediaMetadata
        _state.value = PlayerUiState(
            title = metadata.title?.toString(),
            artist = metadata.artist?.toString(),
            isPlaying = player.isPlaying,
            hasMedia = player.mediaItemCount > 0,
        )
    }
}
