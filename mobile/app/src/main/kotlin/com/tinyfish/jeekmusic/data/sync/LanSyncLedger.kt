package com.tinyfish.jeekmusic.data.sync

import android.content.Context
import android.util.AtomicFile
import dagger.hilt.android.qualifiers.ApplicationContext
import org.json.JSONObject
import java.io.File
import java.io.FileNotFoundException
import java.io.IOException
import javax.inject.Inject
import javax.inject.Singleton

internal class LanLedgerException(cause: Exception) : IOException("LAN recovery ledger was not committed; file replacement stopped", cause)

/** The sole disk entry point for lan-owned.json. AtomicFile itself is NOT thread-safe.
 * The service's coroutine gate owns mutations; scanners only receive detached snapshots.
 * No caller holds this monitor while scanning, awaiting coroutines, or taking the settings lock.
 */
@Singleton
class LanSyncLedger internal constructor(private val file: AtomicFile) {
    @Inject constructor(@ApplicationContext context: Context) : this(AtomicFile(File(context.filesDir, "lan-owned.json")))

    @Synchronized fun read(): JSONObject = try {
        JSONObject(file.readFully().toString(Charsets.UTF_8))
    } catch (e: FileNotFoundException) {
        if (file.baseFile.exists() || File(file.baseFile.path + ".new").exists() || File(file.baseFile.path + ".bak").exists()) throw e
        JSONObject()
    }

    @Synchronized internal fun write(value: JSONObject) {
        val bytes = value.toString().toByteArray(Charsets.UTF_8)
        try {
            val stream = file.startWrite()
            try {
                stream.write(bytes)
                stream.fd.sync() // Unlike AtomicFile.finishWrite's logging, sync failure must throw.
                file.finishWrite(stream)
                // finishWrite logs rename/close failures without throwing. Verify the committed base
                // directly; openRead would remove an uncommitted .new file and hide a failed rename.
                if (File(file.baseFile.path + ".new").exists() || !file.baseFile.readBytes().contentEquals(bytes))
                    throw IOException("Recovery ledger commit verification failed")
            } catch (e: Exception) {
                runCatching { file.failWrite(stream) }.onFailure { e.addSuppressed(it) }
                throw e
            }
        } catch (e: Exception) {
            throw LanLedgerException(e)
        }
    }
}
