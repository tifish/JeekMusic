package com.tinyfish.jeekmusic.data.sync

import com.tinyfish.jeekmusic.SharedCases
import org.junit.Assert.*
import org.junit.Test
import org.json.JSONObject

class FileReconciliationTest {
    @Test fun exactMetadataAndLandingPrecisionMatrix() {
        val cases=SharedCases.load("file-reconciliation-v2.json").getJSONArray("cases")
        for(i in 0 until cases.length()) {
            val c=cases.getJSONObject(i)
            fun v(key: String)=if(c.isNull(key))null else c.getString(key)
            val b=c.optJSONArray("base")
            val base=b?.let { FileReconciliation.Baseline(if(it.isNull(0))null else it.getString(0),"v",if(it.isNull(1))null else it.getString(1),true,c.getBoolean("migration")) }
            val result=FileReconciliation.decide(base,FileReconciliation.State(c.getBoolean("known"),v("local")),FileReconciliation.State(true,v("remote")))
            assertEquals(v("name"),v("action"),result.action);assertEquals(v("name"),v("reason"),result.reason)
        }
    }
    @Test fun sharedPresenceContentAndUnknownMatrix() {
        val cases=SharedCases.load("file-reconciliation-v1.json").getJSONArray("cases")
        for(i in 0 until cases.length()) {
            val c=cases.getJSONObject(i)
            fun hash(o: JSONObject,k: String)=if(o.isNull(k)) null else o.getString(k)
            val base=if(c.getBoolean("tracked")) FileReconciliation.Baseline(hash(c,"base"),"confirmed") else null
            val local=c.getJSONObject("local");val remote=c.getJSONObject("remote")
            val result=FileReconciliation.decide(base,FileReconciliation.State(local.getBoolean("known"),hash(local,"hash")),FileReconciliation.State(remote.getBoolean("known"),hash(remote,"hash")))
            assertEquals(c.getString("name"),c.getString("action"),result.action);assertEquals(c.getString("name"),hash(c,"reason"),result.reason)
        }
    }
}
