package com.tinyfish.jeekmusic.data.playlist

/**
 * m3u / m3u8 读写（需求 PL-4）。
 *
 * 导出：扩展 m3u（#EXTM3U + #EXTINF），路径用 /storage/<卷>/<相对路径> 形式的绝对路径，其他播放器也能识别。
 * 导入：路径可能是绝对路径、相对路径、Windows 路径，统一拆成路径段，交给调用方做后缀匹配。
 */
object M3u {

    private val BOM = Char(0xFEFF).toString()

    data class Entry(val path: String, val durationSec: Long, val title: String)

    fun format(entries: List<Entry>): String = buildString {
        append("#EXTM3U\n")
        for (e in entries) {
            append("#EXTINF:").append(e.durationSec).append(',').append(e.title.replace('\n', ' ')).append('\n')
            append(e.path).append('\n')
        }
    }

    /** 解析出每个条目的路径段（已去掉 file:// 前缀、统一为 "/" 分隔、去掉 "."、".."）。 */
    fun parse(text: String): List<List<String>> = text.lineSequence()
        .map { it.trim().removePrefix(BOM) }
        .filter { it.isNotEmpty() && !it.startsWith("#") }
        .map { line ->
            line.removePrefix("file://")
                .let { if (it.contains('%')) runCatching { java.net.URLDecoder.decode(it, "UTF-8") }.getOrDefault(it) else it }
                .replace('\\', '/')
                .split('/')
                .filter { it.isNotEmpty() && it != "." && it != ".." && !it.endsWith(":") }
        }
        .filter { it.isNotEmpty() }
        .toList()

    /** 卷名 → 文件系统中的挂载路径。 */
    fun storagePath(volumeName: String, relativePath: String, fileName: String): String {
        val root = if (volumeName == "external_primary") "/storage/emulated/0" else "/storage/${volumeName.uppercase()}"
        return "$root/$relativePath$fileName"
    }
}
