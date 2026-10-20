package com.tinyfish.jeekmusic.playback

import androidx.media3.common.Player
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.lang.reflect.Modifier

class NextAlwaysPlayerTest {

    /** 这就是不能用 Kotlin `by` 委托的原因：Java 默认方法不会被转发。 */
    @Test
    fun kotlinDelegationDropsJavaDefaultMethods() {
        val calls = mutableListOf<String>()
        val target = object : Player.Listener {
            override fun onIsPlayingChanged(isPlaying: Boolean) {
                calls += "playing=$isPlaying"
            }
        }
        val byDelegation = object : Player.Listener by target {}
        byDelegation.onIsPlayingChanged(true)
        assertEquals(emptyList<String>(), calls)

        CommandsListener(target) { it }.onIsPlayingChanged(true)
        assertEquals(listOf("playing=true"), calls)
    }

    /** 升级 Media3 后 Player.Listener 如果新增了回调，这里会失败，提醒在 CommandsListener 里补上转发。 */
    @Test
    fun commandsListenerForwardsEveryCallback() {
        val missing = Player.Listener::class.java.methods
            .filter { it.isDefault && !Modifier.isStatic(it.modifiers) }
            .filter { m -> runCatching { CommandsListener::class.java.getDeclaredMethod(m.name, *m.parameterTypes) }.isFailure }
            .map { it.name }
        assertTrue("CommandsListener 没有转发：$missing", missing.isEmpty())
    }

    @Test
    fun equalityFollowsTheWrappedListener() {
        val listener = object : Player.Listener {}
        assertEquals(CommandsListener(listener) { it }, CommandsListener(listener) { it })
    }
}
