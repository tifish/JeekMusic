package com.tinyfish.jeekmusic.data.sync

import java.net.URI

/** Credentials are deliberately excluded from toString and parse errors. No URL is ever opened. */
class LanInvitation private constructor(val host: String, val port: Int, val pin: String, val token: String) {
    val endpoint: String get() = "$host:$port"
    override fun toString(): String = endpoint

    companion object {
        fun parse(value: String): LanInvitation {
            fun invalid(): Nothing = throw IllegalArgumentException("Invalid PC invitation")
            if (value.length !in 150..450 || value.any { it <= ' ' || it >= '\u007f' }) invalid()
            val uri = try { URI(value) } catch (_: Exception) { invalid() }
            if (uri.scheme != "jeeklan" || uri.rawUserInfo != null || uri.rawQuery != null || uri.rawFragment != null || uri.port !in 1..65535) invalid()
            val host = uri.host ?: invalid()
            // Match the PC's IPv4/DNS endpoint format; forbid percent encoding and IPv6 ambiguity.
            if (host.length > 253 || !host.matches(Regex("[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?")) || host.split('.').any { it.isEmpty() || it.length > 63 || it.startsWith('-') || it.endsWith('-') }) invalid()
            val path = uri.rawPath ?: invalid()
            if (!path.matches(Regex("/[0-9a-f]{64}/[0-9a-f]{64}")) || uri.rawAuthority != "$host:${uri.port}") invalid()
            return LanInvitation(host, uri.port, path.substring(1, 65), path.substring(66))
        }
    }
}
