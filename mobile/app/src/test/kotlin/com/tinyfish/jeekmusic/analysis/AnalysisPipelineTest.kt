package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.data.db.TrackEntity
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.launch
import kotlinx.coroutines.cancelAndJoin
import org.junit.Assert.*
import org.junit.Test

class AnalysisPipelineTest {
    private fun track(duration: Long = 10000) = TrackEntity(trackKey="synthetic", fileKey="synthetic", mediaStoreId=1,
        volumeName="synthetic", folderId=1, relativePath="", fileName="synthetic.wav", titleTag=null, title="Synthetic",
        album=null, albumArtist=null, albumKey=null, artistRaw=null, artistDisplay="", artistInferred=false,
        trackNo=null, discNo=null, year=null, genre=null, durationMs=duration, mimeType="audio/wav", bitrate=null,
        sampleRate=16000, dateModified=1, size=1, hasEmbeddedLyrics=false, startMs=90000, endMs=90000+duration)
    private val score = ModelScores(-5.0, -2.0, .1, .1, 0.0)
    @Test fun retriesReusePcmAndAreBounded() = runTest {
        var decodes = 0
        var models = 0
        val data = TrackAnalyzer.analyze(track(), { w, _ ->
            decodes++; assertEquals(90000L, w.first); assertEquals(99999L, w.last); FloatArray(160000)
        }, { _, _ -> models++; if (models < 3) error("transient"); score to List(10) { score } })
        assertTrue(data.complete()); assertEquals(1, decodes); assertEquals(3, models)
        models = 0
        val failed = TrackAnalyzer.analyze(track(), { _, _ -> FloatArray(160000) }, { _, _ -> models++; error("permanent") })
        assertTrue(failed.failed); assertEquals(3, failed.analysisAttempts); assertEquals(3, models)
        val e = AnalysisEvidence.parse(failed.analysisEvidence)!!
        assertEquals(1, e.segments.single().decodeAttempts)
        assertEquals(3, e.segments.single().modelAttempts)
    }
    @Test fun conflictsBudgetAndShortTrackBehavior() = runTest {
        var n = 0
        val data = TrackAnalyzer.analyze(track(100000), { _, _ -> FloatArray(160000) }, { _, _ ->
            val m = if (++n == 1) score.copy(vocal=1.0) else score
            m to List(10) { m }
        })
        val e = AnalysisEvidence.parse(data.analysisEvidence)!!
        assertEquals(5, e.segments.size)
        assertTrue("voice_segment_conflict" in e.additionalReasons)
        assertEquals(20000L, e.segments.filter { it.additional }.sumOf { it.endMs - it.startMs })
        val short = TrackAnalyzer.analyze(track(500), { _, _ -> FloatArray(8000) }, { _, _ -> error("must not infer") })
        assertFalse(short.complete()); assertTrue(short.failed)
        assertEquals("decode:insufficient_samples", AnalysisEvidence.parse(short.analysisEvidence)!!.segments.single().error)
    }
    @Test fun cancellationIsNotPersistedAsFailure() = runTest {
        var returned = false
        val job = launch {
            TrackAnalyzer.analyze(track(), { _, _ -> error("retry") }, { _, _ -> score to listOf(score) })
            returned = true
        }
        testScheduler.runCurrent()
        job.cancelAndJoin()
        assertFalse(returned)
    }
}
