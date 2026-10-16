package com.tinyfish.jeekmusic.data.scanner

import java.util.Locale

/**
 * 专辑归属键（需求 AL-4，docs/design.md §5.4）：
 * - 有专辑艺术家：专辑名 + 专辑艺术家
 * - 没有专辑艺术家：专辑名 + 所在目录（CD1/CD2 这类碟片目录取上一级）
 */
object AlbumKeys {

    private val DISC_DIRECTORY = Regex("""^(cd|disc|disk)\s*\d+$""", RegexOption.IGNORE_CASE)

    fun of(album: String?, albumArtist: String?, volumeName: String, relativePath: String): String? {
        val name = album?.trim().orEmpty()
        if (name.isEmpty()) return null
        val artist = albumArtist?.trim().orEmpty()
        return if (artist.isNotEmpty()) {
            "a:${norm(name)}|${norm(artist)}"
        } else {
            "d:${norm(name)}|$volumeName:${albumDirectory(relativePath)}"
        }
    }

    internal fun albumDirectory(relativePath: String): String {
        val segments = relativePath.trimEnd('/').split('/')
        return if (segments.size > 1 && DISC_DIRECTORY.matches(segments.last())) {
            segments.dropLast(1).joinToString("/", postfix = "/")
        } else {
            relativePath
        }
    }

    private fun norm(s: String) = s.lowercase(Locale.ROOT)
}
