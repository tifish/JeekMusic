package com.tinyfish.jeekmusic.data.sync

import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/** Platform TLS only. The expected DER SHA-256 comes out of band from the PC's invitation. */
internal class LanProtocol(private val connection: JSONObject) : java.io.Closeable {
    @Volatile var active: Socket? = null
    private var input: DataInputStream? = null
    private var output: DataOutputStream? = null
    fun cancel() { runCatching { active?.close() } }
    override fun close() { cancel(); active = null; input = null; output = null }
    @Synchronized fun call(op: String, args: JSONObject = JSONObject()): JSONObject {
        try {
            if (input == null) connect()
            args.put("op", op).put("deviceId", connection.getString("deviceId"))
            if (op != "pair") args.put("token", connection.getString("token"))
            val bytes = args.toString().toByteArray(Charsets.UTF_8)
            require(bytes.size <= 32_000_000) { "User data exceeds LAN frame limit" }
            output!!.writeInt(bytes.size); output!!.write(bytes); output!!.flush()
            val size = input!!.readInt(); require(size in 2..32_000_000) { "Invalid LAN response" }
            val result = JSONObject(ByteArray(size).also(input!!::readFully).toString(Charsets.UTF_8))
            if (result.has("error")) error(result.getString("error"))
            return result
        } catch (e: Exception) { close(); throw e }
    }
    private fun connect() {
        val pin = connection.getString("pin")
        require(pin.matches(Regex("[0-9a-f]{64}"))) { "Invalid certificate fingerprint" }
        val trust = object : X509TrustManager {
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) { error("Client trust not used") }
            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
                require(chain.isNotEmpty() && sha(chain[0].encoded) == pin) { "PC certificate changed; pair again on the PC" }
                chain[0].checkValidity()
            }
        }
        val tls = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), null) }
        val socket = Socket()
        active = socket
        try {
            socket.connect(InetSocketAddress(connection.getString("host"), connection.getInt("port")), 10_000)
            socket.soTimeout = 120_000
            val ssl = tls.socketFactory.createSocket(socket, connection.getString("host"), connection.getInt("port"), true) as SSLSocket
                active = ssl
                ssl.enabledProtocols = ssl.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
                ssl.startHandshake()
                input = DataInputStream(ssl.inputStream); output = DataOutputStream(ssl.outputStream)
        } catch (e: Exception) { socket.close(); throw e }
    }
    companion object {
        fun sha(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
        fun relative(path: String, root: Boolean = false): String {
            if (path.isEmpty() && root) return path
            require(path.length <= 2048 && !path.contains('\\') && !path.contains(':')) { "Unsafe path" }
            for (part in path.split('/')) require(part.isNotEmpty() && !part.startsWith('.') && !part.endsWith('.') && !part.endsWith(' ') &&
                part.none { it < ' ' || it in "<>\"|?*" } && !Regex("(?i)^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\\.)").containsMatchIn(part)) { "Unsafe path component" }
            return path
        }
    }
}
