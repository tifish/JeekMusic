package com.tinyfish.jeekmusic.analysis

import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.data.db.TrackEntity
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Opt-in bounded local corpus. No network, playback, library import, metadata or cache writes. */
@RunWith(AndroidJUnit4::class)
class WorkoutNativeTest {
    @Test fun explicitLocalRecordingsUseMediaCodecTfliteAndProductionPulse() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val argument = InstrumentationRegistry.getArguments().getString("workoutCorpus")
        assumeTrue("Explicit local corpus required; no user music is bundled in tests",argument != null)
        val directory = File(argument!!).canonicalFile
        val owned = context.getExternalFilesDir(null)!!.canonicalFile
        require(directory.path.startsWith(owned.path+File.separator))
        val manifest = JSONObject(File(directory,"manifest.json").readText())
        val records = manifest.getJSONArray("records")
        require(records.length() in 1..8)
        val results = JSONArray()
        val failures = mutableListOf<String>()
        val tagger = AudioTagger(context)
        try {
            for (i in 0 until records.length()) {
                val row = records.getJSONObject(i)
                val source = File(directory,row.getString("file")).canonicalFile
                require(source.parentFile == directory && source.isFile)
                val duration = row.getLong("durationMs")
                val track = TrackEntity(trackKey="local-research-$i",fileKey="local-research-$i",mediaStoreId=0,
                    volumeName="external_primary",folderId=0,relativePath="",fileName=source.name,titleTag=null,title="",
                    album=null,albumArtist=null,albumKey=null,artistRaw=null,artistDisplay="",artistInferred=false,
                    trackNo=null,discNo=null,year=null,genre=null,durationMs=duration,mimeType=null,bitrate=null,
                    sampleRate=null,dateModified=0,size=source.length(),hasEmbeddedLyrics=false)
                val started = System.nanoTime()
                val data = TrackAnalyzer.analyze(track,
                    { w,check -> try { PcmDecoder(context).decode(Uri.fromFile(source),listOf(w),checkCancelled=check).also { android.util.Log.i("WorkoutNativeTest","range=$w samples=${it?.size}") } }
                        catch (e: Exception) { android.util.Log.e("WorkoutNativeTest","local decode failed",e); throw e } },
                    { pcm,check -> tagger.analyzeWindows(pcm,check).let { it.first?.let(AudioTagger::scores) to it.second } })
                val evidence = AnalysisEvidence.parse(data.analysisEvidence)!!
                val tags = TagClassifier.classify(data,Sensitivity.LOOSE)
                val result = JSONObject().put("trackId",row.getLong("trackId")).put("automaticMood",tags[TagDimension.MOOD]?.name)
                    .put("evidence",JSONObject(data.analysisEvidence!!)).put("elapsedMs",(System.nanoTime()-started)/1000000)
                results.put(result)
                File(directory,"results.json").writeText(JSONObject().put("records",results).put("abi",android.os.Build.SUPPORTED_ABIS[0]).toString())
                if (!data.complete()) failures += "incomplete track ${row.getLong("trackId")}"
                if (row.getString("expectedAutomaticMood") != tags[TagDimension.MOOD]?.name)
                    failures += "automatic regression (not human accuracy) track ${row.getLong("trackId")}: ${tags[TagDimension.MOOD]}"
                assertEquals(6,evidence.contract)
                android.util.Log.i("WorkoutNativeTest","trackId=${row.getLong("trackId")} automatic=${tags[TagDimension.MOOD]} ms=${result.getLong("elapsedMs")}")
            }
            assertTrue(failures.joinToString("; "),failures.isEmpty())
        } finally { tagger.close() }
    }
}
