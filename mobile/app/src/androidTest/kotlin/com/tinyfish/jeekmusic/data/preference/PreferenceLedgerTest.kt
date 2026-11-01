package com.tinyfish.jeekmusic.data.preference

import android.content.Context
import android.content.ContextWrapper
import androidx.test.core.app.ApplicationProvider
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.util.UUID

/** Actual Android SQLite, isolated test-owned paths. Never opens the user's preference DB. */
class PreferenceLedgerTest {
    @Test fun resultPagesResumeAcrossBatchesAndRejectChangedOrClearedSnapshots() {
        val app=ApplicationProvider.getApplicationContext<Context>(); val root=File(app.cacheDir,"result-cursor-"+UUID.randomUUID()).apply { mkdirs() }
        try {
            var generation=""; val version=PreferenceStore.hash("isolated result frame")
            PreferenceStore(context(root)).use { store ->
                generation=store.generation
                DesktopResultCursor(store).commit(JSONObject().put("generation",generation).put("version",version).put("next",10000).put("hasMore",true))
            }
            PreferenceStore(context(root)).use { store ->
                val cursor=DesktopResultCursor(store); assertEquals(10000,cursor.offset); assertEquals(version,cursor.version)
                assertThrows(IllegalArgumentException::class.java) { cursor.commit(JSONObject().put("generation",generation).put("version",PreferenceStore.hash("changed")).put("next",10100).put("hasMore",true)) }
                assertEquals(10000,DesktopResultCursor(store).offset)
                cursor.commit(JSONObject().put("generation",generation).put("version",version).put("next",10001).put("hasMore",false)); assertEquals(0,DesktopResultCursor(store).offset)
                cursor.commit(JSONObject().put("generation",generation).put("version",version).put("next",100).put("hasMore",true)); store.clearLearning()
                assertThrows(IllegalArgumentException::class.java) { cursor.commit(JSONObject().put("generation",generation).put("version",version).put("next",200).put("hasMore",true)) }
                assertEquals(0,DesktopResultCursor(store).offset)
            }
        } finally { assertTrue(root.canonicalPath.startsWith(app.cacheDir.canonicalPath+File.separator)); root.deleteRecursively() }
    }
    private fun context(root: File)=object: ContextWrapper(ApplicationProvider.getApplicationContext<Context>()) { override fun getNoBackupFilesDir()=root.apply { mkdirs() } }
    private fun answer(d: String,value: String,role: String="training")=JSONObject().put("dimension",d).put("answer",value).put("basis","whole_recording_familiar")
        .put("contextId",PreferenceStore.context(d)).put("recordingId","a".repeat(64)).put("contentSignature","b".repeat(64)).put("manifestId","batch").put("role",role)
    @Test fun fourDimensionsRetryConcurrencySealingAndDeletion() {
        val app=ApplicationProvider.getApplicationContext<Context>()
        val root=File(app.cacheDir,"preference-ledger-test-"+UUID.randomUUID()).apply { mkdirs() }
        try {
            PreferenceStore(context(File(root,"pc"))).use { a -> PreferenceStore(context(File(root,"phone"))).use { b ->
                for(d in PreferenceStore.dimensions) a.append(PreferenceStore.DATASET,d,"answer",answer(d,PreferenceStore.labels(d).first))
                val first=a.page(0); b.accept("pc",first); b.accept("pc",first)
                assertEquals(4,b.current(PreferenceStore.DATASET,"answer").size)
                a.append(PreferenceStore.DATASET,"CALMNESS","answer",answer("CALMNESS","WITHDRAWN"))
                b.append(PreferenceStore.DATASET,"CALMNESS","answer",answer("CALMNESS","NON_CALM"))
                a.accept("phone",b.page(0)); b.accept("pc",a.page(0))
                assertEquals(2,a.current(PreferenceStore.DATASET,"answer").count { PreferenceStore.dimension(it.getJSONObject("payload"))=="CALMNESS" })
                assertEquals(1,a.current(PreferenceStore.DATASET,"answer").count { PreferenceStore.dimension(it.getJSONObject("payload"))=="VOICE" })
                a.append(PreferenceStore.DATASET,"CALMNESS","answer",answer("CALMNESS","UNCERTAIN"),true)
                b.accept("pc",a.page(b.cursor("pc")))
                assertEquals(4,b.current(PreferenceStore.DATASET,"answer").size)
                a.append(PreferenceStore.DATASET,"sealed","answer",answer("VOICE","VOCAL","sealed_test"))
                assertEquals(4,a.read(PreferenceStore.DATASET,"answer").groupBy { it.getString("entityId") }.size)
                assertTrue(a.read(PreferenceStore.DATASET,"answer",true).size>a.read(PreferenceStore.DATASET,"answer").size)
                a.clearLearning()
                b.accept("pc",a.page(b.cursor("pc")))
                assertTrue(b.read(PreferenceStore.DATASET,"answer",true).isEmpty())
                assertEquals(a.generation,b.generation)
                a.append(PreferenceStore.DATASET,"fresh","answer",answer("VOICE","VOCAL")); b.accept("pc",a.page(b.cursor("pc")))
                assertEquals(1,b.current(PreferenceStore.DATASET,"answer").size)
            } }
        } finally { assertTrue(root.canonicalPath.startsWith(app.cacheDir.canonicalPath+File.separator)); root.deleteRecursively() }
    }
    @Test fun independentInitializationConcurrentClearReplayAndReuse() {
        val app=ApplicationProvider.getApplicationContext<Context>()
        val root=File(app.cacheDir,"preference-generation-"+UUID.randomUUID()).apply { mkdirs() }
        val p=PreferenceStore.DATASET
        try { PreferenceStore(context(File(root,"a"))).use { a -> PreferenceStore(context(File(root,"b"))).use { b ->
            assertEquals(a.generation,b.generation)
            a.append(p,"answer","answer",answer("VOICE","VOCAL")); b.append(p,"answer","answer",answer("VOICE","INSTRUMENTAL"))
            a.accept("b",b.page(0)); b.accept("a",a.page(0)); assertEquals(2,a.current(p,"answer").size)
            val old=b.page(0); a.clearLearning(); b.clearLearning()
            a.append(p,"post-a","answer",answer("VOICE","VOCAL")); b.append(p,"post-b","answer",answer("VOICE","VOCAL"))
            val pa=a.page(0); val pb=b.page(0); a.accept("b",pb); b.accept("a",pa)
            assertEquals(a.generation,b.generation); assertTrue(a.read(p,evaluation=true).isEmpty()); assertTrue(b.read(p,evaluation=true).isEmpty())
            a.accept("stale",old); assertTrue(a.read(p,evaluation=true).isEmpty())
            a.append(p,"fresh","answer",answer("VOICE","VOCAL")); b.accept("a",a.page(b.cursor("a"))); assertEquals(1,b.current(p,"answer").size)
            try { b.accept("old",JSONObject(old.toString()).put("protocol",2)); fail("Must require upgrade") } catch(_: IllegalArgumentException) { }
            assertEquals(0,b.cursor("old")); assertEquals(1,b.current(p,"answer").size)
        } } } finally { assertTrue(root.canonicalPath.startsWith(app.cacheDir.canonicalPath+File.separator)); root.deleteRecursively() }
    }
    @Test fun upgradeIsLearningOnlyOnceAndPreservesNewDataAcrossRestart() {
        val app=ApplicationProvider.getApplicationContext<Context>()
        val root=File(app.cacheDir,"preference-upgrade-"+UUID.randomUUID()).apply { mkdirs() }
        val sentinel=File(root,"ordinary-library-pairing-settings").apply { writeText("library playlists daily tags reviews analysis progress pairing mappings permissions settings") }
        val before=sentinel.readBytes(); val path=File(root,"preferences.db")
        try {
            android.database.sqlite.SQLiteDatabase.openOrCreateDatabase(path,null).use { db ->
                db.execSQL("ATTACH DATABASE ? AS evaluation",arrayOf(path.absolutePath+".evaluation"))
                for(schema in listOf("main","evaluation")) db.execSQL("CREATE TABLE $schema.events(id TEXT PRIMARY KEY,profile TEXT,entity TEXT,kind TEXT,json TEXT,dimension TEXT)")
                db.execSQL("CREATE TABLE journal(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE,sealed INTEGER)")
                db.execSQL("CREATE TABLE local(key TEXT PRIMARY KEY,value TEXT)"); db.execSQL("CREATE TABLE peers(id TEXT PRIMARY KEY,cursor INTEGER)")
                db.execSQL("INSERT INTO events VALUES('old','old','answer','answer','private old body','VOICE')")
                db.execSQL("INSERT INTO evaluation.events VALUES('sealed','old','sealed','answer','private sealed body','VOICE')")
                db.execSQL("INSERT INTO journal(id,sealed) VALUES('old',0)"); db.execSQL("INSERT INTO local VALUES('deviceId','stable-device')")
                db.execSQL("INSERT INTO local VALUES('old-state','old cache')"); db.execSQL("INSERT INTO peers VALUES('pc',100)"); db.version=2
            }
            var generation=""
            PreferenceStore(context(root)).use { store ->
                assertEquals("stable-device",store.deviceId); assertTrue(store.read(PreferenceStore.DATASET,evaluation=true).isEmpty())
                assertNull(store.local("old-state")); assertEquals(0,store.cursor("pc")); assertEquals(1,store.page(0).getLong("nextCursor"))
                generation=store.generation; store.append(PreferenceStore.DATASET,"fresh","answer",answer("CALMNESS","CALM"))
            }
            PreferenceStore(context(root)).use { store -> assertEquals(generation,store.generation); assertEquals(1,store.current(PreferenceStore.DATASET,"answer").size) }
            assertArrayEquals(before,sentinel.readBytes())
            assertFalse(path.readText(Charsets.ISO_8859_1).contains("private old body"))
            assertFalse(File(path.absolutePath+".evaluation").readText(Charsets.ISO_8859_1).contains("private sealed body"))
        } finally { assertTrue(root.canonicalPath.startsWith(app.cacheDir.canonicalPath+File.separator)); root.deleteRecursively() }
    }
    @Test fun legacyMoodDoesNotBecomeCalmnessAndCrossDimensionValuesAreRejected() {
        val e=JSONObject().put("protocol",1).put("eventId","legacy").put("profileId",PreferenceStore.DATASET).put("entityId","answer").put("deviceId","pc").put("kind","answer").put("revision",1).put("at",1).put("parents",org.json.JSONArray()).put("payload",answer("MOOD","NON_SPORT").apply { remove("dimension") })
        try { PreferenceStore.validate(e); fail("Old protocol must require upgrade") } catch(_: IllegalArgumentException) { }
        e.put("protocol",PreferenceStore.VERSION).put("generation","a".repeat(64)); PreferenceStore.validate(e); assertEquals("MOOD",PreferenceStore.dimension(e.getJSONObject("payload")))
        e.getJSONObject("payload").put("dimension","CALMNESS").put("contextId",PreferenceStore.context("CALMNESS"))
        try { PreferenceStore.validate(e); fail("A mood negative must not become a calmness label") } catch(_: IllegalArgumentException) { }
    }
}
