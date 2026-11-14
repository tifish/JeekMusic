package com.tinyfish.jeekmusic.data.sync

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class LanUploadProgressTest {
    @Test fun retiredUploadCannotBeMistakenForAZeroOffsetAcknowledgement() {
        for(state in listOf("expired","aborted","conflict")) {
            val reply=JSONObject().put("state",state).put("offset",0)
            val start=runCatching { LanUploadProgress.start(reply,1024) }.exceptionOrNull()
            val chunk=runCatching { LanUploadProgress.acknowledge(reply,256) }.exceptionOrNull()
            assertTrue(start is LanLocalDeferred);assertTrue(chunk is LanLocalDeferred)
            assertEquals("upload_restart_required",start!!.message);assertEquals("upload_restart_required",chunk!!.message)
        }
    }
    @Test fun invalidOffsetsHaveSpecificFileLevelReasons() {
        val missing=runCatching { LanUploadProgress.start(JSONObject(),1024) }.exceptionOrNull()
        assertTrue(missing is LanLocalDeferred);assertTrue(missing!!.message!!.startsWith("upload_resume_offset_invalid"))
        val mismatch=runCatching { LanUploadProgress.acknowledge(JSONObject().put("offset",0),256) }.exceptionOrNull()
        assertTrue(mismatch is LanLocalDeferred);assertTrue(mismatch!!.message!!.contains("expected=256, received=0"))
    }
    @Test fun liveResumeAndRestartRepliesKeepTheirConfirmedOffsets() {
        assertEquals(768L,LanUploadProgress.start(JSONObject().put("state","uploading").put("offset",768),1024))
        assertEquals(0L,LanUploadProgress.start(JSONObject().put("state","uploading").put("offset",0).put("restarted",true),1024))
        LanUploadProgress.acknowledge(JSONObject().put("offset",1024),1024)
    }
}
