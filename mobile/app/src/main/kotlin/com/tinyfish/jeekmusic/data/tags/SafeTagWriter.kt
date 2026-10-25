package com.tinyfish.jeekmusic.data.tags

import java.io.ByteArrayOutputStream
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.security.MessageDigest

/** Null means leave the entire field alone. The text is never normalized or truncated. */
data class TagEdit(val cover: ByteArray? = null, val lyrics: String? = null)

/**
 * Bounded metadata parsing and lossless target edits. This deliberately does not use TagLib's
 * whole property/picture setters: those synchronize ID3v1 and re-encode retained descriptions.
 * All audio is streamed. Unsupported/ambiguous envelopes throw before the caller commits.
 */
internal object SafeTagWriter {
    fun write(file: File, edit: TagEdit, extension: String = file.extension) {
        if (edit.cover == null && edit.lyrics == null) return
        edit.lyrics?.let { utf8(it); require('\u0000' !in it) { "NUL in lyrics" } }
        val before = parse(file, edit, extension)
        val temporary = File.createTempFile(".jeek-tags-", ".tmp", file.parentFile)
        try {
            RandomAccessFile(file, "r").use { input ->
                temporary.outputStream().use { output ->
                    copy(input, output, 0, before.start)
                    output.write(before.replacement)
                    if (before.writeTail != null) before.writeTail.invoke(input, output)
                    else copy(input, output, before.end, input.length() - before.end)
                }
            }
            // Reparse the resulting native structure; independently compare ALL retained metadata
            // and raw audio/tail ranges, then decode and compare the exact requested target values.
            validate(file, temporary, edit, extension)
            RandomAccessFile(file, "rw").use { output ->
                temporary.inputStream().use { input ->
                    output.setLength(0)
                    val buffer = ByteArray(64 * 1024)
                    while (true) { val n = input.read(buffer); if (n < 0) break; output.write(buffer, 0, n) }
                }
                output.fd.sync()
            }
        } finally { temporary.delete() }
    }

    fun validate(original: File, candidate: File, edit: TagEdit, extension: String) {
        val before = parse(original, edit, extension)
        val after = parse(candidate, edit, extension)
        require(before.protected == after.protected) { "Non-target metadata/audio changed" }
        edit.cover?.let { require(after.coverCount == 1 && after.cover?.contentEquals(it) == true) { "Cover validation failed" } }
        edit.lyrics?.let { require(after.lyrics == it) { "Lyrics validation failed" } }
    }

    private fun parse(file: File, edit: TagEdit, extension: String = file.extension): TagLayout =
        RandomAccessFile(file, "r").use { input ->
            when (extension.lowercase()) {
                "mp3" -> Id3Tags.parse(input, edit)
                "flac" -> FlacTags.parse(input, edit)
                "ogg", "oga", "opus" -> OggTags.parse(input, edit)
                "m4a", "mp4" -> Mp4Tags.parse(input, edit)
                else -> error("No validated native tag writer for .$extension")
            }
        }

    fun sha256(file: File): String = file.inputStream().use { input ->
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(64 * 1024)
        while (true) { val n = input.read(buffer); if (n < 0) break; digest.update(buffer, 0, n) }
        digest.digest().hex()
    }
}

internal data class TagLayout(
    val start: Long,
    val end: Long,
    val replacement: ByteArray,
    val protected: List<String>,
    val cover: ByteArray?,
    val lyrics: String?,
    val writeTail: ((RandomAccessFile, java.io.OutputStream) -> Unit)? = null,
    val coverCount: Int = if (cover == null) 0 else 1,
)

internal const val MAX_METADATA = 32 * 1024 * 1024
internal fun ByteArray.hex() = joinToString("") { "%02x".format(it) }
internal fun hash(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(bytes).hex()
internal fun RandomAccessFile.bytes(start: Long, count: Long): ByteArray {
    require(count in 0..MAX_METADATA.toLong() && start >= 0 && start <= length() - count) { "Invalid metadata boundary" }
    seek(start)
    return ByteArray(count.toInt()).also(::readFully)
}
internal fun rangeHash(input: RandomAccessFile, start: Long, count: Long): String {
    require(start >= 0 && count >= 0 && start <= input.length() - count)
    val digest = MessageDigest.getInstance("SHA-256")
    input.seek(start)
    var remaining = count
    val buffer = ByteArray(64 * 1024)
    while (remaining > 0) {
        val n = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
        require(n > 0)
        digest.update(buffer, 0, n)
        remaining -= n
    }
    return digest.digest().hex()
}
internal fun copy(input: RandomAccessFile, output: java.io.OutputStream, start: Long, count: Long) {
    input.seek(start)
    var remaining = count
    val buffer = ByteArray(64 * 1024)
    while (remaining > 0) {
        val n = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
        require(n > 0)
        output.write(buffer, 0, n)
        remaining -= n
    }
}
internal fun ascii(value: String) = value.toByteArray(Charsets.ISO_8859_1)
internal fun utf8(value: String): ByteArray = Charsets.UTF_8.newEncoder()
    .onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
    .encode(java.nio.CharBuffer.wrap(value)).let { ByteArray(it.remaining()).also(it::get) }
internal fun decode(bytes: ByteArray, charset: java.nio.charset.Charset = Charsets.UTF_8): String = charset.newDecoder()
    .onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
    .decode(ByteBuffer.wrap(bytes)).toString()
internal fun be32(n: Int) = ByteBuffer.allocate(4).putInt(n).array()
internal fun le32(n: Int) = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(n).array()
internal fun u32(b: ByteArray, p: Int, little: Boolean = false): Long {
    require(p >= 0 && p <= b.size - 4)
    return ByteBuffer.wrap(b, p, 4).order(if (little) ByteOrder.LITTLE_ENDIAN else ByteOrder.BIG_ENDIAN).int.toLong() and 0xffffffffL
}
internal fun join(parts: List<ByteArray>): ByteArray {
    require(parts.sumOf { it.size.toLong() } <= MAX_METADATA) { "Metadata too large" }
    return ByteArrayOutputStream().apply { parts.forEach(::write) }.toByteArray()
}
internal fun syncSize(n: Int) = byteArrayOf((n shr 21 and 127).toByte(), (n shr 14 and 127).toByte(), (n shr 7 and 127).toByte(), (n and 127).toByte())
internal fun readSync(b: ByteArray, p: Int): Int {
    require(p >= 0 && p <= b.size - 4 && (p..p + 3).all { b[it].toInt() and 128 == 0 })
    return (0..3).fold(0) { n, i -> (n shl 7) or (b[p + i].toInt() and 127) }
}
