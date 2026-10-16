package com.tinyfish.jeekmusic.data.lyrics

import androidx.compose.runtime.Immutable

@Immutable
data class LyricLine(val timeMs: Long, val text: String, val translation: String? = null)

/** [synced] 为 false 时是没有时间轴的纯文本歌词（需求 LY-4）。 */
@Immutable
data class Lyrics(val lines: List<LyricLine>, val synced: Boolean)

/**
 * LRC 解析（docs/design.md §8.2）：
 * - `[mm:ss.xx]`、`[mm:ss.xxx]`、`[mm:ss]`，一行多个时间戳
 * - `[offset:+/-ms]`；`[ti:]`、`[ar:]` 等元信息忽略
 * - 同一时间戳出现两行时合并为原文 + 翻译（需求 LY-5）
 * - 逐字歌词（Enhanced LRC `<mm:ss.xx>`）去掉逐字时间标记，按行显示
 */
object LrcParser {

    private val TIME_TAG = Regex("""\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?]""")
    private val META_TAG = Regex("""^\[([a-zA-Z#]+):(.*)]$""")
    private val WORD_TAG = Regex("""<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>""")

    fun parse(text: String): Lyrics {
        var offset = 0L
        val timed = mutableListOf<Pair<Long, String>>()
        val plain = mutableListOf<String>()

        for (raw in text.lineSequence()) {
            val line = raw.trim().removePrefix("﻿")
            if (line.isEmpty()) {
                plain += ""
                continue
            }
            // 元信息标签（字母开头，时间标签是数字开头，不会匹配）
            val meta = META_TAG.matchEntire(line)
            if (meta != null) {
                if (meta.groupValues[1].equals("offset", ignoreCase = true)) {
                    offset = meta.groupValues[2].trim().removePrefix("+").toLongOrNull() ?: 0
                }
                continue
            }

            var rest = line
            val times = mutableListOf<Long>()
            while (true) {
                val match = TIME_TAG.find(rest) ?: break
                if (match.range.first != 0) break
                times += toMillis(match)
                rest = rest.substring(match.range.last + 1)
            }
            val content = WORD_TAG.replace(rest, "").trim()
            if (times.isEmpty()) {
                plain += content
            } else {
                times.forEach { timed += it to content }
            }
        }

        if (timed.isEmpty()) {
            return Lyrics(plain.dropWhile { it.isEmpty() }.dropLastWhile { it.isEmpty() }.map { LyricLine(0, it) }, synced = false)
        }

        // 同一时间戳的多行：第一行为原文，其余为翻译。按时间稳定排序，保留原文在前
        val lines = timed
            .withIndex()
            .sortedWith(compareBy({ it.value.first }, { it.index }))
            .groupBy { it.value.first }
            .map { (time, group) ->
                val texts = group.map { it.value.second }
                val translation = texts.drop(1).filter { it.isNotEmpty() }.joinToString(" / ").ifEmpty { null }
                // [offset] 为正时歌词提前显示（LRC 约定）
                LyricLine((time - offset).coerceAtLeast(0), texts.first(), translation)
            }
        return Lyrics(lines, synced = true)
    }

    private fun toMillis(match: MatchResult): Long {
        val minutes = match.groupValues[1].toLong()
        val seconds = match.groupValues[2].toLong()
        val fraction = match.groupValues[3]
        val ms = when (fraction.length) {
            0 -> 0L
            1 -> fraction.toLong() * 100
            2 -> fraction.toLong() * 10
            else -> fraction.take(3).toLong()
        }
        return minutes * 60_000 + seconds * 1_000 + ms
    }

    /** 当前应高亮的行：最后一个时间 ≤ 播放进度的行；还没到第一行时返回 -1。二分查找。 */
    fun currentIndex(lines: List<LyricLine>, positionMs: Long): Int {
        var low = 0
        var high = lines.lastIndex
        var result = -1
        while (low <= high) {
            val mid = (low + high) ushr 1
            if (lines[mid].timeMs <= positionMs) {
                result = mid
                low = mid + 1
            } else {
                high = mid - 1
            }
        }
        return result
    }
}
