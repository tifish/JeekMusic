package com.tinyfish.jeekmusic.data.sync

import kotlinx.coroutines.ensureActive
import org.json.JSONObject
import java.io.File
import java.util.concurrent.atomic.AtomicLong

/** Only file-content reads count as hashBytes; certificate/path/credential digests do not. */
internal object LanFileMetrics {
    val metadataObservations=AtomicLong()
    val hashBytes=AtomicLong()
    fun stamp(file: File): String = android.os.ParcelFileDescriptor.open(file,android.os.ParcelFileDescriptor.MODE_READ_ONLY).use { stamp(LanAtomicFiles.identity(it.fd)) }
    fun stamp(identity: LongArray)=token(identity[2],Math.addExact(Math.multiplyExact(identity[3],1_000_000_000),identity[4]))
    fun setModified(file: File,ns: Long) { android.os.ParcelFileDescriptor.open(file,android.os.ParcelFileDescriptor.MODE_READ_WRITE).use { LanAtomicFiles.setModified(it.fd,ns) } }
    fun version(row: JSONObject?): String?=row?.takeUnless { it.isNull("metadataVersion") }?.optString("metadataVersion")?.takeIf { it.isNotEmpty() }
    fun token(size: Long,ns: Long)="m2:$size:$ns"
    fun absent()=JSONObject().put("known",true).put("exists",false).put("metadataVersion",JSONObject.NULL)
}
