package com.tinyfish.jeekmusic.playback

import android.os.Bundle
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.Timeline
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.lang.reflect.Modifier
import java.lang.reflect.Proxy

class NextAlwaysPlayerTest {

    @Test fun pendingSourceDefersNextAndPauseOrExplicitSeekCancelsIt() {
        var pending: (() -> Unit)? = null
        val h = Harness(before = { pending = it }, cancel = { pending = null })
        h.sessionPlayer.seekToNextMediaItem()
        assertEquals(1, h.index)
        assertFalse(h.playing)
        pending!!()
        assertEquals(2, h.index)
        assertTrue(h.playing)
        h.sessionPlayer.seekToNextMediaItem()
        h.sessionPlayer.pause()
        assertEquals(null, pending)
        h.sessionPlayer.seekToNextMediaItem()
        h.sessionPlayer.seekTo(500L)
        assertEquals(null, pending)
    }

    @Test fun retiringSourceAnchorBeforeDeferredPlayAllowsPausedNextToPlay() {
        lateinit var h: Harness
        h = Harness(after = { h.items.removeAt(0); h.index-- })
        h.deferPlay = true
        h.sessionPlayer.seekToNextMediaItem()
        assertEquals(1, h.index)
        h.completePlay()
        assertTrue(h.playing)
    }

    private val nextEntries: List<(Player) -> Unit> = listOf({ it.seekToNextMediaItem() }, { it.seekToNext() })
    private val previousEntries: List<(Player) -> Unit> = listOf({ it.seekToPrevious() }, { it.seekToPreviousMediaItem() })

    @Test
    fun appAndSystemSkipEntriesPlayAfterPausedNavigationAndKeepPlaying() {
        for (wasPlaying in listOf(false, true)) {
            for (skip in nextEntries + previousEntries) {
                val h = Harness().apply { playing = wasPlaying }
                skip(h.sessionPlayer)
                assertEquals(if (skip in nextEntries) 2 else 0, h.index)
                assertEquals(0L, h.position)
                assertTrue(h.playing)
                assertEquals(if (wasPlaying) 0 else 1, h.playRequests)
                assertEquals(if (wasPlaying) 0 else 1, h.playCalls)
            }
        }
    }

    @Test
    fun previousKeepsMedia3RestartThresholdIncludingFirstItemAtZero() {
        for (position in listOf(3_001L, 30_000L)) {
            val h = Harness().apply { this.position = position }
            h.sessionPlayer.seekToPrevious()
            assertEquals(1, h.index)
            assertEquals(0L, h.position)
            assertTrue(h.playing)
            assertEquals(listOf("seekToPrevious", "play"), h.calls)
        }
        val h = Harness().apply { index = 0; previousIndex = C.INDEX_UNSET; position = 0 }
        h.sessionPlayer.seekToPrevious()
        assertEquals(0, h.index)
        assertTrue(h.playing)
    }

    @Test
    fun previousMediaItemDoesNotUseRestartThreshold() {
        val h = Harness().apply { position = 30_000L }
        h.sessionPlayer.seekToPreviousMediaItem()
        assertEquals(0, h.index)
        assertTrue(h.playing)
    }

    @Test
    fun nextWrapsAtEndAndReplaysSingleItemFromPausedIdleOrEndedQueue() {
        for (count in listOf(1, 3)) {
            for (state in listOf(Player.STATE_IDLE, Player.STATE_READY, Player.STATE_ENDED)) {
                for (skip in nextEntries) {
                    for (playWhenReady in listOf(false, true)) {
                        val h = Harness(count).apply {
                            index = count - 1; nextIndex = C.INDEX_UNSET; this.state = state; playing = playWhenReady
                        }
                        skip(h.sessionPlayer)
                        assertEquals(0, h.index)
                        assertEquals(0L, h.position)
                        assertTrue(h.playing)
                        val needsPlayRequest = !playWhenReady || state != Player.STATE_READY
                        assertEquals(if (needsPlayRequest) 1 else 0, h.playRequests)
                        assertEquals(if (needsPlayRequest) listOf("seekTo", "play") else listOf("seekTo"), h.calls)
                    }
                }
            }
        }
    }

