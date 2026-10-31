package com.tinyfish.jeekmusic.analysis

import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.PI
import kotlin.math.sin

/** Actual Android MediaCodec + TFLite, controlled PCM; no music-accuracy claims. */
@RunWith(AndroidJUnit4::class)
class AnalysisNativeTest {
    @Test fun longVorbisSeeksReturnCompleteTimestampBoundedIntervals() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        val file = File(context.cacheDir,"analysis-long-tone.ogg")
        instrumentation.context.assets.open(file.name).use { source ->
            file.outputStream().use { source.copyTo(it) }
        }
        try {
            repeat(2) {
                val pcm = PcmDecoder(context).decode(Uri.fromFile(file),
                    listOf(11023L until 21023L,27019L until 37019L))!!
                assertTrue("attempt=$it size=${pcm.size}",pcm.size in 319990..320002)
                assertTrue(pcm.all { it.isFinite() })
                assertTrue(pcm.map { it.toDouble()*it }.average() > .001)
            }
        } finally { file.delete() }
    }

    @Test fun vorbisFreshCodecKeepsConfigurationAndLaterSeeksFlush() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        val file = File(context.cacheDir,"analysis-controlled-vorbis.ogg")
        instrumentation.context.assets.open("native-unicode.ogg").use { source ->
            file.outputStream().use { source.copyTo(it) }
        }
        try {
            repeat(5) {
                // Exercise a fresh compressed decoder and a second seek on that codec.
                val pcm = PcmDecoder(context).decode(Uri.fromFile(file),
                    listOf(25L until 125L,125L until 225L))!!
                assertTrue("attempt=$it size=${pcm.size}",pcm.size in 3190..3202)
                assertTrue(pcm.all { it.isFinite() })
                assertTrue(pcm.map { it.toDouble()*it }.average() > .001)
            }
        } finally { file.delete() }
    }

    @Test fun lowRateMultichannelCueAndRealModel() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        for ((rate, channels) in listOf(8000 to 1, 11025 to 2, 48000 to 6)) {
            val count = rate * 3
            val b = ByteBuffer.allocate(44 + count * channels * 2).order(ByteOrder.LITTLE_ENDIAN)
            b.put("RIFF".toByteArray()); b.putInt(b.capacity() - 8); b.put("WAVEfmt ".toByteArray())
            b.putInt(16); b.putShort(1); b.putShort(channels.toShort()); b.putInt(rate)
            b.putInt(rate * channels * 2); b.putShort((channels * 2).toShort()); b.putShort(16)
            b.put("data".toByteArray()); b.putInt(count * channels * 2)
            for (i in 0 until count) repeat(channels) {
                b.putShort(if (i < rate) 0 else (sin(i * 2 * PI * 440 / rate) * 8192).toInt().toShort())
            }
            val file = File(context.cacheDir, "analysis-controlled-$rate.wav")
            file.writeBytes(b.array())
            try {
                val pcm = PcmDecoder(context).decode(Uri.fromFile(file), listOf(1000L until 2000L))!!
                assertTrue("rate=$rate size=${pcm.size}", pcm.size in 15990..16002)
                assertTrue(pcm.all { it.isFinite() && it >= -1f && it <= 1f })
                val power = pcm.map { it.toDouble() * it }.average()
                assertTrue("CUE window must contain tone, not silent file head: $power", power in .025.. .034)
                val started = System.nanoTime()
                val tagger = AudioTagger(context)
                try {
                    val result = tagger.analyzeWindows(pcm)
                    assertEquals(1, result.second.size)
                    assertTrue(result.second.single().vocal.isFinite())
                    android.util.Log.i("AnalysisNativeTest", "rate=$rate channels=$channels input=${pcm.size} frames=${result.second.size} elapsedMs=${(System.nanoTime()-started)/1_000_000}")
                } finally { tagger.close() }
            } finally { file.delete() }
        }
    }
}
