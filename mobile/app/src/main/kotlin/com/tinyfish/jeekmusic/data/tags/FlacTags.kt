package com.tinyfish.jeekmusic.data.tags

import java.io.RandomAccessFile

internal object FlacTags {
    private data class Block(val type: Int, val data: ByteArray)
    fun parse(input: RandomAccessFile, edit: TagEdit): TagLayout {
        require(input.bytes(0, 4).contentEquals(ascii("fLaC"))) { "Not a native FLAC envelope" }
        var p = 4L
        var last = false
        val blocks = ArrayList<Block>()
        while (!last) {
            val header = input.bytes(p, 4); p += 4
            last = header[0].toInt() and 128 != 0
            val type = header[0].toInt() and 127
            require(type < 127)
            val n = (header[1].toInt() and 255 shl 16) or (header[2].toInt() and 255 shl 8) or (header[3].toInt() and 255)
            require(p + n <= MAX_METADATA) { "FLAC metadata too large" }
            blocks += Block(type, input.bytes(p, n.toLong())); p += n
        }
        require(blocks.first().type == 0 && blocks.first().data.size == 34 && blocks.count { it.type == 0 } == 1)
        require(blocks.count { it.type == 4 } <= 1) { "Multiple comment blocks" }
        val output = ArrayList<Block>()
        val protected = arrayListOf("FLAC")
        var cover: ByteArray? = null
        var coverCount = 0
        var lyrics: String? = null
        for (block in blocks) {
            when {
                block.type == 6 && edit.cover != null && NativePicture.type(block.data) in listOf(0, 3) -> { cover = NativePicture.image(block.data); coverCount++ }
                block.type == 4 && edit.lyrics != null -> {
                    val comments = NativeComments.edit(block.data, TagEdit(lyrics = edit.lyrics), false)
                    output += Block(4, comments.bytes)
                    protected += comments.protected.map { "comment:$it" }
                    lyrics = comments.lyrics
                }
                else -> { output += block; protected += "${block.type}:${hash(block.data)}" }
            }
        }
        if (edit.lyrics != null && blocks.none { it.type == 4 }) {
            val comments = NativeComments.edit(NativeComments.empty(), TagEdit(lyrics = edit.lyrics), false)
            output += Block(4, comments.bytes)
            protected += comments.protected.map { "comment:$it" }
        }
        edit.cover?.let { output += Block(6, NativePicture.create(it)) }
        val rendered = join(listOf(ascii("fLaC")) + output.mapIndexed { i, block ->
            require(block.data.size <= 0xffffff)
            byteArrayOf((block.type or if (i == output.lastIndex) 128 else 0).toByte(), (block.data.size shr 16).toByte(), (block.data.size shr 8).toByte(), block.data.size.toByte()) + block.data
        })
        // Metadata blocks can move; compare retained blocks in sequence with the comment's
        // identity separately so adding a previously absent comment block is harmless.
        val stable = protected.filterNot { it.startsWith("comment:") } + protected.filter { it.startsWith("comment:") }
        require(p < input.length())
        return TagLayout(0, p, rendered, stable + rangeHash(input, p, input.length() - p), cover, lyrics, coverCount = coverCount)
    }
}
