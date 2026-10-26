package com.tinyfish.jeekmusic.playback

import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.data.db.TrackEntity
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableSharedFlow
import org.junit.Assert.*
import org.junit.Test

/** Real ExoPlayer timeline, on its Android main looper; no user media or audio output. */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
@org.junit.runner.RunWith(androidx.test.ext.junit.runners.AndroidJUnit4::class)
class QueueSourceHandoffTest {
    private val a = QueueSource.Artist("a")
    private val b = QueueSource.Folder(1, "B/", false)
    private val c = QueueSource.Album("c")

    @Test fun restoredDeletedAnchorDoesNotExcludeFirstRealSourceSong() = runBlocking {
        withContext(Dispatchers.Main) { Harness().use { h ->
            h.start(listOf(7, 8), index = 1)
            h.queue.restoreContext(QueueContext(b, "B", anchorTrackId = 1))
            assertNull(h.queue.nextTarget())
            h.next()
            assertEquals(7L, h.current())
        } }
    }

    @Test fun naturalEofDuringPendingReadRequestsContinuationWhenNewSourceArrives() = runBlocking {
        withContext(Dispatchers.Main) { Harness().use { h ->
            val instrumentation = InstrumentationRegistry.getInstrumentation()
            val clip = java.io.File(instrumentation.targetContext.cacheDir, "source-handoff-tone.mp3")
            instrumentation.context.assets.open("tag-writer-tone.mp3").use { input -> clip.outputStream().use { input.copyTo(it) } }
            try {
                h.player.setMediaItem(androidx.media3.common.MediaItem.Builder().setMediaId("track:1").setUri(android.net.Uri.fromFile(clip)).build())
                h.queue.follow(b, "B")
                h.player.prepare()
                h.player.play()
                h.until { h.player.playbackState == Player.STATE_ENDED }
                assertEquals(0, h.ended)
                h.emit(b, listOf(7, 8))
                assertEquals(1, h.ended)
                assertEquals(listOf(1L, 7L, 8L), h.ids())
            } finally { h.player.stop(); clip.delete() }
        } }
    }

    @Test fun shuffleCrossSourceKeepsCurrentPositionAndManualPriority() = runBlocking {
        withContext(Dispatchers.Main) { Harness().use { h ->
            h.start(listOf(1, 2, 3), index = 1, shuffle = true)
            h.queue.playNext(listOf(song(90)))
            val current = h.player.currentMediaItem
            h.queue.follow(b, "B")
            assertEquals(listOf(2L, 90L), h.ids()) // old preload removed immediately
            var navigated = false
            h.queue.navigate { navigated = true; h.player.seekToNextMediaItem() }
            assertFalse(navigated)
            h.emit(b, listOf(7, 8, 7))
            assertTrue(navigated)
            assertEquals(90L, h.current())
            h.until { h.queue.context?.anchorTrackId == null }
            assertEquals(listOf(7L, 7L, 8L), h.ids().drop(1).sorted())
            assertTrue(h.player.shuffleModeEnabled)
            assertFalse(h.player.playWhenReady)
            assertNotNull(current)
        } }
    }

    @Test fun orderedCurrentInNewListKeepsExactItemAndPositionThenContinues() = runBlocking {
        withContext(Dispatchers.Main) { Harness().use { h ->
            h.start(listOf(1, 2, 3), index = 1)
            val current = h.player.currentMediaItem
            h.queue.follow(b, "B")
            h.emit(b, listOf(7, 2, 8, 2))
            assertEquals(listOf(7L, 2L, 8L, 2L), h.ids())
            assertEquals(current, h.player.currentMediaItem)
            assertEquals(24_340L, h.player.currentPosition)
            assertFalse(h.player.playWhenReady)
            h.player.seekToNextMediaItem()
            assertEquals(8L, h.current())
        } }
    }

    @Test fun oldAnchorCannotLoopAndEmptySourceNextDoesNotRestartIt() = runBlocking {
        withContext(Dispatchers.Main) { Harness().use { h ->
            h.start(listOf(1, 2), index = 0)
            h.player.repeatMode = Player.REPEAT_MODE_ALL
            h.queue.follow(b, "B")
            h.emit(b, listOf(7, 8))
            assertEquals(24_340L, h.player.currentPosition)
            h.next()
            h.until { h.queue.context?.anchorTrackId == null }
            assertEquals(listOf(7L, 8L), h.ids())
            repeat(9) { h.next(); assertTrue(h.current() in listOf(7L, 8L)) }
            val current = h.current()
            h.player.seekTo(12_345L)
            h.queue.follow(c, "Empty")
            h.queue.navigate { h.next() }
            h.emit(c, emptyList())
            assertEquals(current, h.current())
            assertEquals(12_345L, h.player.currentPosition)
        } }
    }

