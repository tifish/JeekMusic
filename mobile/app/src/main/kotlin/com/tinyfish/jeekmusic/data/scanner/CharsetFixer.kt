package com.tinyfish.jeekmusic.data.scanner

import java.nio.ByteBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.Charset
import java.nio.charset.CodingErrorAction

/**
 * 修复 ID3v2.3 / ID3v1 中"声明为 ISO-8859-1、实际存的是 GBK 字节"的乱码（docs/design.md §4.3）。
 *
 * TagLib 会把这类标签按 Latin-1 原样解码，所以每个字符正好对应一个原始字节，可以无损还原。
 */
object CharsetFixer {

    private val GBK: Charset = Charset.forName("GBK")

    /**
     * @param forced 用户在设置里强制指定的旧标签编码；为 null 时自动判断（只尝试 GBK）。
     */
    fun fix(value: String, forced: Charset? = null): String {
        // 含有 Latin-1 以外的字符，说明已经是正确的 Unicode
        if (value.any { it.code > 0xFF }) return value
        // 纯 ASCII，不需要修复
        if (value.none { it.code >= 0x80 }) return value

        val bytes = value.toByteArray(Charsets.ISO_8859_1)
        if (forced != null) return decodeStrict(bytes, forced) ?: value

        if (!looksLikeGb2312Pairs(bytes)) return value
        val decoded = decodeStrict(bytes, GBK) ?: return value
        return if (decoded.any(::isHan)) decoded else value
    }

    fun fixAll(values: List<String>, forced: Charset? = null): List<String> = values.map { fix(it, forced) }

    /**
     * 自动模式下只接受"高字节总是成对出现、且第二个字节也是高字节"的情况，也就是 GB2312 的编码形态。
     *
     * 这样可以排除西文的误判：比如 "Motörhead" 中的 ö(0xF6) 后面跟着 r(0x72)，
     * 在 GBK 里恰好能解码成一个汉字，但第二个字节是 ASCII，不符合这个形态。
     * 代价是 GBK 扩展区里第二个字节小于 0x80 的少数汉字不会被自动修复，可以在设置里强制指定编码。
     */
    private fun looksLikeGb2312Pairs(bytes: ByteArray): Boolean {
        var i = 0
        while (i < bytes.size) {
            val b = bytes[i].toInt() and 0xFF
            if (b < 0x80) {
                i++
                continue
            }
            if (b !in 0xA1..0xF7 || i + 1 >= bytes.size) return false
            val trail = bytes[i + 1].toInt() and 0xFF
            if (trail !in 0xA1..0xFE) return false
            i += 2
        }
        return true
    }

    private fun decodeStrict(bytes: ByteArray, charset: Charset): String? = try {
        charset.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes))
            .toString()
    } catch (_: CharacterCodingException) {
        null
    }

    private fun isHan(c: Char): Boolean = Character.UnicodeScript.of(c.code) == Character.UnicodeScript.HAN
}
