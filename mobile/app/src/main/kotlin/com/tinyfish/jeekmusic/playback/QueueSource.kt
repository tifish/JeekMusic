package com.tinyfish.jeekmusic.playback

/**
 * 播放队列来自哪个列表（需求 P-15）。筛选、排序或曲库变了，播放服务按它重新生成队列里来自列表的部分。
 * [encode] / [decode] 用于在界面和播放服务之间传递、以及断点续播时保存。
 */
sealed interface QueueSource {
    /** 文件夹；[recursive] 为 true 时包括子文件夹（"播放全部（含子文件夹）"）。 */
    data class Folder(val folderId: Long, val relativePath: String, val recursive: Boolean) : QueueSource

    data class Artist(val nameKey: String) : QueueSource

    data class Album(val albumKey: String) : QueueSource

    data class Playlist(val playlistId: Long) : QueueSource

    data class Search(val query: String) : QueueSource

    fun encode(): String = when (this) {
        is Folder -> listOf("folder", folderId.toString(), relativePath, recursive.toString())
        is Artist -> listOf("artist", nameKey)
        is Album -> listOf("album", albumKey)
        is Playlist -> listOf("playlist", playlistId.toString())
        is Search -> listOf("search", query)
    }.joinToString(SEPARATOR)

    companion object {
        private const val SEPARATOR = "\u001F"

        fun decode(value: String?): QueueSource? {
            val parts = value?.split(SEPARATOR) ?: return null
            return runCatching {
                when (parts[0]) {
                    "folder" -> Folder(parts[1].toLong(), parts[2], parts[3].toBoolean())
                    "artist" -> Artist(parts[1])
                    "album" -> Album(parts[1])
                    "playlist" -> Playlist(parts[1].toLong())
                    "search" -> Search(parts[1])
                    else -> null
                }
            }.getOrNull()
        }
    }
}
