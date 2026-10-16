package com.tinyfish.jeekmusic.data.scanner

import java.nio.ByteBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.Charset
import java.nio.charset.CodingErrorAction

/**
 * 文本文件（.cue、.lrc）的编码识别（docs/design.md §8.3）：
 * BOM → 严格 UTF-8 → GBK / Big5 → UTF-8 替换模式。
 *
 * Big5 的字节序列往往也是合法的 GBK，所以不能只看能否解码：
 * 所有双字节都落在 GB2312 区（两个字节都 ≥ 0xA1）时按 GBK；否则先试 Big5（它的第二个字节常小于 0xA1），再试 GBK。
 */
object CharsetDetector {

    private val GBK = Charset.forName("GBK")
    private val BIG5 = Charset.forName("Big5")

    fun decode(bytes: ByteArray): String {
        when {
            bytes.startsWith(0xEF, 0xBB, 0xBF) -> return String(bytes, 3, bytes.size - 3, Charsets.UTF_8)
            bytes.startsWith(0xFF, 0xFE) -> return String(bytes, 2, bytes.size - 2, Charsets.UTF_16LE)
            bytes.startsWith(0xFE, 0xFF) -> return String(bytes, 2, bytes.size - 2, Charsets.UTF_16BE)
        }
        decodeStrict(bytes, Charsets.UTF_8)?.let { return it }
        val order = if (allPairsInGb2312(bytes)) listOf(GBK, BIG5) else listOf(BIG5, GBK)
        for (charset in order) decodeStrict(bytes, charset)?.let { return it }
        return String(bytes, Charsets.UTF_8)
    }

    private fun allPairsInGb2312(bytes: ByteArray): Boolean {
        var i = 0
        while (i < bytes.size) {
            val b = bytes[i].toInt() and 0xFF
            if (b < 0x80) {
                i++
                continue
            }
            if (b !in 0xA1..0xF7 || i + 1 >= bytes.size) return false
            if ((bytes[i + 1].toInt() and 0xFF) !in 0xA1..0xFE) return false
            i += 2
        }
        return true
    }

    private fun ByteArray.startsWith(vararg prefix: Int): Boolean =
        size >= prefix.size && prefix.indices.all { (this[it].toInt() and 0xFF) == prefix[it] }

    private fun decodeStrict(bytes: ByteArray, charset: Charset): String? = try {
        charset.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes))
            .toString()
    } catch (_: CharacterCodingException) {
        null
    }
}
