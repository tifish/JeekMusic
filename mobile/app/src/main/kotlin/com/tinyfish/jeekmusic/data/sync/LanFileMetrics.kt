package com.tinyfish.jeekmusic.data.sync

import kotlinx.coroutines.ensureActive
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.atomic.AtomicLong

/** Only file-content reads count as hashBytes; certificate/path/credential digests do not. */
internal object LanFileMetrics {
    val metadataObservations=AtomicLong()
    val hashBytes=AtomicLong()
    suspend fun digest(file: File): String {
        val hash=MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input -> val buffer=ByteArray(128*1024);while(true) { kotlinx.coroutines.currentCoroutineContext().ensureActive();val n=input.read(buffer);if(n<0)break;hashBytes.addAndGet(n.toLong());hash.update(buffer,0,n) } }
        return hash.digest().joinToString("") { "%02x".format(it) }
    }
    fun version(row: JSONObject?): String?=row?.takeUnless { it.isNull("metadataVersion") }?.optString("metadataVersion")?.takeIf { it.isNotEmpty() }
    fun token(size: Long,ns: Long)="m2:$size:$ns"
    fun absent()=JSONObject().put("known",true).put("exists",false).put("metadataVersion",JSONObject.NULL)
}