    @Test
    fun navigationUsesUnderlyingOrderForShuffleAndRepeat() {
        val next = Harness().apply { index = 0; nextIndex = 2 }
        next.sessionPlayer.seekToNextMediaItem()
        assertEquals(2, next.index)
        val previous = Harness().apply { index = 2; previousIndex = 0 }
        previous.sessionPlayer.seekToPrevious()
        assertEquals(0, previous.index)
        val repeat = Harness().apply { nextIndex = index }
        repeat.sessionPlayer.seekToNext()
        assertEquals(1, repeat.index)
        assertTrue(repeat.playing)
    }

    @Test
    fun cueClippingAndQueueArePreservedWhenNavigationRefreshesArtworkMetadata() {
        val h = Harness()
        val original = h.items.toList()
        h.afterSeek = {
            h.items[h.index] = h.items[h.index].buildUpon()
                .setMediaMetadata(MediaMetadata.Builder().setTitle("refreshed artwork metadata").build()).build()
        }
        h.sessionPlayer.seekToNextMediaItem()
        assertTrue(h.playing)
        assertEquals(original.map { it.mediaId }, h.items.map { it.mediaId })
        assertEquals(original.map { it.clippingConfiguration }, h.items.map { it.clippingConfiguration })
        assertEquals(120_000L, h.items[h.index].clippingConfiguration.startPositionMs)
        assertEquals(0L, h.position)
    }

    @Test
    fun changedTargetOrErrorDuringSeekDoesNotStartPlayback() {
        val changed = Harness().apply {
            afterSeek = { items[index] = items[index].buildUpon().setMediaId("replacement").build() }
        }
        changed.sessionPlayer.seekToNext()
        assertEquals(0, changed.playRequests)
        val failed = Harness().apply { afterSeek = { playbackError = testError() } }
        failed.sessionPlayer.seekToNext()
        assertEquals(0, failed.playRequests)
        val ad = Harness().apply { playingAd = true }
        ad.sessionPlayer.seekToNext()
        assertEquals(0, ad.playRequests)
    }

    @Test
    fun emptyQueueMissingPreviousAndUnavailableCommandsNeverPlayOldItem() {
        for (skip in nextEntries + previousEntries) {
            val empty = Harness(0)
            skip(empty.sessionPlayer)
            assertEquals(0, empty.playRequests)
            val unavailable = Harness().apply { commands.clear() }
            skip(unavailable.sessionPlayer)
            assertEquals(0, unavailable.playRequests)
        }
        val noPrevious = Harness().apply { index = 0; previousIndex = C.INDEX_UNSET }
        noPrevious.sessionPlayer.seekToPreviousMediaItem()
        assertEquals(0, noPrevious.playRequests)
        val noWrap = Harness().apply { nextIndex = C.INDEX_UNSET; commands.remove(Player.COMMAND_SEEK_TO_MEDIA_ITEM) }
        noWrap.sessionPlayer.seekToNext()
        assertEquals(0, noWrap.playRequests)
    }

    @Test
    fun ignoredOrThrowingSeekNeverRequestsPlayback() {
        for (skip in nextEntries + previousEntries) {
            val ignored = Harness().apply { ignoreSeek = true }
            skip(ignored.sessionPlayer)
            assertEquals(1, ignored.index)
            assertEquals(0, ignored.playRequests)
            val throwing = Harness().apply { failSeek = true }
            assertThrows(IllegalStateException::class.java) { skip(throwing.sessionPlayer) }
            assertEquals(0, throwing.playRequests)
        }
        val restart = Harness().apply { position = 30_000L; ignoreSeek = true }
        restart.sessionPlayer.seekToPrevious()
        assertEquals(0, restart.playRequests)
    }

    @Test
    fun platformRefusalFocusSuppressionAndErrorsAreNotOverridden() {
        val denied = Harness().apply { allowPlay = false }
        denied.sessionPlayer.seekToNext()
        assertEquals(2, denied.index)
        assertEquals(1, denied.playRequests)
        assertEquals(0, denied.playCalls)
        assertFalse(denied.playing)
        val suppressed = Harness().apply { suppression = Player.PLAYBACK_SUPPRESSION_REASON_TRANSIENT_AUDIO_FOCUS_LOSS }
        suppressed.sessionPlayer.seekToNext()
        assertFalse(suppressed.playing)
        val error = Harness().apply { playbackError = testError() }
        error.sessionPlayer.seekToNext()
        assertEquals(0, error.playRequests)
        val noPlayCommand = Harness().apply { commands.remove(Player.COMMAND_PLAY_PAUSE) }
        noPlayCommand.sessionPlayer.seekToNext()
        assertFalse(noPlayCommand.playing)
    }

