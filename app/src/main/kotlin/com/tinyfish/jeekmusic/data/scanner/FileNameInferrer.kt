package com.tinyfish.jeekmusic.data.scanner

enum class FileNameFormat { ARTIST_TITLE, TITLE_ARTIST }

data class InferredName(val artist: String, val title: String)

/**
 * 艺术家标签为空时，从文件名推断艺术家和标题（需求 A-9，docs/design.md §4.5）。
 */
object FileNameInferrer {

    /** 开头的音轨号，例如 "01. "、"01 - "、"1_"、"03)"。 */
    private val TRACK_NUMBER = Regex("""^\d{1,3}\s*[.\-_)、]\s*""")

    /** 普通连字符两侧必须有空格（避免拆开 "Jay-Z"），破折号和全角连字符两侧空格可选。 */
    private val SEPARATOR = Regex("""\s+-\s+|\s*[–—－]\s*""")

    fun infer(fileName: String, format: FileNameFormat = FileNameFormat.ARTIST_TITLE): InferredName? {
        var base = fileName.substringBeforeLast('.', fileName)
        TRACK_NUMBER.find(base)?.let { match ->
            val stripped = base.substring(match.range.last + 1)
            // 去掉音轨号后没有分隔符，说明文件名只是"音轨号 - 标题"，推断不出艺术家
            if (!SEPARATOR.containsMatchIn(stripped)) return null
            base = stripped
        }
        val separator = SEPARATOR.find(base) ?: return null
        val left = base.substring(0, separator.range.first).trim()
        val right = base.substring(separator.range.last + 1).trim()
        if (left.isEmpty() || right.isEmpty()) return null
        return when (format) {
            FileNameFormat.ARTIST_TITLE -> InferredName(artist = left, title = right)
            FileNameFormat.TITLE_ARTIST -> InferredName(artist = right, title = left)
        }
    }
}
