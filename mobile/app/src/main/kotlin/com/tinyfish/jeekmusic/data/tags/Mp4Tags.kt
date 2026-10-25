package com.tinyfish.jeekmusic.data.tags

import java.io.RandomAccessFile

/** Apple metadata typed atoms; never moves mdat or changes any chunk offset. */
internal object Mp4Tags {
    private data class Atom(val type: String, val raw: ByteArray, val header: Int = 8) {
        val body get() = raw.copyOfRange(header, raw.size)
    }
    private data class Top(val type: String, val start: Long, val size: Long, val header: Int)
    private val handler = atom("hdlr", ByteArray(8) + ascii("mdirappl") + ByteArray(9))
    fun parse(input: RandomAccessFile, edit: TagEdit): TagLayout {
        val top = ArrayList<Top>()
        var p = 0L
        while (p < input.length()) {
            val h = input.bytes(p, 8)
            var size = u32(h, 0)
            val type = String(h, 4, 4, Charsets.ISO_8859_1)
            val header = if (size == 1L) 16 else 8
            if (size == 1L) {
                val extended = input.bytes(p + 8, 8)
                require(u32(extended, 0) == 0L) { "Atom too large" }
                size = u32(extended, 4)
            }
            // size=0 means through EOF; preserve it only for non-moov atoms before no edit.
            if (size == 0L) size = input.length() - p
            require(size >= header && size <= input.length() - p)
            top += Top(type, p, size, header)
            p += size
        }
        require(top.firstOrNull()?.type == "ftyp" && top.count { it.type == "moov" } == 1 && top.any { it.type == "mdat" }) { "Not an MP4 audio envelope" }
        require(top.none { it.type in setOf("moof", "mfra", "sidx", "ssix", "iloc", "pssh") }) { "Fragmented/protected/indexed MP4 requires a relocation-aware writer" }
        // Top-level metadata is a FullBox too. Do not inspect mdat/opaque payloads
        // as atoms or rely on byte searches for names that may occur in audio.
        for (item in top.filter { it.type in setOf("meta", "udta", "uuid") }) {
            rejectPositionSensitive(listOf(Atom(item.type, input.bytes(item.start, item.size), item.header)))
        }
        val movie = top.single { it.type == "moov" }
        val moov = children(input.bytes(movie.start + movie.header, movie.size - movie.header))
        require(moov.none { it.type in setOf("mvex", "cmov") }) { "Fragmented/compressed movie" }
        rejectPositionSensitive(moov)
        val protected = arrayListOf("MP4")
        fun single(list: List<Atom>, type: String): Atom? = list.filter { it.type == type }.also { require(it.size <= 1) { "Multiple $type atoms" } }.singleOrNull()
        val udtaAtom = single(moov, "udta")
        val udta = udtaAtom?.let { children(it.body) }.orEmpty()
        val metaAtom = single(udta, "meta")
        val metaBody = metaAtom?.body
        require(metaBody == null || metaBody.size >= 4)
        val flags = metaBody?.copyOfRange(0, 4) ?: ByteArray(4)
        require(flags.contentEquals(ByteArray(4))) { "Unsupported meta version/flags" }
        val meta = metaBody?.let { children(it.copyOfRange(4, it.size)) }.orEmpty()
        val ilstAtom = single(meta, "ilst")
        val ilst = ilstAtom?.let { children(it.body) }.orEmpty()
        protected += moov.filter { it.type != "udta" }.map { "moov:${hash(it.raw)}" }
        protected += udta.filter { it.type != "meta" }.map { "udta:${hash(it.raw)}" }
        protected += meta.filter { it.type != "ilst" && !it.raw.contentEquals(handler) }.map { "meta:${hash(it.raw)}" }
        val items = ArrayList<ByteArray>()
        var cover: ByteArray? = null
        var coverCount = 0
        var lyrics: String? = null
        var lyricLocale = ByteArray(4)
        var lyricCount = 0
        for (item in ilst) {
            when {
                item.type == "covr" && edit.cover != null -> {
                    val data = children(item.body)
                    require(data.all { it.type == "data" && it.body.size >= 8 && u32(it.body, 0) in setOf(13L, 14L) }) { "Unsupported cover atom" }
                    cover = data.firstOrNull()?.body?.let { it.copyOfRange(8, it.size) }
                    coverCount += data.size
                }
                item.type == "©lyr" && edit.lyrics != null -> {
                    lyricCount++
                    val data = children(item.body).singleOrNull() ?: error("Multiple lyric data atoms")
                    val b = data.body
                    require(data.type == "data" && b.size >= 8 && u32(b, 0) in setOf(1L, 2L)) { "Unsupported lyric atom type" }
                    lyricLocale = b.copyOfRange(4, 8)
                    lyrics = decode(b.copyOfRange(8, b.size), if (u32(b, 0) == 2L) Charsets.UTF_16BE else Charsets.UTF_8)
                }
                else -> { items += item.raw; protected += "ilst:${hash(item.raw)}" }
            }
        }
        require(lyricCount <= 1) { "Multiple lyric atoms" }
        if (edit.lyrics != null) protected += "lyric-locale:${lyricLocale.hex()}"
        edit.cover?.let { items += atom("covr", atom("data", be32(13) + ByteArray(4) + it)) }
        edit.lyrics?.let { items += atom("©lyr", atom("data", be32(1) + lyricLocale + utf8(it))) }
        val newMeta = meta.filter { it.type != "ilst" }.map { it.raw }.toMutableList()
        if (meta.none { it.type == "hdlr" }) newMeta.add(0, handler)
        newMeta += atom("ilst", join(items))
        val newUdta = udta.filter { it.type != "meta" }.map { it.raw } + atom("meta", flags + join(newMeta))
        val newMoov = atom("moov", join(moov.filter { it.type != "udta" }.map { it.raw } + atom("udta", join(newUdta))))
        val movieIndex = top.indexOf(movie)
        val padding = top.drop(movieIndex + 1).takeWhile { it.type == "free" || it.type == "skip" }
        val end = movie.start + movie.size + padding.sumOf { it.size }
        val available = end - movie.start
        val rendered = if (end == input.length()) newMoov else {
            // A fast-start moov may grow only into existing padding. All other top-level atoms
            // remain at their exact offsets, including mdat/stco/co64.
            val rest = available - newMoov.size
            require(rest == 0L || rest in 8..MAX_METADATA.toLong()) { "Fast-start moov has insufficient safe padding" }
            newMoov + if (rest == 0L) byteArrayOf() else atom("free", ByteArray(rest.toInt() - 8))
        }
        for (item in top) if (item !== movie && item !in padding) protected += "top:${item.start}:${rangeHash(input, item.start, item.size)}"
        return TagLayout(movie.start, end, rendered, protected, cover, lyrics, coverCount = coverCount)
    }
    private fun children(bytes: ByteArray): List<Atom> {
        var p = 0
        val output = ArrayList<Atom>()
        while (p < bytes.size) {
            require(p <= bytes.size - 8)
            var n = u32(bytes, p)
            val header = if (n == 1L) 16 else 8
            if (n == 1L) { require(u32(bytes, p + 8) == 0L); n = u32(bytes, p + 12) }
            require(n >= header && n <= bytes.size - p)
            output += Atom(String(bytes, p + 4, 4, Charsets.ISO_8859_1), bytes.copyOfRange(p, p + n.toInt()), header)
            p += n.toInt()
        }
        return output
    }
    private fun atom(type: String, body: ByteArray): ByteArray = be32(body.size + 8) + ascii(type) + body

