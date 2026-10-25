package com.tinyfish.jeekmusic.data.tags

import java.io.RandomAccessFile

/** No version migration: v2.3 uses UTF-16/BOM, v2.4 uses UTF-8. Unknown frames stay raw. */
internal object Id3Tags {
    private data class Frame(val id: String, val raw: ByteArray) { val body get() = raw.copyOfRange(10, raw.size) }
    private data class Slot(val language: ByteArray, val description: String, val text: String)

    fun parse(input: RandomAccessFile, edit: TagEdit): TagLayout {
        val header = input.bytes(0, minOf(10L, input.length()))
        val tagged = header.size >= 10 && header.copyOfRange(0, 3).contentEquals(ascii("ID3"))
        val version = if (tagged) header[3].toInt() else 4
        require(version == 3 || version == 4) { "ID3v2.2 requires an explicit migration" }
        require(!tagged || (header[4] == 0.toByte() && header[5] == 0.toByte())) { "ID3 flags/extended header/unsynchronisation require a lossless migration" }
        val end = if (tagged) 10L + readSync(header, 6) else 0L
        val body = if (tagged) input.bytes(10, end - 10) else byteArrayOf()
        require(end < input.length()) { "No MPEG payload" }
        val first = input.bytes(end, minOf(3, input.length() - end))
        require(!first.contentEquals(ascii("ID3"))) { "Stacked ID3 tags" }
        // Refuse files masquerading as MP3 (including RIFF/ID3 chunks and arbitrary data).
        require(first.size >= 2 && first[0].toInt() and 255 == 255 && first[1].toInt() and 224 == 224) { "Missing MPEG sync" }
        val frames = ArrayList<Frame>()
        var p = 0
        while (p < body.size) {
            if (body[p] == 0.toByte()) { require(body.copyOfRange(p, body.size).all { it == 0.toByte() }); break }
            require(p <= body.size - 10) { "Truncated ID3 frame" }
            val id = String(body, p, 4, Charsets.US_ASCII)
            require(id.all { it in 'A'..'Z' || it in '0'..'9' }) { "Invalid frame ID" }
            require(id !in setOf("CHAP", "CTOC", "ASPI", "SEEK")) {
                "$id may contain file-position references; lossless relocation is not supported"
            }
            val n = if (version == 4) readSync(body, p + 4).toLong() else u32(body, p + 4)
            require(n <= body.size - p - 10) { "Invalid frame size" }
            // Tag-alter preservation means the frame is invalid after ANY tag edit,
            // even if its opaque payload survives byte-for-byte. Dropping it would
            // violate preservation too, so refuse instead (v2.3 a / v2.4 a bits).
            val tagAlterMask = if (version == 3) 0x80 else 0x40
            require(body[p + 8].toInt() and tagAlterMask == 0) { "$id is invalidated by tag alteration" }
            frames += Frame(id, body.copyOfRange(p, p + 10 + n.toInt()))
            p += 10 + n.toInt()
        }
        val retained = ArrayList<ByteArray>()
        val protected = arrayListOf("ID3v2.$version")
        var cover: ByteArray? = null
        var coverCount = 0
        var lyrics: String? = null
        var lyricSlot: Slot? = null
        var lyricCount = 0
        for (frame in frames) {
            val b = frame.body
            val targeted = when {
                edit.cover != null && frame.id == "APIC" -> {
                    require(frame.raw[8] == 0.toByte() && frame.raw[9] == 0.toByte()) { "Encoded APIC" }
                    val mimeEnd = b.indexOf(0, 1)
                    require(mimeEnd > 1 && mimeEnd + 1 < b.size)
                    val type = b[mimeEnd + 1].toInt() and 255
                    if (type == 0 || type == 3) {
                        val descEnd = terminator(b, mimeEnd + 2, b[0].toInt())
                        cover = b.copyOfRange(descEnd, b.size)
                        coverCount++
                        true
                    } else false
                }
                edit.lyrics != null && frame.id == "SYLT" -> error("Synchronized lyrics require an explicit slot selection")
                edit.lyrics != null && frame.id == "TXXX" -> {
                    require(frame.raw[8] == 0.toByte() && frame.raw[9] == 0.toByte()) { "Encoded custom fields require explicit lyric-slot inspection" }
                    // Keep custom fields raw. Legacy lyric aliases are ambiguous with USLT.
                    require(b.isNotEmpty())
                    val dEnd = terminator(b, 1, b[0].toInt())
                    val description = text(b.copyOfRange(1, dEnd - width(b[0].toInt())), b[0].toInt())
                    require(description.uppercase() !in setOf("LYRICS", "USLT", "UNSYNCEDLYRICS") && !description.uppercase().startsWith("LYRICS:")) { "Custom lyric slot requires explicit selection" }
                    false
                }
                edit.lyrics != null && frame.id == "USLT" -> {
                    require(frame.raw[8] == 0.toByte() && frame.raw[9] == 0.toByte()) { "Encoded USLT" }
                    require(b.size >= 5)
                    val marker = b[0].toInt()
                    val dEnd = terminator(b, 4, marker)
                    val desc = b.copyOfRange(4, dEnd - width(marker))
                    require(marker != 0 || desc.all { it.toInt() and 128 == 0 }) { "Legacy lyric description cannot be safely transcoded" }
                    lyricSlot = Slot(b.copyOfRange(1, 4), text(desc, marker), text(b.copyOfRange(dEnd, b.size), marker))
                    lyrics = lyricSlot.text
                    lyricCount++
                    true
                }
                else -> false
            }
            if (!targeted) { retained += frame.raw; protected += hash(frame.raw) }
        }
        require(lyricCount <= 1) { "Multiple lyric language/description slots" }
        if (edit.lyrics != null) {
            val slot = lyricSlot ?: Slot(ascii("eng"), "", "")
            protected += "lyric-slot:${slot.language.hex()}:${hash(utf8(slot.description))}"
        }
        // APIC differs from a text frame: encoding byte, MIME, role, description, image.
        // Construct explicitly so the UTF-16 BOM belongs to the description, not the MIME.
        if (edit.cover != null) {
            retained += frame("APIC", byteArrayOf(marker(version)) + ascii("image/jpeg") + byteArrayOf(0, 3) + encoded("", version).drop(1).toByteArray() + terminatorBytes(version) + edit.cover, version)
        }
        edit.lyrics?.let {
            val slot = lyricSlot ?: Slot(ascii("eng"), "", "")
            retained += frame("USLT", byteArrayOf(marker(version)) + slot.language + encoded(slot.description, version).drop(1).toByteArray() + terminatorBytes(version) + encoded(it, version).drop(1).toByteArray(), version)
        }
        val rendered = join(retained)
        protected += rangeHash(input, end, input.length() - end) // includes the exact original ID3v1, APE, audio and tails
        return TagLayout(0, end, ascii("ID3") + byteArrayOf(version.toByte(), 0, 0) + syncSize(rendered.size) + rendered, protected, cover, lyrics, coverCount = coverCount)
    }

