package com.tinyfish.jeekmusic.update

import android.content.ContextWrapper
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.FilterInputStream
import java.io.IOException
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/** Real PackageManager validation of a higher-version same-signer APK, plus deterministic
 * transport fixtures. Wi-Fi loss uses the emulator's real connectivity service; installs are
 * exercised separately through the visible production client's Update button.
 */
@RunWith(AndroidJUnit4::class)
class UpdateDownloadDeviceTest {
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private val context get() = instrumentation.targetContext
    private val source get() = File(requireNotNull(InstrumentationRegistry.getArguments().getString("updateApk")))
    private fun info() = context.packageManager.getPackageArchiveInfo(source.absolutePath, 0)!!.let {
        UpdateInfo(it.longVersionCode.toInt(), it.versionName.orEmpty(), "WiFi update fixture",
            "https://example.invalid/update.apk", source.length(), UpdateInstaller.sha256(source))
    }
    private fun isolated() = object : ContextWrapper(context) {
        private val root = File(context.cacheDir, "update-download-${UUID.randomUUID()}").apply { mkdirs() }
        override fun getCacheDir() = File(root, "cache").apply { mkdirs() }
        override fun getNoBackupFilesDir() = File(root, "journal").apply { mkdirs() }
    }
    private fun connection(stream: InputStream) = object : HttpURLConnection(URL("https://example.invalid/update.apk")) {
        override fun connect() = Unit
        override fun disconnect() = Unit
        override fun usingProxy() = false
        override fun getResponseCode() = 200
        override fun getInputStream() = stream
    }
    private fun shell(command: String) {
        instrumentation.uiAutomation.executeShellCommand(command).use { descriptor ->
            android.os.ParcelFileDescriptor.AutoCloseInputStream(descriptor).use { it.readBytes() }
        }
    }
    private fun wifiConnected(): Boolean {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        return cm.getNetworkCapabilities(cm.activeNetwork)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
    }
    private fun waitWifi(value: Boolean) {
        val end = System.currentTimeMillis() + 15_000
        while (wifiConnected() != value && System.currentTimeMillis() < end) Thread.sleep(100)
        assertEquals("emulator Wi-Fi state", value, wifiConnected())
    }

    @Test
    fun prefetchAndExplicitDownloadShareOneTransferAndCacheSurvivesRestart() = runBlocking {
        shell("svc wifi enable"); waitWifi(true)
        val ctx = isolated(); val installer = UpdateInstaller(ctx); val download = UpdateDownload(ctx, installer)
        val info = info(); installer.offer(info)
        val reads = AtomicInteger(); val started = CountDownLatch(1)
        val release = CountDownLatch(1)
        val automatic = async(Dispatchers.IO) {
            download.obtain(info, true) { _, network ->
                assertNotNull(network); reads.incrementAndGet()
                connection(object : FilterInputStream(source.inputStream()) {
                    override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
                        started.countDown(); check(release.await(10, TimeUnit.SECONDS))
                        return super.read(buffer, offset, length)
                    }
                })
            }
        }
        check(started.await(10, TimeUnit.SECONDS))
        val explicit = async(Dispatchers.IO) {
            download.obtain(info, false) { _, _ -> error("A second transfer must not be opened") }
        }
        release.countDown()
        assertEquals(automatic.await(), explicit.await())
        assertEquals(1, reads.get()); assertTrue(download.status.value.ready)
        assertEquals("available", installer.diagnostics().getString("stage"))
        assertEquals(-1, installer.diagnostics().getInt("sessionId"))
        val restored = UpdateInstaller(ctx); assertTrue(restored.state.value is UpdateState.Available)
        val restarted = UpdateDownload(ctx, restored).apply { automaticEnabled = false }
        assertEquals(installer.apkFile, restarted.obtain(info, true) { _, _ -> error("Offline cache reuse must not connect") })
        assertTrue(restarted.status.value.ready)
        restored.dismiss(); assertEquals(UpdateState.Idle, UpdateInstaller(ctx).state.value)
        assertNotNull(restored.pendingDownload())
    }

    @Test
    fun wifiLossStopsAutomaticTransferAndExplicitDownloadRemainsAvailable() = runBlocking {
        shell("svc wifi enable"); waitWifi(true)
        val ctx = isolated(); val installer = UpdateInstaller(ctx); val download = UpdateDownload(ctx, installer)
        val info = info(); var opens = 0; var firstRead = true
        try {
            download.obtain(info, true) { _, network ->
                assertNotNull(network); opens++
                connection(object : FilterInputStream(source.inputStream()) {
                    override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
                        val count = super.read(buffer, offset, length)
                        if (firstRead) { firstRead = false; shell("svc wifi disable"); waitWifi(false) }
                        return count
                    }
                })
            }
            fail("Wi-Fi loss must stop preparation")
        } catch (_: IOException) {
            assertEquals(1, opens); assertFalse(installer.apkFile.exists())
            assertFalse(File(installer.apkFile.parentFile, "update.apk.part").exists())
            assertFalse(download.status.value.ready)
            val file = download.obtain(info, false) { _, network ->
                assertNull(network); opens++; connection(source.inputStream())
            }
            installer.validate(info, file); assertEquals(2, opens)
        } finally { shell("svc wifi enable"); waitWifi(true) }
    }

    @Test
    fun preparationRejectsInvalidArchiveAndNeverPublishesPartialOrTamperedCache() = runBlocking {
        shell("svc wifi enable"); waitWifi(true)
        val ctx = isolated(); val installer = UpdateInstaller(ctx); val download = UpdateDownload(ctx, installer)
        val info = info()
        for (invalid in listOf(info.copy(sha256 = "0".repeat(64)), info.copy(versionCode = info.versionCode + 1))) {
            try {
                download.obtain(invalid, true) { _, _ -> connection(source.inputStream()) }
                fail("Invalid prefetch must be rejected")
            } catch (_: UpdateFailure) {
                assertFalse(installer.apkFile.exists()); assertFalse(download.status.value.ready)
                assertFalse(File(installer.apkFile.parentFile, "update.apk.part").exists())
            }
        }
        download.obtain(info, false) { _, _ -> connection(source.inputStream()) }
        installer.apkFile.appendBytes(byteArrayOf(1))
        download.automaticEnabled = false
        try {
            download.obtain(info, true) { _, _ -> error("Automatic downloads are disabled") }
            fail("Tampered cache must not be reused")
        } catch (_: IOException) { assertFalse(download.status.value.ready) }
    }
}
