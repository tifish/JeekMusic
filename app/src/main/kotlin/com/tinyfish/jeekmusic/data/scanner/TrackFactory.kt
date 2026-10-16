package com.tinyfish.jeekmusic.data.scanner

import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.settings.ScanConfig

data class BuiltTrack(val entity: TrackEntity, val artists: List<String>)

/**
 * 根据 MediaStore 信息和标签计算一条 track 的全部派生字段：
 * 展示标题、展示艺术家、拆分后的艺术家列表、album_key（docs/design.md §4.2 第 6 步）。
 *
 * 纯函数，不访问数据库，方便单元测试；修改分隔符等设置后，也可以用库里已存的字段重新计算。
 */
class TrackFactory(private val config: ScanConfig) {

    fun build(audio: MediaStoreAudio, folderId: Long, tags: TrackTags?, existingId: Long = 0): BuiltTrack {
        val titleTag = tags?.title
        val rawArtists = tags?.artists.orEmpty()
        val derived = derive(audio.fileName, titleTag, rawArtists)
        val entity = TrackEntity(
            id = existingId,
            trackKey = audio.fileKey,
            fileKey = audio.fileKey,
            mediaStoreId = audio.mediaStoreId,
            volumeName = audio.volumeName,
            folderId = folderId,
            relativePath = audio.relativePath,
            fileName = audio.fileName,
            titleTag = titleTag,
            title = derived.title,
            album = tags?.album,
            albumArtist = tags?.albumArtist,
            albumKey = AlbumKeys.of(tags?.album, tags?.albumArtist, audio.volumeName, audio.relativePath),
            artistRaw = rawArtists.takeIf { it.isNotEmpty() }?.joinToString(MULTI_VALUE_SEPARATOR),
            artistDisplay = derived.artistDisplay,
            artistInferred = derived.inferred,
            trackNo = tags?.trackNo,
            discNo = tags?.discNo,
            year = tags?.year,
            genre = tags?.genre,
            durationMs = audio.durationMs.takeIf { it > 0 } ?: tags?.durationMs ?: 0,
            mimeType = audio.mimeType,
            bitrate = tags?.bitrate ?: audio.bitrate?.div(1000),
            sampleRate = tags?.sampleRate,
            dateModified = audio.dateModified,
            size = audio.size,
            hasEmbeddedLyrics = tags?.hasLyrics == true,
        )
        return BuiltTrack(entity, derived.artists)
    }

    /**
     * CUE 整轨：一个文件展开成多条虚拟歌曲（docs/design.md §4.6）。
     * 歌名、艺术家、音轨号来自 CUE；CUE 顶层 TITLE / PERFORMER 作为专辑名 / 专辑艺术家；缺少的字段取自文件本身的标签。
     */
    fun buildCue(audio: MediaStoreAudio, folderId: Long, tags: TrackTags?, cue: ResolvedCue, existingIds: Map<String, Long>): List<BuiltTrack> {
        val fileDuration = audio.durationMs.takeIf { it > 0 } ?: tags?.durationMs ?: 0
        val album = cue.sheet.title ?: tags?.album
        val albumArtist = cue.sheet.performer ?: tags?.albumArtist
        val tracks = cue.file.tracks.sortedBy { it.startMs }
        return tracks.mapIndexed { i, track ->
            val end = tracks.getOrNull(i + 1)?.startMs
            val trackKey = audio.fileKey + "#" + track.number.toString().padStart(2, '0')
            val rawArtists = listOfNotNull(track.performer).ifEmpty { tags?.artists.orEmpty() }
            val artists = ArtistSplitter.split(rawArtists, config.split)
            BuiltTrack(
                TrackEntity(
                    id = existingIds[trackKey] ?: 0,
                    trackKey = trackKey,
                    fileKey = audio.fileKey,
                    mediaStoreId = audio.mediaStoreId,
                    volumeName = audio.volumeName,
                    folderId = folderId,
                    relativePath = audio.relativePath,
                    fileName = audio.fileName,
                    titleTag = track.title,
                    title = track.title ?: (track.number.toString().padStart(2, '0') + ". " + (album ?: audio.fileName.substringBeforeLast('.'))),
                    album = album,
                    albumArtist = albumArtist,
                    albumKey = AlbumKeys.of(album, albumArtist, audio.volumeName, audio.relativePath),
                    artistRaw = rawArtists.takeIf { it.isNotEmpty() }?.joinToString(MULTI_VALUE_SEPARATOR),
                    artistDisplay = rawArtists.joinToString(" / "),
                    artistInferred = false,
                    trackNo = track.number,
                    discNo = tags?.discNo,
                    year = cue.sheet.year ?: tags?.year,
                    genre = cue.sheet.genre ?: tags?.genre,
                    durationMs = ((end ?: fileDuration) - track.startMs).coerceAtLeast(0),
                    mimeType = audio.mimeType,
                    bitrate = tags?.bitrate ?: audio.bitrate?.div(1000),
                    sampleRate = tags?.sampleRate,
                    dateModified = audio.dateModified,
                    size = audio.size,
                    hasEmbeddedLyrics = false,
                    cueTrackNo = track.number,
                    startMs = track.startMs,
                    endMs = end,
                ),
                artists,
            )
        }
    }

    /** 用库里已存的原始字段重新计算派生字段（修改分隔符、例外名单或文件名推断设置后）。 */
    fun rebuild(track: TrackEntity): BuiltTrack {
        val rawArtists = track.artistRaw?.split(MULTI_VALUE_SEPARATOR).orEmpty()
        val derived = derive(track.fileName, track.titleTag, rawArtists)
        return BuiltTrack(
            track.copy(title = derived.title, artistDisplay = derived.artistDisplay, artistInferred = derived.inferred),
            derived.artists,
        )
    }

    private data class Derived(val title: String, val artistDisplay: String, val artists: List<String>, val inferred: Boolean)

    private fun derive(fileName: String, titleTag: String?, rawArtists: List<String>): Derived {
        val inferred = if (rawArtists.isEmpty() && config.inferFromFileName) {
            FileNameInferrer.infer(fileName, config.fileNameFormat)
        } else {
            null
        }
        val artists = when {
            rawArtists.isNotEmpty() -> ArtistSplitter.split(rawArtists, config.split)
            // 文件名里不能出现 "/"，网易云等用 "," 分隔多个艺术家，所以推断出的艺术家额外按逗号拆
            inferred != null -> ArtistSplitter.split(listOf(inferred.artist), config.split.copy(separators = config.split.separators + ","))
            else -> emptyList()
        }
        return Derived(
            title = titleTag ?: inferred?.title ?: fileName.substringBeforeLast('.', fileName),
            artistDisplay = when {
                rawArtists.isNotEmpty() -> rawArtists.joinToString(" / ")
                inferred != null -> inferred.artist
                else -> ""
            },
            artists = artists,
            inferred = rawArtists.isEmpty() && inferred != null,
        )
    }

    companion object {
        const val MULTI_VALUE_SEPARATOR = "\u0000"
    }
}
