package com.tinyfish.jeekmusic.data.online

import android.icu.text.Transliterator
import com.tinyfish.jeekmusic.data.cover.CoverMatcher

/** 繁简转换（ICU）：比较在线结果时统一转成简体；iTunes 的中文只有繁体能搜到。 */
object ChineseText {
    private val simplified: Transliterator by lazy { Transliterator.getInstance("Traditional-Simplified") }
    private val traditional: Transliterator by lazy { Transliterator.getInstance("Simplified-Traditional") }

    // Transliterator 不保证线程安全
    fun toSimplified(text: String): String =
        if (CoverMatcher.hasCjk(text)) synchronized(simplified) { simplified.transliterate(text) } else text

    fun toTraditional(text: String): String = synchronized(traditional) { traditional.transliterate(text) }

    /** 比较在线结果用的匹配器。 */
    val matcher by lazy { CoverMatcher(::toSimplified) }
}
