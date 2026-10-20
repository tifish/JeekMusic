package com.tinyfish.jeekmusic.data.scanner

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.description
import com.tinyfish.jeekmusic.objects
import com.tinyfish.jeekmusic.str
import com.tinyfish.jeekmusic.strings
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Test
import java.nio.charset.Charset

/** 用例在 shared/testcases/charset.json（两端共用）。 */
class CharsetFixerTest {

    /** 模拟标签库的行为：把 [charset] 编码的字节按 Latin-1 解码。 */
    private fun mojibake(text: String, charset: String) =
        text.toByteArray(Charset.forName(charset)).toString(Charsets.ISO_8859_1)

    private fun input(o: JSONObject) = o.str("mojibake")?.let { mojibake(o.getString("text"), it) } ?: o.getString("text")

    @Test
    fun fix() {
        val file = SharedCases.load("charset.json")
        for (c in file.cases("fix")) {
            val value = input(c)
            val forced = c.str("forced")?.let { Charset.forName(it) }
            assertEquals(c.description(), c.str("expected") ?: value, CharsetFixer.fix(value, forced))
        }
        for (c in file.cases("fixAll")) {
            val values = c.getJSONArray("values").objects().map(::input)
            assertEquals(c.description(), c.getJSONArray("expected").strings(), CharsetFixer.fixAll(values))
        }
    }

    @Test
    fun detect() {
        for (c in SharedCases.load("charset.json").cases("detect")) {
            val prefix = c.getString("prefix").chunked(2).map { it.toInt(16).toByte() }.toByteArray()
            val bytes = prefix + c.getString("text").toByteArray(Charset.forName(c.getString("charset")))
            assertEquals(c.description(), c.getString("expected"), CharsetDetector.decode(bytes))
        }
    }
}
