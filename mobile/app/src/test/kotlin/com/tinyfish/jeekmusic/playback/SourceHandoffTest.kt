package com.tinyfish.jeekmusic.playback

import org.junit.Assert.*
import org.junit.Test
import kotlin.random.Random

class SourceHandoffTest {
    @Test fun absentCurrentContinuesAtNewBeginningAndEmptySourceHasNoTarget() {
        assertEquals(SourceHandoff.Plan(emptyList(), listOf(0, 1), true), SourceHandoff.plan(listOf(7, 8), 1, false, false))
        assertEquals(SourceHandoff.Plan(emptyList(), emptyList(), true), SourceHandoff.plan(emptyList(), 1, false, true))
    }

    @Test fun currentInListContinuesFromItsNewPositionWithoutCollapsingDuplicates() {
        assertEquals(SourceHandoff.Plan(listOf(0), listOf(2, 3), false), SourceHandoff.plan(listOf(7, 1, 8, 1), 1, false, false))
    }

    @Test fun manualCurrentDoesNotConsumeAnOccurrenceInSource() {
        assertEquals(SourceHandoff.Plan(emptyList(), listOf(0, 1), true), SourceHandoff.plan(listOf(1, 2), 1, true, false))
    }

    @Test fun shuffleUsesOnlyNewSourceAndKeepsEveryOccurrenceExceptCurrent() {
        for (seed in 0..30) {
            val plan = SourceHandoff.plan(listOf(7, 1, 8, 7), 1, false, true, Random(seed))
            assertTrue(plan.before.isEmpty())
            assertEquals(listOf(0, 2, 3), plan.after.sorted())
            assertFalse(plan.anchor)
            val absent = SourceHandoff.plan(listOf(7, 8, 7), 1, false, true, Random(seed))
            assertEquals(listOf(0, 1, 2), absent.after.sorted())
            assertTrue(absent.anchor)
        }
    }
}
