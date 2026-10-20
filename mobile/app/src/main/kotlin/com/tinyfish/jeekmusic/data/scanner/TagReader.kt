package com.tinyfish.jeekmusic.data.scanner

import android.content.Context
import android.net.Uri
import com.kyant.taglib.TagLib
import dagger.hilt.android.qualifiers.ApplicationContext
import java.nio.charset.Charset
import javax.inject.Inject

data class TrackTags(
    val title: String?,
    val artists: List<String>,
    val album: String?,
    val albumArtist: String?,
    val trackNo: Int?,
    val discNo: Int?,
    val year: Int?,
    val genre: String?,
    val hasLyrics: Boolean,
    val cueSheet: String?,
    val durationMs: Long?,
    val bitrate: Int?,
    val sampleRate: Int?,
)

/**
 * 用 TagLib 读取标签（docs/design.md §4.3）。读取失败时返回 null，调用方退回到文件名信息。
 */
class TagReader @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    fun read(uri: Uri, legacyCharset: Charset? = null): TrackTags? = runCatching {
        context.contentResolver.openFileDescriptor(uri, "r")?.use { pfd ->
            // TagLib 的 FileStream 会关闭传入的 fd，所以每次调用都传一个 dup 出来的副本
            val metadata = TagLib.getMetadata(pfd.dup().detachFd(), readPictures = false) ?: return null
            val properties = TagLib.getAudioProperties(pfd.dup().detachFd())
            val map = metadata.propertyMap

            fun first(key: String) = map[key]?.firstOrNull()?.let { CharsetFixer.fix(it, legacyCharset) }?.trim()?.takeIf { it.isNotEmpty() }
            fun all(key: String) = map[key]?.map { CharsetFixer.fix(it, legacyCharset).trim() }?.filter { it.isNotEmpty() }.orEmpty()

            TrackTags(
                title = first("TITLE"),
                artists = all("ARTIST"),
                album = first("ALBUM"),
                albumArtist = first("ALBUMARTIST"),
                trackNo = parseNumber(first("TRACKNUMBER")),
                discNo = parseNumber(first("DISCNUMBER")),
                year = first("DATE")?.let { YEAR.find(it)?.value?.toIntOrNull() },
                genre = first("GENRE"),
                hasLyrics = lyricsKeys(map.keys).any { !map[it].isNullOrEmpty() },
                cueSheet = map["CUESHEET"]?.firstOrNull(),
                durationMs = properties?.length?.toLong()?.takeIf { it > 0 },
                bitrate = properties?.bitrate?.takeIf { it > 0 },
                sampleRate = properties?.sampleRate?.takeIf { it > 0 },
            )
        }
    }.getOrNull()

    private fun parseNumber(value: String?): Int? = value?.substringBefore('/')?.trim()?.toIntOrNull()

    companion object {
        private val YEAR = Regex("""\d{4}""")

        /** 标准的是 LYRICS（ID3 USLT、Vorbis LYRICS、MP4 ©lyr）；有些工具写成自定义字段。 */
        val LYRICS_KEYS = listOf("LYRICS", "UNSYNCEDLYRICS", "UNSYNCED LYRICS", "USLT")

        /**
         * 标签里的歌词字段，按 [LYRICS_KEYS] 的顺序。带描述的 ID3 USLT 帧在属性表里是 "LYRICS:<描述>"
         * （TagLib 自己写入时描述就是 "LYRICS"），排在最后。
         */
        fun lyricsKeys(keys: Collection<String>): List<String> =
            LYRICS_KEYS.filter { it in keys } + keys.filter { it.startsWith("LYRICS:") }.sorted()
    }
}
