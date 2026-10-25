package com.tinyfish.jeekmusic.data.tags

import java.util.Base64

/** Vorbis comments retain vendor, key spelling, order, duplicates and unknown entries verbatim. */
internal object NativeComments {
    data class Result(val bytes: ByteArray, val protected: List<String>, val cover: ByteArray?, val lyrics: String?, val coverCount: Int)
    fun edit(bytes: ByteArray, edit: TagEdit, pictures: Boolean, allowTail: Boolean = false): Result {
        var p = 0
        fun word(): Int { val n = u32(bytes, p, true); p += 4; require(n <= MAX_METADATA); return n.toInt() }
        fun field(): ByteArray { val n = word(); require(n <= bytes.size - p); return bytes.copyOfRange(p, p + n).also { p += n } }
        val vendor = field()
        val count = word()
        require(count <= (bytes.size - p) / 4)
        val entries = ArrayList<ByteArray>()
        val protected = arrayListOf(hash(vendor))
        var cover: ByteArray? = null
        var coverCount = 0
        var lyrics: String? = null
        var lyricKey: ByteArray? = null
        var lyricCount = 0
        repeat(count) {
            val entry = field()
            val equal = entry.indexOf('='.code.toByte())
            require(equal > 0) { "Malformed comment" }
            val keyBytes = entry.copyOfRange(0, equal)
            require(keyBytes.all { (it.toInt() and 255) in 0x20..0x7d && it != '='.code.toByte() })
            val key = String(keyBytes, Charsets.US_ASCII).uppercase()
            val value = entry.copyOfRange(equal + 1, entry.size)
            val targeted = when {
                edit.lyrics != null && (key in setOf("LYRICS", "UNSYNCEDLYRICS", "USLT") || key.startsWith("LYRICS:")) -> {
                    require(key in setOf("LYRICS", "UNSYNCEDLYRICS", "USLT")) { "Described lyric slot" }
                    lyricCount++
                    lyricKey = keyBytes
                    lyrics = runCatching { decode(value) }.getOrNull()
                    true
                }
                pictures && edit.cover != null && key == "METADATA_BLOCK_PICTURE" -> {
                    val picture = Base64.getDecoder().decode(value)
                    val type = NativePicture.type(picture)
                    if (type == 0 || type == 3) { cover = NativePicture.image(picture); coverCount++; true } else false
                }
                pictures && edit.cover != null && key in setOf("COVERART", "COVERARTMIME") -> error("Legacy cover field requires explicit migration")
                else -> false
            }
            if (!targeted) { entries += entry; protected += hash(entry) }
        }
        require(allowTail || p == bytes.size) { "Trailing comment data" }
        val tail = bytes.copyOfRange(p, bytes.size)
        if (allowTail) protected += "opus-extension:${hash(tail)}"
        require(lyricCount <= 1) { "Multiple lyric comment slots" }
        if (edit.lyrics != null) protected += "lyric-key:${hash(lyricKey ?: ascii("LYRICS"))}"
        edit.lyrics?.let { entries += (lyricKey ?: ascii("LYRICS")) + ascii("=") + utf8(it) }
        if (pictures) edit.cover?.let { entries += ascii("METADATA_BLOCK_PICTURE=") + Base64.getEncoder().encode(NativePicture.create(it)) }
        val output = join(listOf(le32(vendor.size), vendor, le32(entries.size)) + entries.flatMap { listOf(le32(it.size), it) } + tail)
        return Result(output, protected, cover, lyrics, coverCount)
    }
    fun empty() = le32(0) + le32(0)
}

internal object NativePicture {
    fun type(bytes: ByteArray): Int { require(bytes.size >= 32); return u32(bytes, 0).toInt() }
    fun image(bytes: ByteArray): ByteArray {
        type(bytes)
        var p = 4
        repeat(2) { val n = u32(bytes, p); p += 4; require(n <= bytes.size - p); p += n.toInt() }
        require(p <= bytes.size - 20)
        p += 16 // width, height, depth and palette count
        val n = u32(bytes, p); p += 4
        require(n == (bytes.size - p).toLong())
        return bytes.copyOfRange(p, bytes.size)
    }
    fun create(image: ByteArray) = be32(3) + be32(10) + ascii("image/jpeg") + be32(0) + ByteArray(16) + be32(image.size) + image
}
