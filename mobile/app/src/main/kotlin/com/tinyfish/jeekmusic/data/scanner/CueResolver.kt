package com.tinyfish.jeekmusic.data.scanner

import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.library.DocumentTree
import javax.inject.Inject

data class ResolvedCue(val sheet: CueSheet, val file: CueFile)

/**
 * 为整轨文件找到对应的 CUE（需求 CUE-1、CUE-5、CUE-8，docs/design.md §4.6）。
 */
class CueResolver @Inject constructor(private val tree: DocumentTree) {

    /** 只检查 FLAC / WAV，且时长至少 5 分钟（有内嵌 cuesheet 时不限时长），大部分单曲目录不需要额外的 SAF 调用。 */
    fun isCandidate(audio: MediaStoreAudio): Boolean =
        audio.fileName.substringAfterLast('.').lowercase() in CUE_AUDIO && audio.durationMs >= MIN_DURATION_MS

    /**
     * @param directoryCache 同一次扫描中按目录缓存的子文件列表
     */
    fun resolve(
        folder: LibraryFolderEntity,
        audio: MediaStoreAudio,
        embeddedCue: String?,
        directoryCache: MutableMap<String, List<DocumentTree.Child>>,
        explicitCuePaths: List<String> = emptyList(),
    ): ResolvedCue? {
        if (audio.fileName.substringAfterLast('.').lowercase() !in CUE_AUDIO) return null

        // 1. 内嵌 cuesheet
        embeddedCue?.takeIf { it.isNotBlank() }?.let { text ->
            val sheet = CueParser.parse(text)
            pick(sheet, audio.fileName, single = true)?.let { return it }
        }
        // LAN manifests bind a CUE to its exact root-relative dependency, including sibling folders.
        // This binding is persisted locally and reused by ordinary scans; no filename suffix guessing.
        for (cuePath in explicitCuePaths) {
            if (!cuePath.startsWith(folder.relativePath)) continue
            val id = com.tinyfish.jeekmusic.data.library.childDocumentId(DocumentTree.directoryDocumentId(folder, cuePath.substringBeforeLast('/', "") + "/"), cuePath.substringAfterLast('/'))
            val text = tree.readLimited(folder, id, 2_000_000)?.let(CharsetDetector::decode) ?: continue
            val sheet = CueParser.parse(text)
            if (sheet.isPerTrack) continue
            val wanted = audio.relativePath + audio.fileName
            val file = sheet.files.singleOrNull { file ->
                val parts = mutableListOf<String>()
                var safe = true
                for (part in (cuePath.substringBeforeLast('/', "") + "/" + file.name.replace('\\', '/')).split('/')) {
                    when (part) { "", "." -> Unit; ".." -> if (parts.isEmpty()) safe = false else parts.removeAt(parts.lastIndex); else -> parts += part }
                }
                safe && parts.joinToString("/").startsWith(folder.relativePath) && parts.joinToString("/").equals(wanted, ignoreCase = true)
            }
            if (file != null && file.tracks.size >= 2) return ResolvedCue(sheet, file)
        }
        if (audio.durationMs < MIN_DURATION_MS) return null

        // 2. 外部 .cue
        val children = directoryCache.getOrPut(audio.relativePath) { tree.list(folder, audio.relativePath) }
        val cues = children.filter { !it.isDirectory && it.name.endsWith(".cue", ignoreCase = true) }
        if (cues.isEmpty()) return null
        val stem = audio.fileName.substringBeforeLast('.')
        val images = children.count { !it.isDirectory && it.name.substringAfterLast('.').lowercase() in CUE_AUDIO }

        val byName = cues.firstOrNull { it.name.equals(audio.fileName + ".cue", ignoreCase = true) }
            ?: cues.firstOrNull { it.name.substringBeforeLast('.').equals(stem, ignoreCase = true) }
        if (byName != null) read(folder, byName)?.let { sheet -> pick(sheet, audio.fileName, single = true)?.let { return it } }

        // c. 任意 CUE 中 FILE 的文件名去掉扩展名后与音频同名（CUE 里写 .wav、实际是 .flac）
        // d. 目录中只有一个 CUE 和一个整轨文件时直接配对
        for (cue in cues) {
            if (cue == byName) continue
            val sheet = read(folder, cue) ?: continue
            pick(sheet, audio.fileName, single = cues.size == 1 && images == 1)?.let { return it }
        }
        return null
    }

    private fun read(folder: LibraryFolderEntity, cue: DocumentTree.Child): CueSheet? =
        tree.read(folder, cue.documentId)?.let { CueParser.parse(CharsetDetector.decode(it)) }

    /** 在 CUE 中找到与音频文件对应的 FILE 段；[single] 为 true 时，只有一个 FILE 段也算匹配。 */
    private fun pick(sheet: CueSheet, fileName: String, single: Boolean): ResolvedCue? {
        if (sheet.isPerTrack) return null
        val stem = fileName.substringBeforeLast('.')
        val file = sheet.files.firstOrNull { it.name.substringAfterLast('/').substringAfterLast('\\').equals(fileName, ignoreCase = true) }
            ?: sheet.files.firstOrNull { it.name.substringAfterLast('/').substringAfterLast('\\').substringBeforeLast('.').equals(stem, ignoreCase = true) }
            ?: sheet.files.singleOrNull()?.takeIf { single }
            ?: return null
        return if (file.tracks.size >= 2) ResolvedCue(sheet, file) else null
    }

    companion object {
        private val CUE_AUDIO = setOf("flac", "wav")
        private const val MIN_DURATION_MS = 5 * 60_000L
    }
}
