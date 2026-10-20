package com.tinyfish.jeekmusic.analysis

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.exp
import kotlin.math.sin
import kotlin.random.Random

class FeatureExtractorTest {

    private val sr = FeatureExtractor.SAMPLE_RATE

    /** 节拍器：每拍一个短促的鼓点（衰减的噪声加低频）。 */
    private fun clickTrack(bpm: Double, seconds: Int, amplitude: Float = 0.8f): FloatArray {
        val out = FloatArray(sr * seconds)
        val period = sr * 60 / bpm
        val random = Random(1)
        var beat = 0.0
        while (beat < out.size) {
            val start = beat.toInt()
            for (i in 0 until (sr * 0.08).toInt()) {
                if (start + i >= out.size) break
                val env = exp(-i / (sr * 0.015))
                val kick = sin(2 * PI * 60 * i / sr)
                out[start + i] += (amplitude * env * (0.6 * kick + 0.4 * (random.nextDouble() * 2 - 1))).toFloat()
            }
            beat += period
        }
        return out
    }

    private fun sine(freq: Double, seconds: Int, amplitude: Double) =
        FloatArray(sr * seconds) { (amplitude * sin(2 * PI * freq * it / sr)).toFloat() }

    @Test
    fun `节拍器的 BPM`() {
        for (bpm in listOf(90.0, 128.0, 150.0)) {
            val f = FeatureExtractor.extract(clickTrack(bpm, 30))
            val detected = f.bpm!!
            assertTrue("expected $bpm, got $detected", abs(detected - bpm) < 3)
            assertTrue("confidence ${f.bpmConfidence}", f.bpmConfidence > 1.3)
        }
    }

    @Test
    fun `起音密度接近每秒拍数`() {
        val f = FeatureExtractor.extract(clickTrack(120.0, 30))
        assertTrue("onset rate ${f.onsetRate}", abs(f.onsetRate - 2.0) < 0.5)
    }

    @Test
    fun `正弦波的响度`() {
        // 幅度 0.1 的正弦，均方 0.005 → -0.691 + 10·log10(0.005) ≈ -23.7
        val f = FeatureExtractor.extract(sine(1000.0, 10, 0.1))
        assertEquals(-23.7, f.loudness, 0.5)
        assertTrue(f.loudnessRange < 1)
    }

    @Test
    fun `低频占比和频谱质心`() {
        val low = FeatureExtractor.extract(sine(80.0, 10, 0.3))
        val high = FeatureExtractor.extract(sine(3000.0, 10, 0.3))
        assertTrue("low ratio ${low.lowRatio}", low.lowRatio > 0.9)
        assertTrue("high ratio ${high.lowRatio}", high.lowRatio < 0.05)
        assertTrue(high.centroidHz > low.centroidHz)
        assertEquals(3000.0, high.centroidHz, 300.0)
    }

    @Test
    fun `静音`() {
        val f = FeatureExtractor.extract(FloatArray(sr * 10))
        assertEquals(-70.0, f.loudness, 0.01)
        assertNull(f.bpm)
    }

    @Test
    fun `太短的输入`() {
        assertNull(FeatureExtractor.extract(FloatArray(100)).bpm)
    }
}
