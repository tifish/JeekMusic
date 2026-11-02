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
    private var authenticated = false
    @Volatile private var cancelled = false
    private val activePlan = java.util.concurrent.atomic.AtomicReference<String?>(null)
    internal val pendingPlanJobId: String? get() = activePlan.get()
    @Volatile internal var remoteCancellationState = "none"
        private set
    private fun cancelRemotePlan() {
        val id=activePlan.getAndSet(null) ?: return
        remoteCancellationState="queued"
        try { remoteCancellation.execute {
            remoteCancellationState=try { LanProtocol(connection).use { it.call("plan_cancel",JSONObject().put("job",id)).optString("state","unreachable") } } catch(_: Exception) { "unreachable" }
        } } catch(_: java.util.concurrent.RejectedExecutionException) { remoteCancellationState="unreachable" }
    }
    fun cancel() { cancelled = true; runCatching { active?.close() }; cancelRemotePlan() }
    override fun close() { runCatching { active?.close() }; active = null; input = null; output = null; authenticated = false }
    private fun pause(milliseconds: Long) {
        var remaining = milliseconds
        while(remaining > 0) { check(!cancelled) { "LAN transfer cancelled" }; val step=minOf(remaining,100L); Thread.sleep(step); remaining-=step }
        check(!cancelled) { "LAN transfer cancelled" }
    }
    @Synchronized fun call(op: String, args: JSONObject = JSONObject()): JSONObject {
        for(attempt in 0..2) {
            try { return callOnce(op,args) }
            catch(e: LanRemoteException) {
                if(e.code != "busy" || op !in setOf("roots","tree","plan","plan_status","manifest","read","file_open","file_rows","file_baseline","file_probe","file_ack","file_begin","file_chunk","file_finish","file_delete","file_upload_status","file_read","file_abort","capabilities","state","preference_delta","preference_results") || attempt == 2) throw e
                close(); pause(30_000)
            }
        }
        error("LAN retry budget exhausted")
    }
    private fun callOnce(op: String, args: JSONObject): JSONObject {
        try {
            pause(requestDelay())
            if (input == null) connect()
            if (op != "pair" && !authenticated) {
                val auth = JSONObject().put("op", "auth").put("protocol", 4).put("deviceId", connection.getString("deviceId")).put("token", connection.getString("token"))
                send(auth, 4096)
                val accepted = try { receive(4096) } catch (e: LanRemoteException) {
                    if(e.code == "request_refused") throw java.io.IOException("Upgrade the PC for LAN protocol 4", e)
                    throw e
                }
                require(accepted.optInt("protocol") == 4 && accepted.optBoolean("authenticated")) { "Upgrade the PC for LAN protocol 4" }
                authenticated = true
            }
            args.put("op", op).put("deviceId", connection.getString("deviceId"))
            if (op != "pair") args.put("token", connection.getString("token"))
            if (op == "state" || op == "preference_delta") pause(550) // Bound legitimate paginated uploads across one connection.
            if (op == "read") pause(20)
            val bytes = args.toString().toByteArray(Charsets.UTF_8)
            require(bytes.size <= requestLimit(op)) { "User data exceeds operation budget; split the selection or upgrade both clients" }
            if (op != "pair") {
                send(JSONObject().put("op", "request").put("kind", op).put("length", bytes.size), 4096)
                require(receive(4096).optBoolean("ready")) { "PC did not accept request" }
            }
            output!!.writeInt(bytes.size); output!!.write(bytes); output!!.flush()
            var result = receive(if (op == "state") 32_000_000 else 2_000_000)
            if (op in setOf("plan","file_probe","file_ack","file_finish","file_delete")) {
                if(result.optBoolean("pending")) activePlan.set(result.getString("job"))
                if(cancelled) cancelRemotePlan()
                val deadline = System.nanoTime() + java.util.concurrent.TimeUnit.MINUTES.toNanos(31)
                while (result.optBoolean("pending")) {
                    if (System.nanoTime() > deadline) {
                        call("plan_cancel", JSONObject().put("job", result.getString("job")))
                        throw java.io.IOException("Plan exceeded time budget; select smaller folders")
                    }
                    pause(500)
                    result = call("plan_status", JSONObject().put("job", result.getString("job")))
                }
            }
            if(op in setOf("plan","file_probe","file_ack","file_finish","file_delete")) activePlan.set(null)
            return result
        } catch (e: Exception) { if(cancelled) cancelRemotePlan(); close(); throw e }
    }
    private fun send(value: JSONObject, maximum: Int) {
        val bytes = value.toString().toByteArray(Charsets.UTF_8); require(bytes.size <= maximum)
        output!!.writeInt(bytes.size); output!!.write(bytes); output!!.flush()
    }
    private fun receive(maximum: Int): JSONObject {
        val size = input!!.readInt(); require(size in 2..maximum) { "Invalid LAN response" }
        val bytes = ByteArray(size).also(input!!::readFully)
        val text = Charsets.UTF_8.newDecoder().onMalformedInput(java.nio.charset.CodingErrorAction.REPORT).decode(java.nio.ByteBuffer.wrap(bytes)).toString()
        val result = JSONObject(text)
        if (result.has("error")) throw LanRemoteException(result.optString("code"), result.getString("error"))
        return result
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
            socket.soTimeout = 10_000
            val ssl = tls.socketFactory.createSocket(socket, connection.getString("host"), connection.getInt("port"), true) as SSLSocket
                active = ssl
                ssl.enabledProtocols = ssl.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
                ssl.startHandshake()
                ssl.soTimeout = 45_000
                input = DataInputStream(ssl.inputStream); output = DataOutputStream(ssl.outputStream)
        } catch (e: Exception) { socket.close(); throw e }
    }
    companion object {
        private val remoteCancellation = java.util.concurrent.ThreadPoolExecutor(1,1,0L,java.util.concurrent.TimeUnit.MILLISECONDS,
            java.util.concurrent.ArrayBlockingQueue<Runnable>(4),java.util.concurrent.ThreadFactory { task -> Thread(task,"LAN plan cancellation").apply { isDaemon=true } },java.util.concurrent.ThreadPoolExecutor.AbortPolicy())
        private val pacingGate = Any()
        private var nextRequest = 0L
        private fun requestDelay(): Long = synchronized(pacingGate) {
            val now=System.nanoTime(); val at=maxOf(now,nextRequest)
            nextRequest=at+20_000_000L
            maxOf(0L,(at-now)/1_000_000L)
        }

        internal fun requestLimit(op: String): Int = when(op) { "pair", "auth" -> 4096; "state" -> 32_000_000; "preference_delta" -> 4_000_000; "file_chunk" -> 512*1024; else -> 64 * 1024 }

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

internal class LanRemoteException(val code: String, message: String) : java.io.IOException(message)