    private fun frame(id: String, body: ByteArray, version: Int): ByteArray =
        ascii(id) + (if (version == 4) syncSize(body.size) else be32(body.size)) + byteArrayOf(0, 0) + body
    private fun marker(version: Int): Byte = if (version == 3) 1 else 3
    private fun encoded(value: String, version: Int) = byteArrayOf(marker(version)) + if (version == 3) byteArrayOf(-1, -2) + value.toByteArray(Charsets.UTF_16LE) else utf8(value)
    private fun terminatorBytes(version: Int) = ByteArray(if (version == 3) 2 else 1)
    private fun width(marker: Int): Int { require(marker in 0..3); return if (marker == 1 || marker == 2) 2 else 1 }
    private fun terminator(b: ByteArray, start: Int, marker: Int): Int {
        val w = width(marker)
        var p = start
        while (p <= b.size - w) {
            if (b[p] == 0.toByte() && (w == 1 || b[p + 1] == 0.toByte())) return p + w
            p += w
        }
        error("Missing ID3 string terminator")
    }
    private fun text(b: ByteArray, marker: Int): String = decode(b, when (marker) {
        0 -> Charsets.ISO_8859_1
        1 -> { require(b.isEmpty() || (b.size >= 2 && ((b[0] == (-1).toByte() && b[1] == (-2).toByte()) || (b[0] == (-2).toByte() && b[1] == (-1).toByte())))); Charsets.UTF_16 }
        2 -> Charsets.UTF_16BE
        3 -> Charsets.UTF_8
        else -> error("Unknown ID3 text encoding")
    })
    private fun ByteArray.indexOf(value: Byte, start: Int): Int = (start until size).firstOrNull { this[it] == value } ?: -1
}
