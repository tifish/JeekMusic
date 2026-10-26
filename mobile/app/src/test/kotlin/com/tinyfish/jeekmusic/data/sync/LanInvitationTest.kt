package com.tinyfish.jeekmusic.data.sync

import org.junit.Assert.*
import org.junit.Test

class LanInvitationTest {
    private val pin = "a".repeat(64)
    private val token = "b".repeat(64)
    private val good = "jeeklan://192.168.1.2:39843/$pin/$token"
    @Test fun manualAndScannerUseStrictCredentialParser() {
        val invite = LanInvitation.parse(good)
        assertEquals("192.168.1.2:39843", invite.endpoint)
        assertEquals(pin, invite.pin)
        assertEquals(token, invite.token)
        assertFalse(invite.toString().contains(token))
        assertEquals("pc-name.local", LanInvitation.parse(good.replace("192.168.1.2", "pc-name.local")).host)
        val rejected = listOf(
            good + "/", good + "/extra", good + "?x=1", good + "#fragment", good + "?", good + "#",
            good.replace("jeeklan", "https"), good.replace("://", "://user@"),
            good.replace(":39843", ":0"), good.replace(":39843", ":65536"), good.replace(":39843", ":039843"),
            good.replace(pin, "%61" + pin.drop(1)), good.replace(token, token.uppercase()),
            " $good", "$good\n", good.replace("192.168.1.2", "pc..local"), good.replace("192.168.1.2", "-pc.local"),
            good.replace("192.168.1.2", "[::1]"), good.replace("192.168.1.2", "pc%2elocal"), "x".repeat(10000),
        )
        rejected.forEach { raw ->
            try { LanInvitation.parse(raw); fail("Invalid invitation accepted (credential redacted)") }
            catch (e: IllegalArgumentException) { assertEquals("Invalid PC invitation", e.message) }
        }
    }
}