    @Test
    fun delayedConnectionCannotUndoPauseStopReleaseOrQueueChanges() {
        val cancellations: List<(Harness) -> Unit> = listOf(
            { it.sessionPlayer.pause() },
            { it.sessionPlayer.stop() },
            { it.sessionPlayer.playWhenReady = false },
            { it.sessionPlayer.release() },
            { it.listeners.toList().forEach { listener -> listener.onTimelineChanged(Timeline.EMPTY, Player.TIMELINE_CHANGE_REASON_PLAYLIST_CHANGED) } },
            { it.listeners.toList().forEach { listener -> listener.onPlayWhenReadyChanged(false, Player.PLAY_WHEN_READY_CHANGE_REASON_AUDIO_BECOMING_NOISY) } },
            { it.suppression = Player.PLAYBACK_SUPPRESSION_REASON_TRANSIENT_AUDIO_FOCUS_LOSS },
        )
        for (cancel in cancellations) {
            val h = Harness().apply { deferPlay = true }
            h.sessionPlayer.seekToNext()
            cancel(h)
            h.completePlay()
            assertEquals(0, h.playCalls)
        }
        val h = Harness().apply { deferPlay = true }
        h.sessionPlayer.seekToNext()
        val oldRequest = h.pendingPlay!!
        h.sessionPlayer.seekToPrevious()
        assertFalse(oldRequest())
        h.completePlay()
        assertTrue(h.playing)
    }

    @Test
    fun ordinarySeekAndUnderlyingAutomaticNavigationDoNotStartPlayback() {
        val h = Harness()
        h.sessionPlayer.seekTo(12_345L)
        assertEquals(12_345L, h.position)
        assertFalse(h.playing)
        h.player.seekToNextMediaItem() // 服务的曲尾/错误恢复持有原播放器，不能触发显式导航语义。
        assertEquals(2, h.index)
        assertFalse(h.playing)
        assertEquals(0, h.playRequests)
    }

