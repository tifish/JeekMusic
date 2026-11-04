package com.tinyfish.jeekmusic.data.sync

import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Test
import org.junit.Assume.assumeTrue
import org.json.JSONObject

/** Explicit emulator-only visual fixture, never run against the user's phone. */
class LanConflictUiFixtureTest {
    @Test fun prepareOrRemoveSyntheticComparisonRows() {
        val args=InstrumentationRegistry.getArguments()
        assumeTrue(args.getString("jeekConflictUiFixture")=="true")
        check(android.os.Build.HARDWARE in setOf("ranchu", "goldfish"))
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        check(context.packageName.endsWith(".debug"))
        LanFileDatabase(context).use { db ->
            for(i in 1..2) {
                val path="Conflict demo $i.mp3";val mapping="visual-conflict-fixture"
                db.resolved(mapping,path)
                if(args.getString("cleanup")=="true")continue
                val row=JSONObject().put("id",i.toString().repeat(64)).put("mapping",mapping).put("path",path)
                    .put("localVersion",if(i==1)"m2:10485760:1793799853318915800" else JSONObject.NULL)
                    .put("remoteVersion","m2:8388608:1793799853188764700")
                    .put("localChange",if(i==1)"modified" else "deleted").put("remoteChange","modified")
                    .put("reason",if(i==1)"both_modified" else "modified_vs_deleted")
                if(i==2)row.put("choice","remote")
                db.issue(row)
            }
        }
    }
}