    private fun rejectPositionSensitive(atoms: List<Atom>, depth: Int = 0) {
        require(depth <= 32) { "Excessive movie nesting" }
        for (item in atoms) {
            require(item.type !in setOf("saio", "saiz", "senc", "pssh", "sinf", "schi", "tenc", "iloc", "uuid")) {
                "Protected/auxiliary ${item.type} offsets require a relocation-aware writer"
            }
            when (item.type) {
                "trak", "mdia", "minf", "stbl", "edts", "dinf", "udta", "meco" -> rejectPositionSensitive(children(item.body), depth + 1)
                "meta" -> {
                    val body = item.body
                    require(body.size >= 4 && body.copyOfRange(0, 4).contentEquals(ByteArray(4))) { "Unsupported meta version/flags" }
                    rejectPositionSensitive(children(body.copyOfRange(4, body.size)), depth + 1)
                }
                "ilst" -> {
                    for (entry in children(item.body)) {
                        // Each Apple item is a container of typed data/mean/name boxes;
                        // the contents of those leaf boxes are never parsed as a tree.
                        require(entry.type !in setOf("iloc", "uuid")) { "Unclassified indexed/private metadata item" }
                        rejectPositionSensitive(children(entry.body), depth + 1)
                    }
                }
                "stsd" -> {
                    val data = item.body
                    require(data.size >= 8)
                    val entries = children(data.copyOfRange(8, data.size))
                    require(u32(data, 4) == entries.size.toLong()) { "Invalid sample entry count" }
                    for (entry in entries) {
                        require(entry.type !in setOf("enca", "encv", "enct", "encs", "drms", "drmi")) { "Protected MP4 sample entry ${entry.type}" }
                        // Inspect native AudioSampleEntry extensions structurally, never by
                        // scanning opaque private bytes for strings such as 'sinf' or 'saio'.
                        require(entry.type in setOf("mp4a", "alac", "ac-3", "ec-3", "Opus", "fLaC", ".mp3", "mp3 ", "twos", "sowt", "lpcm", "raw ")) { "Unvalidated MP4 sample entry ${entry.type}" }
                        val body = entry.body
                        require(body.size >= 28)
                        val version = (body[8].toInt() and 255 shl 8) or (body[9].toInt() and 255)
                        val start = when (version) { 0 -> 28; 1 -> 44; 2 -> 64; else -> error("Unsupported AudioSampleEntry version") }
                        require(start <= body.size)
                        rejectPositionSensitive(children(body.copyOfRange(start, body.size)), depth + 1)
                    }
                }
            }
        }
    }
}
