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
    private fun context(root: File)=object: ContextWrapper(ApplicationProvider.getApplicationContext<Context>()) { override fun getNoBackupFilesDir()=root.apply { mkdirs() } }
    private fun answer(d: String,value: String,role: String="training")=JSONObject().put("dimension",d).put("answer",value).put("basis","whole_recording_familiar")
        .put("contextId",PreferenceStore.context(d)).put("recordingId","a".repeat(64)).put("contentSignature","b".repeat(64)).put("manifestId","batch").put("role",role)
    @Test fun fourDimensionsRetryConcurrencySealingAndDeletion() {
        val app=ApplicationProvider.getApplicationContext<Context>()
        val root=File(app.cacheDir,"preference-ledger-test-"+UUID.randomUUID()).apply { mkdirs() }
        try {
            PreferenceStore(context(File(root,"pc"))).use { a -> PreferenceStore(context(File(root,"phone"))).use { b ->
                a.append("person","person","profile",JSONObject().put("name","Synthetic fixture"))
                for(d in PreferenceStore.dimensions) a.append("person",d,"answer",answer(d,PreferenceStore.labels(d).first))
                val first=a.page(0); b.accept("pc",first); b.accept("pc",first)
                assertEquals(4,b.current("person","answer").size)
                a.append("person","CALMNESS","answer",answer("CALMNESS","WITHDRAWN"))
                b.append("person","CALMNESS","answer",answer("CALMNESS","NON_CALM"))
                a.accept("phone",b.page(0)); b.accept("pc",a.page(0))
                assertEquals(2,a.current("person","answer").count { PreferenceStore.dimension(it.getJSONObject("payload"))=="CALMNESS" })
                assertEquals(1,a.current("person","answer").count { PreferenceStore.dimension(it.getJSONObject("payload"))=="VOICE" })
                a.append("person","CALMNESS","answer",answer("CALMNESS","UNCERTAIN"),true)
                b.accept("pc",a.page(b.cursor("pc")))
                assertEquals(4,b.current("person","answer").size)
                a.append("person","sealed","answer",answer("VOICE","VOCAL","sealed_test"))
                assertEquals(4,a.read("person","answer").groupBy { it.getString("entityId") }.size)
                assertTrue(a.read("person","answer",true).size>a.read("person","answer").size)
                a.append("person","person","profile",JSONObject().put("deleted",true),true)
                b.accept("pc",a.page(b.cursor("pc")))
                assertTrue(b.read("person","answer",true).isEmpty())
                assertTrue(b.current("person","profile").single().getJSONObject("payload").getBoolean("deleted"))
            } }
        } finally { assertTrue(root.canonicalPath.startsWith(app.cacheDir.canonicalPath+File.separator)); root.deleteRecursively() }
    }
    @Test fun legacyMoodDoesNotBecomeCalmnessAndCrossDimensionValuesAreRejected() {
        val e=JSONObject().put("protocol",1).put("eventId","legacy").put("profileId","person").put("entityId","answer").put("deviceId","pc").put("kind","answer").put("revision",1).put("at",1).put("parents",org.json.JSONArray()).put("payload",answer("MOOD","NON_SPORT").apply { remove("dimension") })
        PreferenceStore.validate(e); assertEquals("MOOD",PreferenceStore.dimension(e.getJSONObject("payload")))
        e.getJSONObject("payload").put("dimension","CALMNESS").put("contextId",PreferenceStore.context("CALMNESS"))
        try { PreferenceStore.validate(e); fail("A mood negative must not become a calmness label") } catch(_: IllegalArgumentException) { }
    }
}
