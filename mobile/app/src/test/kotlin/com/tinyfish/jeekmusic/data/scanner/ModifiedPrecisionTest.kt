package com.tinyfish.jeekmusic.data.scanner

import org.junit.Assert.*
import org.junit.Test

class ModifiedPrecisionTest {
    @Test fun rejectStaleMediaRowsAndInvalidFractions() {
        val stat = longArrayOf(1, 2, 100, 1793099744, 318915800)
        assertEquals(318915800, MediaStoreSource.matchingModifiedNanos(1793099744, 100, stat))
        assertNull(MediaStoreSource.matchingModifiedNanos(1793099743, 100, stat))
        assertNull(MediaStoreSource.matchingModifiedNanos(1793099744, 101, stat))
        for (n in listOf(-1L, 1000000000L)) {
            assertNull(MediaStoreSource.matchingModifiedNanos(1793099744, 100, stat.copyOf().apply { this[4] = n }))
        }
    }
}
