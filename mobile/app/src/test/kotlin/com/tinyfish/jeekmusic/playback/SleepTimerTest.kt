package com.tinyfish.jeekmusic.playback

import androidx.media3.common.Player
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.lang.reflect.Proxy

@OptIn(ExperimentalCoroutinesApi::class)
class SleepTimerTest {
    @Test
    fun enablingDuringFadeRestoresVolumeAndWaitsForNaturalCompletion() = runTest {
        val h = Harness(this)
        h.timer.startMinutes(1, false)
        val deadline = h.status.endElapsedMs
        advanceTimeBy(45_000)
        runCurrent()
        assertEquals(0.5f, h.volume, 0.001f)

        h.timer.setFinishSong(true)
        assertEquals(1f, h.volume, 0f)
        assertEquals(deadline, h.status.endElapsedMs)
        advanceTimeBy(15_000)
        runCurrent()
        assertEquals(1, h.status.songsLeft)
        assertEquals(0, h.pauses)

        h.listener.onMediaItemTransition(null, Player.MEDIA_ITEM_TRANSITION_REASON_SEEK)
        assertEquals(1, h.status.songsLeft)
        h.listener.onMediaItemTransition(null, Player.MEDIA_ITEM_TRANSITION_REASON_AUTO)
        assertEquals(1, h.pauses)
        assertEquals(SleepTimer.Status(), h.status)
    }

    @Test
    fun disablingDuringCountdownFadesAndStopsAtOriginalDeadline() = runTest {
        val h = Harness(this)
        h.timer.startMinutes(1, true)
        val deadline = h.status.endElapsedMs
        advanceTimeBy(45_000)
        h.timer.setFinishSong(false)
        runCurrent()
        assertEquals(deadline, h.status.endElapsedMs)
        assertFalse(h.status.finishSong)
        assertEquals(0.5f, h.volume, 0.001f)

        advanceTimeBy(15_000)
        runCurrent()
        assertEquals(1, h.pauses)
        assertEquals(1f, h.volume, 0f)
        assertEquals(SleepTimer.Status(), h.status)
    }

    @Test
    fun disablingAfterDeadlineStopsImmediately() = runTest {
        val h = Harness(this)
        h.timer.startMinutes(1, true)
        advanceTimeBy(60_000)
        runCurrent()
        assertTrue(h.status.finishSong)
        assertEquals(1, h.status.songsLeft)

        h.timer.setFinishSong(false)
        assertEquals(1, h.pauses)
        assertEquals(SleepTimer.Status(), h.status)
    }

    @Test
    fun repeatedChangesKeepDeadlineAndCancelPreviousCountdown() = runTest {
        val h = Harness(this)
        h.timer.startMinutes(1, false)
        val deadline = h.status.endElapsedMs
        repeat(3) {
            advanceTimeBy(10_000)
            h.timer.setFinishSong(true)
            h.timer.setFinishSong(false)
            assertEquals(deadline, h.status.endElapsedMs)
        }
        h.timer.setFinishSong(true)
        advanceTimeBy(60_000)
        runCurrent()
        assertEquals(0, h.pauses)
        assertEquals(1, h.status.songsLeft)
        h.listener.onMediaItemTransition(null, Player.MEDIA_ITEM_TRANSITION_REASON_REPEAT)
        assertEquals(1, h.pauses)
    }

    @Test
    fun changesDoNotStartAnInactiveTimerOrReplaceSongCount() = runTest {
        val h = Harness(this)
        h.timer.setFinishSong(true)
        assertEquals(SleepTimer.Status(), h.status)
        h.timer.startSongs(3)
        h.timer.setFinishSong(true)
        h.timer.setFinishSong(false)
        assertEquals(SleepTimer.Status(songsLeft = 3), h.status)
        advanceTimeBy(120_000)
        runCurrent()
        assertEquals(0, h.pauses)
    }

    @Test
    fun cancellingAfterChangingModeLeavesNoCountdownRunning() = runTest {
        val h = Harness(this)
        h.timer.startMinutes(1, true)
        h.timer.setFinishSong(false)
        h.timer.cancel()
        advanceTimeBy(120_000)
        runCurrent()
        assertEquals(SleepTimer.Status(), h.status)
        assertEquals(0, h.pauses)
        assertEquals(1f, h.volume, 0f)
    }

    private class Harness(scope: TestScope) {
        var status = SleepTimer.Status()
        var volume = 1f
        var pauses = 0
        lateinit var listener: Player.Listener
        private val player = Proxy.newProxyInstance(Player::class.java.classLoader, arrayOf(Player::class.java)) { _, method, args ->
            when (method.name) {
                "addListener" -> { listener = args!![0] as Player.Listener; null }
                "removeListener" -> null
                "setVolume" -> { volume = args!![0] as Float; null }
                "pause" -> { pauses++; null }
                else -> error("Unexpected player call: ${method.name}")
            }
        } as Player
        val timer = SleepTimer(player, scope.backgroundScope, nowMs = { scope.testScheduler.currentTime + 1 }) { status = it }
    }
}
