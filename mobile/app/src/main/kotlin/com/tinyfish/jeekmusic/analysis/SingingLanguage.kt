package com.tinyfish.jeekmusic.analysis

/** Actual vocal languages; unknown and instrumental never match a negative filter. */
data class SingingLanguage(val state: String, val languages: List<String>, val source: String = "AUTO", val confidence: Double? = null) {
    fun matches(filter: LanguageFilter): Boolean = state == "known" && when (filter) {
        LanguageFilter.CHINESE -> languages.any { it == "zh" || it == "yue" }
        LanguageFilter.NON_CHINESE -> languages.none { it == "zh" || it == "yue" }
        LanguageFilter.ENGLISH -> "en" in languages
        LanguageFilter.NON_ENGLISH -> "en" !in languages
    }
    companion object {
        fun manual(value: String): SingingLanguage = when (value) {
            "CHINESE" -> SingingLanguage("known", listOf("zh"), "MANUAL")
            "ENGLISH" -> SingingLanguage("known", listOf("en"), "MANUAL")
            "CHINESE_ENGLISH" -> SingingLanguage("known", listOf("zh", "en"), "MANUAL")
            "OTHER" -> SingingLanguage("known", listOf("other"), "MANUAL")
            "INSTRUMENTAL" -> SingingLanguage("instrumental", emptyList(), "MANUAL")
            "UNKNOWN" -> SingingLanguage("unknown", emptyList(), "MANUAL")
            else -> error("Invalid vocal language")
        }
        fun windows(start: Long, duration: Long): List<LongRange> {
            if (duration < 3000) return emptyList()
            val size = minOf(20_000L, duration)
            val result = mutableListOf<LongRange>()
            for (fraction in listOf(.2, .5, .8)) {
                val from = maxOf(start + ((duration - size) * fraction).toLong(), result.lastOrNull()?.let { it.last + 1 } ?: start)
                if (from + size <= start + duration) result += from until from + size
            }
            return result
        }
        fun aggregate(samples: List<LanguageSample>): SingingLanguage {
            val supported = samples.filter { it.speechConfirmed && it.language != null && it.probability?.let { p -> p.isFinite() && p >= .6 } == true }
            val languages = supported.groupBy { it.language!! }.filterValues { it.size >= 2 || it.maxOf { s -> s.probability!! } >= .9 }.keys.sorted()
            val instrumental = samples.size >= 2 && samples.all { it.vocalScore?.let { v -> v < TagClassifier.VOCAL_THRESHOLD } == true }
            return SingingLanguage(if (languages.isNotEmpty()) "known" else if (instrumental) "instrumental" else "unknown", languages,
                confidence = supported.filter { it.language in languages }.minOfOrNull { it.probability!! })
        }
    }
}
enum class LanguageFilter { CHINESE, NON_CHINESE, ENGLISH, NON_ENGLISH;
    val chineseGroup: Boolean get() = this == CHINESE || this == NON_CHINESE
}
data class LanguageSample(val vocalScore: Double?, val language: String? = null, val probability: Double? = null, val speechConfirmed: Boolean = false)
