package com.tinyfish.jeekmusic.data.backup

/**
 * 备份中的歌曲引用 = 相对于所属音乐文件夹的路径（docs/design.md §3.1）。
 * 音乐文件夹换了卷或换了上级目录（比如拷到新手机、挪到 SD 卡），依然能对上。
 */
object BackupRefs {

    data class Root(val volumeName: String, val relativePath: String)

    /**
     * track_key（"volume:relative_path + file_name[#NN]"）→ 相对引用（"子目录/文件名[#NN]"）。
     * 找不到所属的音乐文件夹时返回 null。
     */
    fun refOf(trackKey: String, roots: List<Root>): String? {
        val volume = trackKey.substringBefore(':', "")
        val path = trackKey.substringAfter(':', "")
        if (volume.isEmpty()) return null
        val root = roots.filter { it.volumeName == volume && path.startsWith(it.relativePath) }
            .maxByOrNull { it.relativePath.length } ?: return null
        return path.removePrefix(root.relativePath)
    }

    /**
     * 恢复时的映射：先找完全相同的相对引用；找不到时按"后缀匹配"再找一次——
     * 路径段从右往左比对，只有唯一命中时才采用（文件夹层级在新设备上可能不同）。
     */
    class Matcher(refToTrackKey: Map<String, String>) {
        private val exact = refToTrackKey
        private val byFileName = refToTrackKey.entries.groupBy { it.key.substringAfterLast('/') }

        fun match(ref: String): String? {
            exact[ref]?.let { return it }
            val candidates = byFileName[ref.substringAfterLast('/')].orEmpty()
            if (candidates.size == 1) return candidates.single().value
            if (candidates.isEmpty()) return null
            // 同名文件有多个时，取共同后缀段最多且唯一的那个
            val segments = ref.split('/')
            val scored = candidates.map { it to commonSuffix(segments, it.key.split('/')) }
            val best = scored.maxOf { it.second }
            return scored.filter { it.second == best }.singleOrNull()?.first?.value
        }

        private fun commonSuffix(a: List<String>, b: List<String>): Int {
            var n = 0
            while (n < a.size && n < b.size && a[a.size - 1 - n] == b[b.size - 1 - n]) n++
            return n
        }
    }
}
