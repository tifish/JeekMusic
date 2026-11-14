package com.tinyfish.jeekmusic.analysis

import android.net.Uri
import android.os.SystemClock
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Explicit large local fixtures provisioned by tools/test-feature-parity.py, never bundled in APK. */
@RunWith(AndroidJUnit4::class)
class NativeLanguageDeviceTest {
    @Test fun realCpuModelRecognizesDiagnosticSpeechAndCancelsInference() = runBlocking {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val folder=File(context.getExternalFilesDir(null),"parity-fixtures")
        val model=File(folder,"ggml-tiny.bin")
        assertEquals(LanguageAnalysisService.MODEL_BYTES,model.length())
        val decoder=PcmDecoder(context)
        val tagger=AudioTagger(context)
        try {
            NativeWhisper(model.absolutePath).use { engine ->
                for ((file,expected) in listOf("english-speech.wav" to "en","mandarin-speech.wav" to "zh")) {
                    val pcm=decoder.decode(Uri.fromFile(File(folder,file)),listOf(0L until 20_000L))!!
                    val scores=tagger.analyzeWindows(pcm).first!!.let(AudioTagger::scores)
                    assertTrue(scores.vocal>=TagClassifier.VOCAL_THRESHOLD)
                    val (language,probability,speech)=engine.recognize(pcm,SystemClock.elapsedRealtime()+120_000)
                    assertEquals(expected,language); assertTrue(probability>=.6); assertTrue(speech)
                }
            }
            val pcm=decoder.decode(Uri.fromFile(File(folder,"english-speech.wav")),listOf(0L until 20_000L))!!
            NativeWhisper(model.absolutePath).use { engine ->
                val started=SystemClock.elapsedRealtime()
                val inference=async(Dispatchers.IO) {
                    try { engine.recognize(pcm,SystemClock.elapsedRealtime()+120_000); false }
                    catch (_: IllegalStateException) { true }
                }
                delay(100); engine.cancel()
                assertTrue(inference.await())
                assertTrue(SystemClock.elapsedRealtime()-started<15_000)
            }
        } finally { tagger.close() }
    }
}