    @Test fun latestNavigationAndExplicitPlayInvalidateLateResults() = runBlocking {
        withContext(Dispatchers.Main) { Harness().use { h ->
            h.start(listOf(1, 2), index = 0)
            h.queue.follow(b, "B")
            h.queue.follow(c, "C")
            h.flows.getValue(b).emit(listOf(song(7)))
            h.emit(c, listOf(8, 9))
            assertEquals(listOf(1L, 8L, 9L), h.ids())
            h.queue.follow(b, "B")
            h.queue.play(a, "A", listOf(song(10), song(11)), 0, false)
            // Stop preparation of synthetic URIs; timeline ownership is the subject here.
            h.player.stop()
            h.flows.getValue(b).emit(listOf(song(7)))
            repeat(5) { yield() }
            assertEquals(listOf(10L, 11L), h.ids())
            assertEquals(a, h.queue.context?.source)
            val old = h.queue.beginPlay()
            h.queue.follow(c, "C")
            assertFalse(h.queue.isCurrentRequest(old))
        } }
    }

    @Test fun cancelledPendingNextDoesNotResumeAndRestoredPendingSourceFinishes() = runBlocking {
        withContext(Dispatchers.Main) { Harness().use { h ->
            h.start(listOf(1, 2), index = 0)
            h.queue.follow(b, "B")
            var navigated = false
            h.queue.navigate { navigated = true }
            h.queue.cancelNavigation()
            h.emit(b, listOf(7, 8))
            assertFalse(navigated)
            assertEquals(1L, h.current())
            assertEquals(24_340L, h.player.currentPosition)
            h.queue.restoreContext(QueueContext(c, "C", anchorTrackId = 1, pending = true))
            h.emit(c, listOf(9))
            assertEquals(listOf(1L, 9L), h.ids())
            assertFalse(h.queue.context!!.pending)
        } }
    }

    private inner class Harness : AutoCloseable {
        val player = ExoPlayer.Builder(InstrumentationRegistry.getInstrumentation().targetContext).build().apply { volume = 0f }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        val flows = listOf(a, b, c).associateWith { MutableSharedFlow<List<TrackEntity>>(replay = 1) }
        var ended = 0
        val queue = QueueController(player, scope, { flows.getValue(it) }, onEndedWhileLoading = { ended++ }) {}
        fun start(ids: List<Int>, index: Int, shuffle: Boolean = false) {
            player.setMediaItems(ids.map { MediaItemMapper.toMediaItem(song(it)) }, index, 24_340)
            queue.restoreShuffle(shuffle)
        }
        fun ids() = List(player.mediaItemCount) { MediaItemMapper.trackId(player.getMediaItemAt(it).mediaId)!! }
        fun current() = MediaItemMapper.trackId(player.currentMediaItem!!.mediaId)!!
        suspend fun emit(source: QueueSource, ids: List<Int>) {
            flows.getValue(source).emit(ids.map(::song))
            until { queue.context?.pending == false }
        }
        suspend fun until(done: () -> Boolean) = withTimeout(5_000) { while (!done()) delay(10) }
        fun next() {
            val target = queue.nextTarget()
            if (target != null) { if (target >= 0) player.seekTo(target, 0) }
            else if (player.hasNextMediaItem()) player.seekToNextMediaItem() else player.seekTo(0, 0)
        }
        override fun close() { scope.cancel(); player.release() }
    }

    private fun song(id: Int) = TrackEntity(
        id = id.toLong(), trackKey = "test:$id", fileKey = "test:$id", mediaStoreId = id.toLong(), volumeName = "external", folderId = 1,
        relativePath = "Synthetic/", fileName = "$id.wav", titleTag = null, title = "Song $id", album = null, albumArtist = null,
        albumKey = null, artistRaw = null, artistDisplay = "Test", artistInferred = false, trackNo = null, discNo = null,
        year = null, genre = null, durationMs = 60_000, mimeType = "audio/wav", bitrate = null, sampleRate = null,
        dateModified = 1, size = 1, hasEmbeddedLyrics = false,
    )
}
