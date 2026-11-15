package com.tinyfish.jeekmusic.update

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import com.tinyfish.jeekmusic.BuildConfig
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import javax.inject.Inject
import javax.inject.Singleton

data class UpdateDownloadStatus(val versionCode: Int = 0, val progress: Float? = null, val ready: Boolean = false)

/** One validated cache and one transfer for automatic preparation and explicit installation.
 * Automatic connections use a specific Wi-Fi Network, including redirects, never the mobile
 * default. Partial bytes are private and never handed to PackageInstaller.
 */
@Singleton
class UpdateDownload @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val installer: UpdateInstaller,
) {
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private val transfer = Mutex()
    private val _status = MutableStateFlow(UpdateDownloadStatus())
    val status = _status.asStateFlow()
    private val _wifi = MutableStateFlow(wifiNetwork())
    val wifi = _wifi.asStateFlow()
    @Volatile private var automaticConnection: HttpURLConnection? = null
    @Volatile private var automaticNetwork: Network? = null
    @Volatile var automaticEnabled = true
        set(value) {
            field = value
            if (!value) stopAutomatic()
        }

    init {
        connectivity.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = refresh()
            override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) = refresh()
            override fun onLost(network: Network) {
                if (automaticNetwork == network) stopAutomatic()
                refresh()
            }
        })
    }

    private fun wifiNetwork(): Network? = connectivity.activeNetwork?.takeIf {
        val caps = connectivity.getNetworkCapabilities(it)
        caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true &&
            !caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) &&
            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
    }

    private fun refresh() {
        val network = wifiNetwork()
        if (automaticNetwork != null && automaticNetwork != network) automaticConnection?.disconnect()
        _wifi.value = network
    }

    internal fun stopAutomatic() { automaticConnection?.disconnect() }

    internal suspend fun obtain(info: UpdateInfo, automatic: Boolean,
        connectionFactory: (String, Network?) -> HttpURLConnection = ::open): java.io.File = transfer.withLock {
        withContext(Dispatchers.IO) {
            val file = installer.apkFile
            _status.value = UpdateDownloadStatus(info.versionCode)
            if (file.isFile && runCatching { installer.validate(info, file) }.isSuccess) {
                _status.value = UpdateDownloadStatus(info.versionCode, ready = true)
                return@withContext file
            }
            val network = if (automatic) wifiNetwork() ?: throw IOException("wifi_required") else null
            fun requireWifi() {
                if (automatic && (!automaticEnabled || wifiNetwork() != network)) throw IOException("wifi_required")
            }
            requireWifi()
            file.parentFile!!.mkdirs()
            val partial = java.io.File(file.parentFile, "update.apk.part")
            val connection = connectionFactory(info.apkUrl, network)
            if (automatic) { automaticNetwork = network; automaticConnection = connection }
            try {
                requireWifi()
                if (connection.responseCode !in 200..299) throw IOException("http_${connection.responseCode}")
                connection.inputStream.use { input ->
                    partial.outputStream().use { output ->
                        val buffer = ByteArray(64 * 1024)
                        var done = 0L
                        while (true) {
                            currentCoroutineContext().ensureActive()
                            requireWifi()
                            val count = input.read(buffer)
                            if (count < 0) break
                            done += count
                            if (info.sizeBytes <= 0 || done > info.sizeBytes) throw UpdateFailure("size_mismatch")
                            output.write(buffer, 0, count)
                            val progress = (done.toFloat() / info.sizeBytes).coerceIn(0f, 1f)
                            _status.value = UpdateDownloadStatus(info.versionCode, progress)
                            installer.downloadProgress(info, progress)
                        }
                    }
                }
                // Includes size, digest, actual package/version and signer checks for prefetch too.
                installer.validate(info, partial)
                Files.move(partial.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE)
                _status.value = UpdateDownloadStatus(info.versionCode, ready = true)
                file
            } finally {
                connection.disconnect()
                if (automatic) { automaticConnection = null; automaticNetwork = null }
                partial.delete()
                if (!_status.value.ready) _status.value = UpdateDownloadStatus(info.versionCode)
            }
        }
    }

    private fun open(url: String, network: Network?) =
        ((network?.openConnection(URL(url)) ?: URL(url).openConnection()) as HttpURLConnection).apply {
            connectTimeout = 15_000
            readTimeout = 15_000
            setRequestProperty("User-Agent", "JeekMusic/${BuildConfig.VERSION_NAME} (https://github.com/tifish/JeekMusic)")
        }
}
