package com.tinyfish.jeekmusic.data.library

import com.tinyfish.jeekmusic.data.db.DirectoryCount
import com.tinyfish.jeekmusic.util.NaturalOrderComparator

data class FolderNode(val name: String, val relativePath: String, val trackCount: Int)

/**
 * 根据歌曲的 relative_path 在内存中折叠出直接子文件夹（docs/design.md §5.2），
 * 不扫描文件系统，所以空文件夹自然不会出现（需求 F-3）。
 */
object FolderTree {

    fun children(path: String, counts: List<DirectoryCount>): List<FolderNode> {
        val byChild = LinkedHashMap<String, Int>()
        for (count in counts) {
            if (!count.relativePath.startsWith(path) || count.relativePath == path) continue
            val childName = count.relativePath.substring(path.length).substringBefore('/')
            if (childName.isEmpty()) continue
            byChild[childName] = (byChild[childName] ?: 0) + count.count
        }
        return byChild.entries
            .map { (name, total) -> FolderNode(name, "$path$name/", total) }
            .sortedWith(compareBy(NaturalOrderComparator) { it.name })
    }
}
