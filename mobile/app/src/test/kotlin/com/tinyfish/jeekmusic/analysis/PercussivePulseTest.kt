package com.tinyfish.jeekmusic.analysis

import org.junit.Assert.*
import org.junit.Test
import kotlin.math.*
import java.util.concurrent.CancellationException

class PercussivePulseTest {
    private fun percussion(bpm: Int): FloatArray {
        var state = 12345
        return FloatArray(16000*6) { i ->
            state = state xor (state shl 13); state = state xor (state ushr 17); state = state xor (state shl 5)
            val t = i/16000.0
            val phase = (t+.1)%(60.0/bpm)
            val noise = ((state.toLong() and 0xffffffffL)/4294967295.0*2-1)*.4*exp(-phase/.025)
            val bass = sin(2*PI*80*phase)*.6*exp(-phase/.07)
            (noise+bass).toFloat()
        }
    }
    @Test fun emptyInterbeatsDoNotDoubleSlowPercussion() {
        for (bpm in listOf(80,120)) {
            val result = PercussivePulseExtractor.fromModelPcm(percussion(bpm))
            assertTrue(abs(result.broadband[0].bpm-bpm) <= 1)
            assertEquals(bpm == 120,result.supportsWorkout)
        }
    }
    @Test fun silenceBoundsAndCancellation() {
        val result = PercussivePulseExtractor.fromModelPcm(FloatArray(16000*4))
        assertTrue(result.bass.isEmpty() && result.broadband.isEmpty())
        assertThrows(CancellationException::class.java) {
            PercussivePulseExtractor.fromModelPcm(FloatArray(16000*10)) { throw CancellationException() }
        }
        assertThrows(IllegalArgumentException::class.java) { PercussivePulseExtractor.fromModelPcm(FloatArray(16000*32)) }
    }
}
