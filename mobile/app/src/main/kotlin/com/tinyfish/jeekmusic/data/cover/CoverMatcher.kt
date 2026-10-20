package com.tinyfish.jeekmusic.data.cover

import java.text.Normalizer
import java.util.Locale

/**
 * 判断在线搜索结果是不是这首歌（需求 C-3）。批量获取时没人把关，只接受歌名和艺术家都对得上的结果。
 *
 * 比较前统一大小写、全半角、繁简（[fold]），去掉标点空白和括号里的附注（Live、feat.、Remastered 等）。
 */
class CoverMatcher(private val fold: (String) -> String = { it }) {

    fun normalize(text: String): String {
        val folded = fold(Normalizer.normalize(text, Normalizer.Form.NFKC)).lowercase(Locale.ROOT)
        return folded.filter { it.isLetterOrDigit() }
    }

    /** 去掉括号里的附注和 " - Remastered" 之类的后缀后再比较。 */
    fun normalizeTitle(text: String): String {
        val stripped = text.replace(BRACKETS, " ").substringBefore(" - ").ifBlank { text }
        return normalize(stripped).ifEmpty { normalize(text) }
    }

    fun titleMatches(expected: String, actual: String): Boolean {
        val a = normalizeTitle(expected)
        val b = normalizeTitle(actual)
        if (a.isEmpty() || b.isEmpty()) return false
        // 不做前缀匹配："Baby" 和 "Baby Shark" 是两首歌
        return a == b
    }

    /** 歌曲的任意一位艺术家出现在结果的艺术家里（或反过来）就算对上。 */
    fun artistMatches(expected: String, actual: String): Boolean {
        val b = normalize(actual)
        if (b.isEmpty()) return false
        return splitArtists(expected).map(::normalize).filter { it.isNotEmpty() }.any { a ->
            a == b || (minOf(a.length, b.length) >= 2 && (b.contains(a) || a.contains(b)))
        }
    }

    companion object {
        private val BRACKETS = Regex("""[(\[（【「][^)\]）】」]*[)\]）】」]""")
        private val SEPARATORS = Regex("""\s*(?:[,，、/／&;；]|\bfeat\.?|\bft\.|\bvs\.?)\s*""", RegexOption.IGNORE_CASE)

        fun splitArtists(text: String): List<String> = text.split(SEPARATORS).map { it.trim() }.filter { it.isNotEmpty() }

        /** 搜索用的艺术家：只取第一位，多位艺术家放在一起时搜索引擎往往一个都找不到。 */
        fun primaryArtist(text: String): String = splitArtists(text).firstOrNull() ?: text.trim()

        fun hasCjk(text: String): Boolean = text.any { Character.UnicodeScript.of(it.code) == Character.UnicodeScript.HAN }
    }
}
