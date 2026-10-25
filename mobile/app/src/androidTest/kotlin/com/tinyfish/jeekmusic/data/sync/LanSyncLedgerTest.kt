package com.tinyfish.jeekmusic.data.sync

import android.content.ContextWrapper
import android.util.AtomicFile
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** Real platform AtomicFile, synthetic private directories; never clears existing app evidence. */
@RunWith(AndroidJUnit4::class)
class LanSyncLedgerTest {
    private fun context() = object : ContextWrapper(InstrumentationRegistry.getInstrumentation().targetContext) {
        private val isolated = File(super.getFilesDir(), "lan-ledger-tests/${UUID.randomUUID()}").apply { mkdirs() }
        override fun getFilesDir(): File = isolated
    }
    private fun value() = JSONObject().put("pending", JSONObject().put("path", "A.wav").put("mapping",
        JSONObject().put("volume", "external_primary").put("relative", "Documents/Isolated/")))
        .put("refresh", JSONObject().put("task", JSONObject().put("paths", JSONArray(listOf("A.wav", "B.wav")))))

    private class ClosingFile(base: File, val onClose: () -> Unit) : AtomicFile(base) {
        var armed = false
        override fun startWrite(): FileOutputStream {
            val stream = super.startWrite()
            return object : FileOutputStream(stream.fd) {
                override fun close() {
                    stream.close()
                    if (armed) { armed = false; onClose() }
                }
            }
        }
    }

    @Test fun platformBaselineScannerCanDeleteClosedUncommittedNewFile() {
        val base = File(context().filesDir, "lan-owned.json")
        val atomic = ClosingFile(base) {
            assertTrue(File(base.path + ".new").exists())
            assertEquals("{}", String(AtomicFile(base).readFully()))
            assertFalse(File(base.path + ".new").exists())
        }
        atomic.startWrite().also { it.write("{}".toByteArray()); atomic.finishWrite(it) }
        atomic.armed = true
        atomic.startWrite().also { it.write(value().toString().toByteArray()); atomic.finishWrite(it) }
        assertEquals("{}", base.readText()) // finishWrite returned normally despite losing pending/refresh.
        android.util.Log.i("LanLedgerTest", "API ${android.os.Build.VERSION.SDK_INT}: baseline reproduced lost pending/refresh at $base")
    }

    @Test fun scannerReadBlocksUntilVerifiedPendingAndRefreshCommit() {
        val ctx = context(); val base = File(ctx.filesDir, "lan-owned.json")
        val closed = CountDownLatch(1); val release = CountDownLatch(1)
        val atomic = ClosingFile(base) { closed.countDown(); check(release.await(10, TimeUnit.SECONDS)) }
        val ledger = LanSyncLedger(atomic); val store = LanSyncStore(ctx, ledger)
        ledger.write(JSONObject()); atomic.armed = true
        val pool = Executors.newFixedThreadPool(2)
        try {
            val writer = pool.submit { ledger.write(value()) }
            assertTrue(closed.await(10, TimeUnit.SECONDS))
            val readerStarted = CountDownLatch(1)
            lateinit var readerThread: Thread
            val reader = pool.submit<Set<String>> {
                readerThread = Thread.currentThread(); readerStarted.countDown()
                val keys = store.pendingFileKeys() // The production scanner entry point.
                assertEquals(2, ledger.read().getJSONObject("refresh").getJSONObject("task").getJSONArray("paths").length())
                keys
            }
            assertTrue(readerStarted.await(10, TimeUnit.SECONDS))
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
            while (readerThread.state != Thread.State.BLOCKED && System.nanoTime() < deadline) Thread.yield()
            assertEquals(Thread.State.BLOCKED, readerThread.state)
            assertFalse(reader.isDone)
            assertTrue(File(base.path + ".new").exists()) // scanner has not removed the writer's file.
            release.countDown(); writer.get(10, TimeUnit.SECONDS)
            assertEquals(setOf("external_primary:Documents/Isolated/A.wav"), reader.get(10, TimeUnit.SECONDS))
            assertEquals(value().toString(), base.readText())
            assertEquals(value().toString(), LanSyncLedger(AtomicFile(base)).read().toString()) // restart
            android.util.Log.i("LanLedgerTest", "Shared owner blocked scanner at close-before-rename; pending+refresh persisted and survived reopen at $base")
        } finally { release.countDown(); pool.shutdownNow() }
    }

    @Test fun silentlyFailedFinishWriteThrowsBeforeAnyTargetRename() {
        val base = File(context().filesDir, "lan-owned.json")
        val atomic = ClosingFile(base) { check(File(base.path + ".new").delete()) }
        val ledger = LanSyncLedger(atomic)
        ledger.write(JSONObject()); atomic.armed = true
        val renamed = AtomicBoolean(false)
        try {
            ledger.write(value())
            renamed.set(true) // This is the service's boundary before old -> backup.
            fail("An uncommitted ledger must prevent file replacement")
        } catch (_: IOException) { }
        assertFalse(renamed.get()); assertEquals("{}", base.readText())
        android.util.Log.i("LanLedgerTest", "Silent finishWrite failure threw IOException before rename at $base")
    }
}
