package com.tinyfish.jeekmusic.data.sync

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.LinkProperties
import com.tinyfish.jeekmusic.R
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import javax.inject.Inject
import javax.inject.Singleton

data class SyncNetworkState(val wifiOnly: Boolean, val wifiConnected: Boolean) {
    val waitingForWifi get() = wifiOnly && !wifiConnected
}

internal class SyncWifiRequiredException(message: String) : IOException(message)

/** One device-local policy for every PC sync connection, including independent learning/polling. */
@Singleton
class SyncNetworkPolicy @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val store: LanSyncStore,
) : java.io.Closeable {
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private val sockets = mutableMapOf<Socket, Network?>()
    private val _state = MutableStateFlow(snapshot())
    val state = _state.asStateFlow()
    val wifiOnly get() = store.read().optBoolean("wifiOnly", true)
    val waitingForWifi get() = wifiOnly && wifiNetwork() == null

    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = refresh()
        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) = refresh()
        override fun onLinkPropertiesChanged(network: Network, properties: LinkProperties) = refresh()
        override fun onLost(network: Network) {
            synchronized(sockets) { sockets.filterValues { it == network }.keys.toList().forEach { runCatching { it.close() } } }
            refresh()
        }
    }
    private val wifiCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = refresh()
        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) = refresh()
        override fun onLinkPropertiesChanged(network: Network, properties: LinkProperties) = refresh()
        override fun onLost(network: Network) = callback.onLost(network)
    }
    init {
        connectivity.registerDefaultNetworkCallback(callback)
        connectivity.registerNetworkCallback(NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build(), wifiCallback)
    }

    private fun isWifi(network: Network?) = network != null && connectivity.getNetworkCapabilities(network)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
    private fun wifiNetwork(): Network? {
        val active=connectivity.activeNetwork
        if(isWifi(active))return active
        // Do not bypass an active VPN. A local-only Wi-Fi can otherwise be used even
        // when Android keeps cellular as its default internet network.
        if(connectivity.getNetworkCapabilities(active)?.hasTransport(NetworkCapabilities.TRANSPORT_VPN)==true)return null
        return connectivity.allNetworks.firstOrNull { isWifi(it) && connectivity.getLinkProperties(it)?.linkAddresses?.isNotEmpty()==true }
    }
    private fun snapshot() = SyncNetworkState(wifiOnly, wifiNetwork()!=null)
    private fun refresh() = synchronized(sockets) {
        val next = snapshot()
        _state.value = next
        if (next.waitingForWifi) sockets.keys.toList().forEach { runCatching { it.close() } }
    }
    fun setWifiOnly(value: Boolean) = synchronized(sockets) {
        store.update { it.put("wifiOnly", value) }
        // A connection opened on cellular before the switch must not continue afterwards.
        if (value) sockets.filterValues { !isWifi(it) }.keys.toList().forEach { runCatching { it.close() } }
        refresh()
    }
    internal fun requireAllowed() {
        if (waitingForWifi) throw SyncWifiRequiredException(context.getString(R.string.sync_wifi_waiting))
    }
    internal fun connect(socket: Socket, host: String, port: Int) {
        val (network, restricted) = synchronized(sockets) {
            requireAllowed()
            val restricted=wifiOnly
            val network=if(restricted)wifiNetwork() else connectivity.activeNetwork
            sockets[socket]=network
            network to restricted
        }
        try {
            if (restricted && !isWifi(network)) throw SyncWifiRequiredException(context.getString(R.string.sync_wifi_waiting))
            // Select DNS and TCP together even when cellular is allowed: an unbound socket
            // can otherwise retain the process's previous default during a network transition.
            val address = if (network != null) {
                network.bindSocket(socket)
                InetSocketAddress(network.getByName(host), port)
            } else InetSocketAddress(host, port)
            requireAllowed()
            socket.connect(address, 10_000)
            requireAllowed()
        } catch (e: Exception) { release(socket); throw e }
    }
    internal fun release(socket: Socket) { synchronized(sockets) { sockets.remove(socket) }; runCatching { socket.close() } }
    override fun close() {
        connectivity.unregisterNetworkCallback(callback)
        connectivity.unregisterNetworkCallback(wifiCallback)
        synchronized(sockets) { sockets.keys.toList().forEach(::release) }
    }
}
