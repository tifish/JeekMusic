package com.tinyfish.jeekmusic.data.sync

import android.graphics.Bitmap
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.MainActivity
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.filter.LanguageRepository
import dagger.hilt.android.EntryPointAccessors
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File

/** Real production menu on the emulator; imported cache must not depend on a local task. */
class LanguageCoverageUiTest {
    @Test fun importedResultsRefreshOpenMenuWithoutLocalRecognition()=runBlocking {
        assumeTrue(InstrumentationRegistry.getArguments().getString("languageCoverageUi")=="true")
        check(android.os.Build.HARDWARE in setOf("ranchu","goldfish"))
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        val context=instrumentation.targetContext
        check(context.packageName.endsWith(".debug"))
        val service=EntryPointAccessors.fromApplication(context,LanSyncWorker.Dependencies::class.java).lan()
        val db=LanSyncService::class.java.getDeclaredField("db").apply { isAccessible=true }.get(service) as JeekDatabase
        val repository=LanguageRepository(db)
        val ui=instrumentation.uiAutomation
        fun find(value: String): AccessibilityNodeInfo? {
            fun visit(node: AccessibilityNodeInfo): AccessibilityNodeInfo? {
                if(node.viewIdResourceName==value || node.text?.toString()==value)return node
                for(i in 0 until node.childCount)node.getChild(i)?.let { child -> visit(child)?.let { return it } }
                return null
            }
            return ui.rootInActiveWindow?.let(::visit)
        }
        suspend fun tap(value: String) {
            withTimeout(12_000) {
                while(true) {
                    var node=find(value)
                    while(node?.isClickable==false)node=node.parent
                    if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK))break
                    delay(50)
                }
            }
            delay(300)
        }
        suspend fun revealCoverage() {
            repeat(10) {
                if(find("language_coverage")?.isVisibleToUser==true)return
                ui.executeShellCommand("input swipe 520 1500 520 650 300").close();delay(400)
            }
            error("Language coverage not visible")
        }
        fun shot(name: String) {
            val dir=File(context.cacheDir,"language-coverage-ui").apply { mkdirs() }
            val bitmap=ui.takeScreenshot() ?: error("Screenshot unavailable")
            File(dir,"$name.png").outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG,100,it) };bitmap.recycle()
        }
        ActivityScenario.launch(MainActivity::class.java).use {
            delay(3500)
            val track=db.trackDao().all().first { repository.result(it)==null }
            val oldRows=db.languageDao().forTrack(track.trackKey)
            try {
                val before=repository.coverage.first()
                tap("top_filter");revealCoverage()
                withTimeout(5000) {
                    while(find("language_coverage")?.text?.toString()!=context.getString(R.string.language_coverage,before.cached,before.total,before.pending))delay(50)
                }
                shot("before-import")
                val row=JSONObject().put("source","AUTO").put("state","known").put("languages",JSONArray(listOf("en")))
                    .put("confidence",.95).put("updated_at",System.currentTimeMillis()).put("size",track.size)
                    .put("duration_ms",track.durationMs).put("start_ms",track.startMs ?: 0)
                LanguageExchange(db).accept(JSONObject().put("version",1).put("items",JSONObject().put(track.trackKey,JSONArray().put(row)))) { it }
                val after=repository.coverage.first { coverage -> coverage.cached==before.cached+1 }
                withTimeout(5000) {
                    while(find("language_coverage")?.text?.toString()!=context.getString(R.string.language_coverage,after.cached,after.total,after.pending))delay(50)
                }
                assertNull(find(context.getString(R.string.language_idle)))
                shot("after-import")
                println("LANGUAGE_COVERAGE_UI before=${before.cached}/${before.total} after=${after.cached}/${after.total} localRecognition=false openMenuRefreshed=true")
            } finally {
                db.openHelper.writableDatabase.execSQL("DELETE FROM track_language WHERE track_key=?",arrayOf(track.trackKey))
                oldRows.forEach { db.languageDao().save(it) }
            }
        }
    }
}