    /** 纯 JVM 的 Player 替身：没有 ExoPlayer、Android 服务、设备、音频输出或系统音量。 */
    private class Harness(count: Int = 3, val before: (() -> Unit) -> Unit = { it() }, val cancel: () -> Unit = {}, val after: () -> Unit = {}) {
        val items = MutableList(count) {
            MediaItem.Builder().setMediaId("cue-or-track-$it")
                .setClippingConfiguration(MediaItem.ClippingConfiguration.Builder()
                    .setStartPositionMs(it * 60_000L).setEndPositionMs((it + 1) * 60_000L).build())
                .build()
        }
        var index = if (count > 0) 1.coerceAtMost(count - 1) else C.INDEX_UNSET
        var position = 1_000L
        var nextIndex = if (count > 2) 2 else C.INDEX_UNSET
        var previousIndex = if (count > 1) 0 else C.INDEX_UNSET
        var state = Player.STATE_READY
        var playing = false
        var playingAd = false
        var suppression = Player.PLAYBACK_SUPPRESSION_REASON_NONE
        var playbackError: PlaybackException? = null
        var ignoreSeek = false
        var failSeek = false
        var allowPlay = true
        var deferPlay = false
        var playRequests = 0
        var playCalls = 0
        var pendingPlay: (() -> Boolean)? = null
        var afterSeek: (() -> Unit)? = null
        val calls = mutableListOf<String>()
        val listeners = mutableListOf<Player.Listener>()
        val commands = mutableSetOf(
            Player.COMMAND_SEEK_TO_NEXT, Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM,
            Player.COMMAND_SEEK_TO_PREVIOUS, Player.COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM,
            Player.COMMAND_SEEK_TO_MEDIA_ITEM, Player.COMMAND_PLAY_PAUSE,
        )
        val player = Proxy.newProxyInstance(Player::class.java.classLoader, arrayOf(Player::class.java)) { _, method, args ->
            when (method.name) {
                "addListener" -> { listeners += args!![0] as Player.Listener; null }
                "removeListener" -> { listeners -= args!![0] as Player.Listener; null }
                "getMediaItemCount" -> items.size
                "getCurrentMediaItemIndex" -> index
                "getMediaItemAt" -> items[args!![0] as Int]
                "getCurrentMediaItem" -> items.getOrNull(index)
                "getCurrentPosition" -> position
                "getNextMediaItemIndex" -> nextIndex
                "getPreviousMediaItemIndex" -> previousIndex
                "hasNextMediaItem" -> (nextIndex != C.INDEX_UNSET)
                "hasPreviousMediaItem" -> (previousIndex != C.INDEX_UNSET)
                "getMaxSeekToPreviousPosition" -> 3_000L
                "isCurrentMediaItemLive" -> false
                "isPlayingAd" -> playingAd
                "isCurrentMediaItemSeekable" -> true
                "getPlaybackState" -> state
                "getPlayWhenReady" -> playing
                "getPlaybackSuppressionReason" -> suppression
                "getPlayerError" -> playbackError
                "isCommandAvailable" -> (args!![0] as Int in commands)
                "seekToNext", "seekToNextMediaItem" -> seek(method.name, nextIndex)
                "seekToPreviousMediaItem" -> seek(method.name, previousIndex)
                "seekToPrevious" -> seek(method.name, if (position <= 3_000L && previousIndex != C.INDEX_UNSET) previousIndex else index)
                "seekTo" -> if (args!!.size == 1) seek(method.name, index, args[0] as Long) else seek(method.name, args[0] as Int, args[1] as Long)
                "play" -> { calls += "play"; playCalls++; playing = true; null }
                "prepare" -> { calls += "prepare"; state = Player.STATE_READY; null }
                "pause", "stop" -> { playing = false; null }
                "setPlayWhenReady" -> { playing = args!![0] as Boolean; null }
                "release" -> null
                else -> error("Unexpected player call: ${method.name}")
            }
        } as Player
        val sessionPlayer = NextAlwaysPlayer(player, beforeNavigation = before, cancelNavigation = cancel, afterNavigation = after) { valid ->
            playRequests++
            pendingPlay = valid
            if (!deferPlay) completePlay()
        }

        fun completePlay() {
            if (pendingPlay?.invoke() == true && allowPlay) {
                // 只模拟平台允许/拒绝播放；MediaSession 的 prepare/曲尾恢复流程留待设备验收。
                player.play()
            }
            pendingPlay = null
        }

        private fun seek(name: String, target: Int, targetPosition: Long = 0): Any? {
            calls += name
            check(!failSeek) { "seek failed" }
            if (!ignoreSeek && target in items.indices) { index = target; position = targetPosition }
            afterSeek?.invoke()
            return null
        }
    }

    private companion object {
        fun testError() = object : PlaybackException("failure", null, ERROR_CODE_UNSPECIFIED, Bundle.EMPTY, 0L) {}
    }

    /** 这就是不能用 Kotlin `by` 委托的原因：Java 默认方法不会被转发。 */
    @Test
    fun kotlinDelegationDropsJavaDefaultMethods() {
        val calls = mutableListOf<String>()
        val target = object : Player.Listener {
            override fun onIsPlayingChanged(isPlaying: Boolean) {
                calls += "playing=$isPlaying"
            }
        }
        val byDelegation = object : Player.Listener by target {}
        byDelegation.onIsPlayingChanged(true)
        assertEquals(emptyList<String>(), calls)

        CommandsListener(target) { it }.onIsPlayingChanged(true)
        assertEquals(listOf("playing=true"), calls)
    }

    /** 升级 Media3 后 Player.Listener 如果新增了回调，这里会失败，提醒在 CommandsListener 里补上转发。 */
    @Test
    fun commandsListenerForwardsEveryCallback() {
        val missing = Player.Listener::class.java.methods
            .filter { it.isDefault && !Modifier.isStatic(it.modifiers) }
            .filter { m -> runCatching { CommandsListener::class.java.getDeclaredMethod(m.name, *m.parameterTypes) }.isFailure }
            .map { it.name }
        assertTrue("CommandsListener 没有转发：$missing", missing.isEmpty())
    }

    @Test
    fun equalityFollowsTheWrappedListener() {
        val listener = object : Player.Listener {}
        assertEquals(CommandsListener(listener) { it }, CommandsListener(listener) { it })
    }
}
