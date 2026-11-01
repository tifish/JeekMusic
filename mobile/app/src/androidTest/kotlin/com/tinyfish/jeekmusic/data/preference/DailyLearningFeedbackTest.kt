package com.tinyfish.jeekmusic.data.preference

import android.content.Context
import android.content.ContextWrapper
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.tinyfish.jeekmusic.analysis.*
import com.tinyfish.jeekmusic.data.db.*
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.util.UUID

/** Real Android SQLite + production manual-tag service. Isolated verified content-cache fixture; no user media/settings writes. */
class DailyLearningFeedbackTest {
    @Test fun mainTagBusinessCapturesOnlyChangedDimensionsAndAutoWithdrawsAndSyncs() = runBlocking {
        val app=ApplicationProvider.getApplicationContext<Context>()
        val root=File(app.cacheDir,"daily-learning-test-"+UUID.randomUUID()).apply { mkdirs() }
        val context=object: ContextWrapper(app) { override fun getNoBackupFilesDir()=root }
        val db=Room.inMemoryDatabaseBuilder(app,JeekDatabase::class.java).build()
        val feedback=DailyLearningFeedback(context,db)
        try {
            val folder=db.folderDao().insert(LibraryFolderEntity(treeUri="fixture://"+UUID.randomUUID(),treeDocumentId="primary:Fixture",volumeName="external_primary",relativePath="Fixture/",displayName="Fixture",addedAt=0))
            val t=TrackEntity(trackKey="external_primary:Fixture/song.mp3",fileKey="external_primary:Fixture/song.mp3",mediaStoreId=-999999,volumeName="external_primary",folderId=folder,relativePath="Fixture/",fileName="song.mp3",titleTag=null,title="Fixture song",album="Fixture album",albumArtist="Fixture artist",albumKey="fixture",artistRaw="Fixture artist",artistDisplay="Fixture artist",artistInferred=false,trackNo=null,discNo=null,year=null,genre=null,durationMs=180000,mimeType="audio/mpeg",bitrate=null,sampleRate=null,dateModified=42,size=123,hasEmbeddedLyrics=false)
            db.trackDao().insert(listOf(t))
            val signature=PreferenceStore.hash("verified synthetic content fixture")
            val id=PreferenceStore.hash("sha256:$signature:whole")
            feedback.store.setLocal("daily-content:"+t.trackKey,JSONObject().put("signature","42:123").put("id",id).put("content",signature).toString())
            val repo=TagRepository(db,db.tagDao(),SettingsRepository(app),feedback)
            // Simulated paired-PC result, confined to this Room/SQLite fixture. Metadata alone is inert.
            val version=PreferenceStore.hash("isolated simulated active head")
            feedback.store.append(PreferenceStore.DATASET,"active-voice","model",JSONObject().put("dimension","VOICE").put("state","active").put("version",version))
            assertTrue(feedback.personalResults().isEmpty())
            feedback.store.setLocal("desktop-heads",JSONObject().put("generation",feedback.store.generation).put("resultProtocol",1).put("analyzerVersion",6).put("rulesVersion",9).put("heads",org.json.JSONArray().put(JSONObject().put("dimension","VOICE").put("enabled",true).put("version",version).put("answerRevision",feedback.answerRevision("VOICE")))).toString())
            val result=JSONObject().put("signature","42:123").put("heads",JSONObject().put("VOICE",JSONObject().put("version",version).put("value","VOCAL")))
            feedback.store.setLocal("desktop-result:"+t.trackKey,result.toString())
            repo.recomputeAll(); assertEquals(Tag.VOCAL,repo.info(t.trackKey).auto[TagDimension.VOICE]); assertTrue(repo.info(t.trackKey).manual.isEmpty())
            result.put("signature","43:123"); feedback.store.setLocal("desktop-result:"+t.trackKey,result.toString())
            repo.recomputeAll(); assertNull(repo.info(t.trackKey).auto[TagDimension.VOICE])
            result.put("signature","42:123"); feedback.store.setLocal("desktop-result:"+t.trackKey,result.toString())
            repo.setManual(t.trackKey,TagDimension.VOICE,Tag.VOCAL); feedback.capture()
            assertTrue(feedback.personalResults().isEmpty()) // same-dimension new correction invalidates remote use
            assertEquals(1,feedback.store.current(PreferenceStore.DATASET,"answer").size)
            val answer=feedback.store.current(PreferenceStore.DATASET,"answer").single().getJSONObject("payload")
            assertEquals("VOICE",answer.getString("dimension")); assertEquals("track_tag_feedback",answer.getString("basis")); assertEquals("explicit_daily_tag_correction",answer.getString("source"))
            val count=feedback.store.read(PreferenceStore.DATASET,"answer").size; feedback.capture(); assertEquals(count,feedback.store.read(PreferenceStore.DATASET,"answer").size)
            repo.setManual(t.trackKey,TagDimension.GENRE,Tag.CLASSICAL); repo.setManual(t.trackKey,TagDimension.VOICE,Tag.INSTRUMENTAL); feedback.capture()
            assertEquals(2,feedback.store.current(PreferenceStore.DATASET,"answer").size)
            assertFalse(feedback.store.current(PreferenceStore.DATASET,"answer").any { PreferenceStore.dimension(it.getJSONObject("payload")) in listOf("MOOD","CALMNESS") })
            assertEquals(listOf(t.trackKey),db.tagDao().observeManualKeys().first())
            assertEquals(1,db.folderDao().observeWithCounts(null,null,null,null,true).first().single().trackCount)
            repo.clearManual(t.trackKey,TagDimension.VOICE); feedback.capture()
            assertEquals("WITHDRAWN",feedback.store.current(PreferenceStore.DATASET,"answer").single { PreferenceStore.dimension(it.getJSONObject("payload"))=="VOICE" }.getJSONObject("payload").getString("answer"))
            val peerRoot=File(root,"peer").apply { mkdirs() }
            PreferenceStore(object: ContextWrapper(app) { override fun getNoBackupFilesDir()=peerRoot }).use { peer -> peer.accept("phone",feedback.store.page(0)); peer.accept("phone",feedback.store.page(0)); assertEquals(2,peer.current(PreferenceStore.DATASET,"answer").size) }
            repo.clearManual(t.trackKey,TagDimension.GENRE); feedback.capture(); assertTrue(db.tagDao().observeManualKeys().first().isEmpty())
            assertTrue(db.tagDao().reviews(t.trackKey).isNotEmpty()) // Audit history is not the manual filter.
            feedback.store.clearLearning(); feedback.capture(); assertTrue(feedback.store.current(PreferenceStore.DATASET,"answer").isEmpty())
            feedback.store.setLocal("daily-content:"+t.trackKey,JSONObject().put("signature","42:123").put("id",id).put("content",signature).toString())
            repo.setManual(t.trackKey,TagDimension.CALMNESS,Tag.CALM); feedback.capture(); assertEquals(1,feedback.store.current(PreferenceStore.DATASET,"answer").size)
            assertEquals("CALMNESS",PreferenceStore.dimension(feedback.store.current(PreferenceStore.DATASET,"answer").single().getJSONObject("payload")))
            feedback.store.removeLocal("daily-content:"+t.trackKey)
            repo.setManual(t.trackKey,TagDimension.MOOD,Tag.SPORT); feedback.capture()
            assertEquals(Tag.SPORT,repo.info(t.trackKey).tags[TagDimension.MOOD]); assertTrue(feedback.status().getInt("pending")>0)
        } finally {
            feedback.close(); db.close()
            assertTrue(root.canonicalPath.startsWith(app.cacheDir.canonicalPath+File.separator)); root.deleteRecursively()
        }
    }
}
