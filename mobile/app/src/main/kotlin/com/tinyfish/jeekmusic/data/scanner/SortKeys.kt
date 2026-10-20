package com.tinyfish.jeekmusic.data.scanner

import android.icu.text.Transliterator
import java.util.Locale

/**
 * 艺术家排序键和字母索引：中文转拼音后按 A–Z 排序（需求 A-1）。
 *
 * [transliterate] 在 Android 上用 ICU 的 "Han-Latin; Latin-ASCII"，单元测试里可以替换成假实现。
 */
class SortKeys(private val transliterate: (String) -> String) {

    fun sortKey(name: String): String =
        transliterate(name.trim()).lowercase(Locale.ROOT).replace(WHITESPACE, " ").trim()

    fun indexLetter(sortKey: String): String {
        val first = sortKey.firstOrNull()?.uppercaseChar() ?: return "#"
        return if (first in 'A'..'Z') first.toString() else "#"
    }

    companion object {
        private val WHITESPACE = Regex("\\s+")

        /** 创建 Transliterator 较慢且它不是线程安全的，所以复用一个实例并加锁。 */
        fun android(): SortKeys {
            val transliterator = Transliterator.getInstance("Han-Latin; Latin-ASCII")
            return SortKeys { text -> synchronized(transliterator) { transliterator.transliterate(text) } }
        }
    }
}
