package com.tinyfish.jeekmusic.data.scanner

import java.util.Locale

data class ArtistSplitConfig(
    val separators: Set<String> = DEFAULT_SEPARATORS,
    val exceptions: List<String> = DEFAULT_EXCEPTIONS,
) {
    companion object {
        /** 所有可选的分隔符，设置页按这个顺序展示。 */
        val ALL_SEPARATORS = listOf(";", "/", "、", "feat.", "ft.", "featuring", "&", ",", "x")

        /** 依据真实曲库统计确定的默认值（docs/design.md §4.4）。 */
        val DEFAULT_SEPARATORS = setOf(";", "/", "、", "feat.", "ft.", "featuring")

        val DEFAULT_EXCEPTIONS = listOf("AC/DC", "Earth, Wind & Fire", "Simon & Garfunkel")

        /** 用词语作分隔符时，两侧必须有空白，避免把 "Fitz" 中的 "ft" 拆开。 */
        internal val WORD_SEPARATORS = setOf("feat.", "ft.", "featuring", "x")
    }
}

/**
 * 把艺术家标签拆成多个艺术家（需求 A-2 ~ A-5，docs/design.md §4.4）。
 */
object ArtistSplitter {

    fun split(values: List<String>, config: ArtistSplitConfig = ArtistSplitConfig()): List<String> {
        val regex = buildRegex(config.separators)
        val result = LinkedHashMap<String, String>()
        for (value in values) {
            for (name in splitOne(value, regex, config.exceptions)) {
                result.putIfAbsent(nameKey(name), name)
            }
        }
        return result.values.toList()
    }

    /** 归一化的艺术家键：忽略大小写、首尾空格和连续空白（需求 A-5）。 */
    fun nameKey(name: String): String = name.trim().replace(WHITESPACE, " ").lowercase(Locale.ROOT)

    private fun splitOne(value: String, regex: Regex?, exceptions: List<String>): List<String> {
        // 先把例外名单里的名字替换成占位符，拆完再换回来
        var protected = value
        val placeholders = mutableListOf<String>()
        for (exception in exceptions) {
            if (exception.isBlank()) continue
            val pattern = Regex(Regex.escape(exception), RegexOption.IGNORE_CASE)
            protected = pattern.replace(protected) { match ->
                placeholders += match.value
                "\u0001${placeholders.size - 1}\u0001"
            }
        }
        // "A (feat. B)" → "A feat. B"
        protected = BRACKETED_FEAT.replace(protected) { " ${it.groupValues[1]} ${it.groupValues[2]}" }

        val parts = regex?.split(protected) ?: listOf(protected)
        return parts.map { part ->
            PLACEHOLDER.replace(part) { placeholders[it.groupValues[1].toInt()] }
                .trim()
                .trim('(', ')', '（', '）', '[', ']')
                .trim()
        }.filter { it.isNotEmpty() }
    }

    private fun buildRegex(separators: Set<String>): Regex? {
        val symbols = separators.filter { it !in ArtistSplitConfig.WORD_SEPARATORS }
        val words = separators.filter { it in ArtistSplitConfig.WORD_SEPARATORS }
        val alternatives = buildList {
            if (symbols.isNotEmpty()) add("\\s*(?:${symbols.joinToString("|") { Regex.escape(it) }})\\s*")
            if (words.isNotEmpty()) {
                val wordPattern = words.joinToString("|") { Regex.escape(it.removeSuffix(".")) + if (it.endsWith(".")) "\\.?" else "" }
                add("\\s+(?:$wordPattern)\\s+")
            }
        }
        if (alternatives.isEmpty()) return null
        return Regex(alternatives.joinToString("|"), RegexOption.IGNORE_CASE)
    }

    private val WHITESPACE = Regex("\\s+")
    private val PLACEHOLDER = Regex("\u0001(\\d+)\u0001")
    private val BRACKETED_FEAT = Regex("\\s*[(（\\[]\\s*(feat\\.?|ft\\.?|featuring)\\s+([^)）\\]]+)[)）\\]]", RegexOption.IGNORE_CASE)
}
