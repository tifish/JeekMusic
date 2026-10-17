package com.tinyfish.jeekmusic.data.cover

import android.content.Context
import dagger.hilt.android.qualifiers.ApplicationContext
import java.io.File
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 最近在线找不到封面的专辑（没有专辑时是歌曲），30 天内自动获取不再查（需求 C-3）。
 * 批量获取和播放时获取共用；文件每行"时间\t键"。
 */
@Singleton
class CoverMissLog @Inject constructor(@param:ApplicationContext context: Context) {
    private val file = File(context.filesDir, "cover_misses.tsv")

    private val entries: MutableMap<String, Long> by lazy {
        runCatching {
            file.readLines().mapNotNull { line ->
                val (time, key) = line.split('\t', limit = 2).takeIf { it.size == 2 } ?: return@mapNotNull null
                time.toLongOrNull()?.let { key to it }
            }.toMap(HashMap())
        }.getOrDefault(HashMap())
    }

    @Synchronized
    fun recent(key: String): Boolean = entries[key]?.let { System.currentTimeMillis() - it < RETRY_AFTER_MS } == true

    @Synchronized
    fun add(key: String) {
        entries[key] = System.currentTimeMillis()
    }

    /** 写回文件，顺便去掉过期的记录。 */
    @Synchronized
    fun save() {
        val now = System.currentTimeMillis()
        runCatching { file.writeText(entries.filter { now - it.value < RETRY_AFTER_MS }.entries.joinToString("") { "${it.value}\t${it.key}\n" }) }
    }

    private companion object {
        const val RETRY_AFTER_MS = 30L * 24 * 3600 * 1000
    }
}
