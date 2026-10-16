package com.tinyfish.jeekmusic.data.scanner

data class CueTrack(val number: Int, val title: String?, val performer: String?, val startMs: Long)

data class CueFile(val name: String, val tracks: List<CueTrack>)

data class CueSheet(
    val title: String?,
    val performer: String?,
    val year: Int?,
    val genre: String?,
    val files: List<CueFile>,
) {
    /**
     * 每个文件只有一首歌的"分轨 CUE"（需求 CUE-8）：这些文件本身就是单曲，忽略这个 CUE。
     */
    val isPerTrack: Boolean get() = files.size > 1 && files.all { it.tracks.size <= 1 }
}

/**
 * CUE 解析（docs/design.md §4.6）：
 * - 顶层 TITLE / PERFORMER → 专辑名 / 专辑艺术家，REM DATE / REM GENRE
 * - TRACK nn AUDIO 下的 TITLE、PERFORMER（缺省继承顶层）、INDEX 01 mm:ss:ff（每秒 75 帧）
 * - 忽略 INDEX 00：曲间间隙归入上一首，连续播放时不丢音频
 */
object CueParser {

    private val LINE = Regex("""^\s*(\S+)\s*(.*?)\s*$""")
    private val TIME = Regex("""^(\d+):(\d{1,2}):(\d{1,2})$""")

    fun parse(text: String): CueSheet {
        var title: String? = null
        var performer: String? = null
        var year: Int? = null
        var genre: String? = null
        val files = mutableListOf<CueFile>()

        var fileName: String? = null
        var tracks = mutableListOf<CueTrack>()
        var trackNumber: Int? = null
        var trackTitle: String? = null
        var trackPerformer: String? = null
        var trackStart: Long? = null

        fun endTrack() {
            val number = trackNumber ?: return
            val start = trackStart
            if (start != null) tracks += CueTrack(number, trackTitle, trackPerformer ?: performer, start)
            trackNumber = null
            trackTitle = null
            trackPerformer = null
            trackStart = null
        }

        fun endFile() {
            endTrack()
            fileName?.let { files += CueFile(it, tracks) }
            fileName = null
            tracks = mutableListOf()
        }

        for (raw in text.lineSequence()) {
            val match = LINE.find(raw) ?: continue
            val command = match.groupValues[1].uppercase()
            val args = match.groupValues[2]
            when (command) {
                "FILE" -> {
                    endFile()
                    fileName = parseFileName(args)
                }
                "TRACK" -> {
                    endTrack()
                    trackNumber = args.substringBefore(' ').toIntOrNull()
                }
                "TITLE" -> if (trackNumber != null) trackTitle = unquote(args) else title = unquote(args)
                "PERFORMER" -> if (trackNumber != null) trackPerformer = unquote(args) else performer = unquote(args)
                "INDEX" -> {
                    val parts = args.split(Regex("\\s+"))
                    if (parts.size == 2 && parts[0].toIntOrNull() == 1) trackStart = parseTime(parts[1])
                }
                "REM" -> {
                    val key = args.substringBefore(' ').uppercase()
                    val value = unquote(args.substringAfter(' ', ""))
                    when (key) {
                        "DATE" -> year = Regex("""\d{4}""").find(value)?.value?.toIntOrNull()
                        "GENRE" -> genre = value.ifEmpty { null }
                    }
                }
            }
        }
        endFile()
        return CueSheet(title?.ifEmpty { null }, performer?.ifEmpty { null }, year, genre, files)
    }

    /** "mm:ss:ff" → 毫秒，ff 是 1/75 秒的帧。 */
    internal fun parseTime(value: String): Long? {
        val m = TIME.find(value) ?: return null
        val (min, sec, frames) = m.destructured
        return min.toLong() * 60_000 + sec.toLong() * 1_000 + frames.toLong() * 1_000 / 75
    }

    /** FILE "name with spaces.wav" WAVE → name with spaces.wav */
    private fun parseFileName(args: String): String {
        val trimmed = args.trim()
        return if (trimmed.startsWith('"')) {
            trimmed.substring(1).substringBefore('"')
        } else {
            trimmed.substringBeforeLast(' ').ifEmpty { trimmed }
        }
    }

    private fun unquote(value: String): String = value.trim().removeSurrounding("\"").trim()
}
