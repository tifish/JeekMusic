package com.tinyfish.jeekmusic.data.sync

import android.content.ContextWrapper
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.net.Socket
import java.util.UUID

/** Opt-in emulator network toggles; private settings and sockets, no stored pairing or music. */
@RunWith(AndroidJUnit4::class)
class SyncNetworkPolicyTest {
    @Test fun wifiOnlyBlocksCellularClosesTransfersAndResumesOnWifi() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val args = InstrumentationRegistry.getArguments()
        assumeTrue(args.getString("jeekNetworkFixture") == "true")
        val host = args.getString("networkHost")!!
        require(host == "10.0.2.2")
        val port = args.getString("networkPort")!!.toInt()
        val base = instrumentation.targetContext
        val folder = File(base.cacheDir, "sync-network-${UUID.randomUUID()}").apply { mkdirs() }
        val context = object : ContextWrapper(base) {
            override fun getFilesDir() = folder
        }
        val store = LanSyncStore(context, LanSyncLedger(context))
        fun shell(command: String): String = instrumentation.uiAutomation.executeShellCommand(command).use { descriptor ->
            android.os.ParcelFileDescriptor.AutoCloseInputStream(descriptor).bufferedReader().use { it.readText().trim() }
        }
        fun waitFor(predicate: () -> Boolean) {
            val end = System.nanoTime() + 20_000_000_000L
            while (!predicate()) { check(System.nanoTime() < end) { "Network transition timed out" }; Thread.sleep(100) }
        }
        fun exchange(policy: SyncNetworkPolicy): Socket {
            val socket = Socket()
            policy.connect(socket, host, port)
            socket.soTimeout = 3000
            socket.getOutputStream().write(42)
            assertEquals(42, socket.getInputStream().read())
            return socket
        }
        val originalWifi = shell("settings get global wifi_on") != "0"
        val originalData = shell("settings get global mobile_data") != "0"
        try {
            shell("svc data enable")
            SyncNetworkPolicy(context, store).use { policy ->
                assertTrue(policy.wifiOnly)
                shell("svc wifi enable"); waitFor { !policy.waitingForWifi }
                val wifi = exchange(policy)
                shell("svc wifi disable"); waitFor { policy.waitingForWifi && wifi.isClosed }
                val connectivity=base.getSystemService(android.net.ConnectivityManager::class.java)
                waitFor { connectivity.getNetworkCapabilities(connectivity.activeNetwork)?.hasTransport(android.net.NetworkCapabilities.TRANSPORT_CELLULAR)==true }
                val connection = JSONObject().put("host", host).put("port", port).put("pin", "0".repeat(64)).put("deviceId", "isolated")
                val denied = runCatching { LanProtocol(connection, policy).use { it.call("roots") } }.exceptionOrNull()
                assertTrue("Transport must refuse before DNS/TLS on cellular", denied is SyncWifiRequiredException)
                policy.setWifiOnly(false)
                assertFalse(LanSyncStore(context, LanSyncLedger(context)).read().optBoolean("wifiOnly", true))
                val cellular = exchange(policy)
                policy.setWifiOnly(true); waitFor { cellular.isClosed }
                assertTrue(policy.waitingForWifi)
                shell("svc wifi enable"); waitFor { !policy.waitingForWifi }
                exchange(policy).use { }
                assertTrue(store.read().getBoolean("wifiOnly"))
            }
        } finally {
            shell(if (originalWifi) "svc wifi enable" else "svc wifi disable")
            shell(if (originalData) "svc data enable" else "svc data disable")
            folder.deleteRecursively()
        }
    }
}
