package com.tinyfish.jeekmusic.data.online

import org.junit.Assert.assertEquals
import org.junit.Test

class QqMusicTest {

    @Test
    fun unescapesHtmlEntities() {
        assertEquals("Don't Stop", QqMusic.unescape("Don&#39;t Stop"))
        assertEquals("[ti:A & B]", QqMusic.unescape("[ti:A &amp; B]"))
        assertEquals("\"引号\" <x>", QqMusic.unescape("&quot;引号&quot; &lt;x&gt;"))
        assertEquals("[00:01.00]冒号", QqMusic.unescape("[00&#58;01&#46;00]冒号"))
        assertEquals("é", QqMusic.unescape("&#xe9;"))
    }

    @Test
    fun leavesUnknownEntitiesAlone() {
        assertEquals("&nbsp;", QqMusic.unescape("&nbsp;"))
    }
}
