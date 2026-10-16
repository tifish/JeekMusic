package com.tinyfish.jeekmusic.data.scanner

/** MediaStore 中的卷名和相对路径，相对路径为空串或以 "/" 结尾。 */
data class VolumePath(val volumeName: String, val relativePath: String)

/**
 * 把 SAF 树 URI 的 documentId 转换为 MediaStore 查询条件（docs/design.md §4.1）。
 *
 * ExternalStorageProvider 的 documentId 形如：
 * - "primary:Music/Pop"  → 内部存储
 * - "1A2B-3C4D:Music"    → SD 卡 / U 盘（卷 UUID）
 * - "home:Music"         → 内部存储的 Documents 目录
 */
object SafPaths {

    private const val VOLUME_EXTERNAL_PRIMARY = "external_primary"

    fun fromTreeDocumentId(documentId: String): VolumePath? {
        val colon = documentId.indexOf(':')
        if (colon <= 0) return null
        val root = documentId.substring(0, colon)
        val path = documentId.substring(colon + 1).trim('/')

        return when {
            root == "primary" -> VolumePath(VOLUME_EXTERNAL_PRIMARY, asDirectory(path))
            root == "home" -> VolumePath(VOLUME_EXTERNAL_PRIMARY, asDirectory(join("Documents", path)))
            UUID_PATTERN.matches(root) -> VolumePath(root.lowercase(), asDirectory(path))
            // "raw:"、"msf:" 等其他 provider 的 id 无法映射到 MediaStore
            else -> null
        }
    }

    private fun join(parent: String, child: String) = if (child.isEmpty()) parent else "$parent/$child"

    private fun asDirectory(path: String) = if (path.isEmpty()) "" else "$path/"

    private val UUID_PATTERN = Regex("[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}")
}
