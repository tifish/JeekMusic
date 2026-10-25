package com.tinyfish.jeekmusic.data.tags

import java.io.ByteArrayOutputStream
import java.io.RandomAccessFile
import java.security.MessageDigest

/** Single-stream Vorbis/Opus only. Audio page granules, lacing and payload remain exact. */
internal object OggTags {
    private data class Page(val raw: ByteArray, val end: Long) {
        val flags get() = raw[5].toInt() and 255
        val serial get() = u32(raw, 14, true)
        val sequence get() = u32(raw, 18, true)
        val segments get() = raw[26].toInt() and 255
    }
    fun parse(input: RandomAccessFile, edit: TagEdit): TagLayout {
        var offset = 0L
        var sequence = 0L
        var serial: Long? = null
        var headersNeeded = 0
        val packets = ArrayList<ByteArray>()
        var packet = ByteArrayOutputStream()
        while (headersNeeded == 0 || packets.size < headersNeeded) {
            val page = page(input, offset)
            if (serial == null) serial = page.serial
            require(page.serial == serial && page.sequence == sequence++) { "Multiplexed/chained Ogg" }
            require(page.flags and 4 == 0 && page.flags and 2 == if (offset == 0L) 2 else 0)
            require(page.flags and 1 == if (packet.size() > 0) 1 else 0)
            var p = 27 + page.segments
            for (i in 0 until page.segments) {
                val n = page.raw[27 + i].toInt() and 255
                require(packet.size() + n <= MAX_METADATA)
                packet.write(page.raw, p, n); p += n
                if (n < 255) {
                    require(headersNeeded == 0 || packets.size < headersNeeded) { "Header/audio mixed page requires repacketization" }
                    packets += packet.toByteArray()
                    packet = ByteArrayOutputStream()
                    if (packets.size == 1) headersNeeded = when {
                        packets[0].size >= 7 && packets[0].copyOfRange(0, 7).contentEquals(byteArrayOf(1) + ascii("vorbis")) -> 3
                        packets[0].size >= 8 && packets[0].copyOfRange(0, 8).contentEquals(ascii("OpusHead")) -> 2
                        else -> error("Not Vorbis/Opus")
                    }
                }
            }
            offset = page.end
            require(offset <= MAX_METADATA)
        }
        require(packet.size() == 0) { "Mixed header/audio page" }
        val vorbis = headersNeeded == 3
        val commentPrefix = if (vorbis) byteArrayOf(3) + ascii("vorbis") else ascii("OpusTags")
        val comment = packets[1]
        require(comment.size >= commentPrefix.size && comment.copyOfRange(0, commentPrefix.size).contentEquals(commentPrefix))
        if (vorbis) {
            require(comment.last() == 1.toByte()) { "Missing Vorbis framing bit" }
            require(packets[2].size >= 7 && packets[2].copyOfRange(0, 7).contentEquals(byteArrayOf(5) + ascii("vorbis")))
        }
        val result = NativeComments.edit(comment.copyOfRange(commentPrefix.size, comment.size - if (vorbis) 1 else 0), edit, true, allowTail = !vorbis)
        val renderedPackets = packets.toMutableList()
        renderedPackets[1] = commentPrefix + result.bytes + if (vorbis) byteArrayOf(1) else byteArrayOf()
        val renderedPages = ArrayList<ByteArray>()
        for ((index, bytes) in renderedPackets.withIndex()) {
            val lacing = List(bytes.size / 255) { 255 } + listOf(bytes.size % 255)
            var position = 0
            val chunks = lacing.chunked(255)
            for ((part, segments) in chunks.withIndex()) {
                val dataSize = segments.sum()
                val header = ascii("OggS") + byteArrayOf(0, ((if (index == 0 && part == 0) 2 else 0) or (if (part > 0) 1 else 0)).toByte()) +
                    (if (part == chunks.lastIndex) ByteArray(8) else ByteArray(8) { -1 }) +
                    le32(serial!!.toInt()) + le32(renderedPages.size) + ByteArray(4) + byteArrayOf(segments.size.toByte()) + segments.map { it.toByte() }.toByteArray()
                renderedPages += checksum(header + bytes.copyOfRange(position, position + dataSize))
                position += dataSize
            }
        }
        // Validate every original audio page and its CRC, single logical stream and sequence.
        // Page sequence/checksum are derived fields; hash every other byte independently.
        val audio = MessageDigest.getInstance("SHA-256")
        var p = offset
        var eos = false
        while (p < input.length()) {
            val page = page(input, p)
            require(!eos && page.serial == serial && page.sequence == sequence++ && page.flags and 2 == 0) { "Chained/multiplexed Ogg" }
            eos = page.flags and 4 != 0
            audio.update(page.raw, 0, 18)
            audio.update(page.raw, 26, page.raw.size - 26)
            p = page.end
        }
        require(eos) { "Incomplete Ogg stream" }
        val protected = listOf(if (vorbis) "Vorbis" else "Opus", hash(packets[0])) +
            (if (vorbis) listOf(hash(packets[2])) else emptyList()) + result.protected + audio.digest().hex()
        return TagLayout(0, offset, join(renderedPages), protected, result.cover, result.lyrics, writeTail = { source, destination ->
            var at = offset
            var seq = renderedPages.size
            while (at < source.length()) {
                val page = page(source, at)
                val raw = page.raw
                le32(seq++).copyInto(raw, 18)
                ByteArray(4).copyInto(raw, 22)
                destination.write(checksum(raw))
                at = page.end
            }
        }, coverCount = result.coverCount)
    }
    private fun page(input: RandomAccessFile, at: Long): Page {
        val header = input.bytes(at, 27)
        require(header.copyOfRange(0, 4).contentEquals(ascii("OggS")) && header[4] == 0.toByte() && header[5].toInt() and 248 == 0)
        val segments = input.bytes(at + 27, (header[26].toInt() and 255).toLong())
        val n = segments.sumOf { it.toInt() and 255 }
        val raw = header + segments + input.bytes(at + 27 + segments.size, n.toLong())
        val expected = raw.copyOfRange(22, 26)
        ByteArray(4).copyInto(raw, 22)
        require(checksum(raw).copyOfRange(22, 26).contentEquals(expected)) { "Invalid Ogg CRC" }
        return Page(raw, at + raw.size)
    }
    private fun checksum(bytes: ByteArray): ByteArray {
        var crc = 0
        for (b in bytes) {
            crc = crc xor ((b.toInt() and 255) shl 24)
            repeat(8) { crc = (crc shl 1) xor (if (crc < 0) 0x04c11db7 else 0) }
        }
        le32(crc).copyInto(bytes, 22)
        return bytes
    }
}
