package com.tinyfish.jeekmusic.data.sync

import org.json.JSONObject

/** Validate resumable upload replies without aborting unrelated files on a protocol mismatch. */
internal object LanUploadProgress {
    fun restartRequired(reply: JSONObject)=reply.optString("state") in setOf("expired","aborted","conflict")
    fun start(reply: JSONObject,size: Long): Long {
        if(restartRequired(reply))throw LanLocalDeferred("upload_restart_required")
        val offset=reply.optLong("offset",-1)
        if(offset !in 0..size)throw LanLocalDeferred("upload_resume_offset_invalid: offset=$offset, size=$size")
        return offset
    }
    fun acknowledge(reply: JSONObject,expected: Long) {
        if(restartRequired(reply))throw LanLocalDeferred("upload_restart_required")
        val actual=reply.optLong("offset",-1)
        if(actual!=expected)throw LanLocalDeferred("upload_offset_mismatch: expected=$expected, received=$actual")
    }
}
