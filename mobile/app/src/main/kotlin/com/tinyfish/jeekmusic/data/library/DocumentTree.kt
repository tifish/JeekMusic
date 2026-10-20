package com.tinyfish.jeekmusic.data.library

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 通过用户授权的 SAF 树访问音乐目录中的非媒体文件：.cue、.lrc、封面图片、备份（docs/design.md §4.1）。
 * ExternalStorageProvider 的 documentId 基于路径，所以可以直接拼出子文件的 id，不用逐级查询。
 */
@Singleton
class DocumentTree @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    data class Child(val documentId: String, val name: String, val isDirectory: Boolean)

    internal val resolver get() = context.contentResolver

    fun list(folder: LibraryFolderEntity, relativePath: String): List<Child> = runCatching {
        val treeUri = Uri.parse(folder.treeUri)
        val children = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, directoryDocumentId(folder, relativePath))
        context.contentResolver.query(
            children,
            arrayOf(
                DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                DocumentsContract.Document.COLUMN_MIME_TYPE,
            ),
            null, null, null,
        )?.use { c ->
            buildList {
                while (c.moveToNext()) {
                    add(Child(c.getString(0), c.getString(1), c.getString(2) == DocumentsContract.Document.MIME_TYPE_DIR))
                }
            }
        }.orEmpty()
    }.getOrDefault(emptyList())

    fun read(folder: LibraryFolderEntity, documentId: String): ByteArray? = runCatching {
        context.contentResolver.openInputStream(documentUri(folder, documentId))?.use { it.readBytes() }
    }.getOrNull()

    fun documentUri(folder: LibraryFolderEntity, documentId: String): Uri =
        DocumentsContract.buildDocumentUriUsingTree(Uri.parse(folder.treeUri), documentId)

    companion object {
        /** App 在每个音乐文件夹根下的目录：备份、在线封面、在线歌词。 */
        const val APP_DIR = ".jeekmusic"

        /** 音乐文件夹根下 `.jeekmusic/<name>/` 的相对路径，用于 [list]。 */
        fun appPath(folder: LibraryFolderEntity, name: String) = "${folder.relativePath}$APP_DIR/$name/"

        /** 歌曲所在目录在 SAF 树中的 documentId。 */
        fun directoryDocumentId(folder: LibraryFolderEntity, relativePath: String): String {
            val rest = relativePath.removePrefix(folder.relativePath).trimEnd('/')
            return when {
                rest.isEmpty() -> folder.treeDocumentId
                folder.treeDocumentId.endsWith(":") -> folder.treeDocumentId + rest
                else -> "${folder.treeDocumentId.trimEnd('/')}/$rest"
            }
        }
    }
}

/** 写入相关的操作单独放在扩展里：只有歌词、封面和备份会写音乐目录（需求 §4.2 文件安全）。 */
fun DocumentTree.findChild(folder: LibraryFolderEntity, parentDocumentId: String, name: String): DocumentTree.Child? =
    listById(folder, parentDocumentId).firstOrNull { it.name == name }

fun DocumentTree.listById(folder: LibraryFolderEntity, parentDocumentId: String): List<DocumentTree.Child> = runCatching {
    val children = DocumentsContract.buildChildDocumentsUriUsingTree(Uri.parse(folder.treeUri), parentDocumentId)
    resolver.query(
        children,
        arrayOf(
            DocumentsContract.Document.COLUMN_DOCUMENT_ID,
            DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE,
        ),
        null, null, null,
    )?.use { c ->
        buildList { while (c.moveToNext()) add(DocumentTree.Child(c.getString(0), c.getString(1), c.getString(2) == DocumentsContract.Document.MIME_TYPE_DIR)) }
    }.orEmpty()
}.getOrDefault(emptyList())

/** 新建文件或目录，返回新文档的 documentId 和实际文件名（DocumentsProvider 可能自动改名）。 */
fun DocumentTree.create(folder: LibraryFolderEntity, parentDocumentId: String, mimeType: String, name: String): DocumentTree.Child? = runCatching {
    val parent = documentUri(folder, parentDocumentId)
    val uri = DocumentsContract.createDocument(resolver, parent, mimeType, name) ?: return null
    val id = DocumentsContract.getDocumentId(uri)
    val actualName = resolver.query(uri, arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME), null, null, null)
        ?.use { if (it.moveToFirst()) it.getString(0) else null } ?: name
    DocumentTree.Child(id, actualName, mimeType == DocumentsContract.Document.MIME_TYPE_DIR)
}.getOrNull()

fun DocumentTree.write(folder: LibraryFolderEntity, documentId: String, bytes: ByteArray): Boolean = runCatching {
    resolver.openOutputStream(documentUri(folder, documentId), "wt")?.use { it.write(bytes) } != null
}.getOrDefault(false)

fun DocumentTree.delete(folder: LibraryFolderEntity, documentId: String): Boolean =
    runCatching { DocumentsContract.deleteDocument(resolver, documentUri(folder, documentId)) }.getOrDefault(false)

/** 音乐文件夹根下 `.jeekmusic/<name>/` 的 documentId，没有时创建；失败返回 null。 */
fun DocumentTree.appDirectory(folder: LibraryFolderEntity, name: String): String? {
    val app = findChild(folder, folder.treeDocumentId, DocumentTree.APP_DIR)
        ?: create(folder, folder.treeDocumentId, DocumentsContract.Document.MIME_TYPE_DIR, DocumentTree.APP_DIR)
        ?: return null
    val dir = findChild(folder, app.documentId, name)
        ?: create(folder, app.documentId, DocumentsContract.Document.MIME_TYPE_DIR, name)
        ?: return null
    return dir.documentId
}

/**
 * 写入文件，已存在时覆盖。新建时 mime 用 application/octet-stream：
 * 部分 DocumentsProvider 会按 mime 自作主张地改扩展名（比如给 .lrc 追加 .txt）。
 */
fun DocumentTree.writeFile(folder: LibraryFolderEntity, dirId: String, name: String, bytes: ByteArray): Boolean {
    findChild(folder, dirId, name)?.let { return write(folder, it.documentId, bytes) }
    val created = create(folder, dirId, "application/octet-stream", name) ?: return false
    if (created.name != name || !write(folder, created.documentId, bytes)) {
        delete(folder, created.documentId)
        return false
    }
    return true
}

/** 同一目录下文件的 documentId（ExternalStorageProvider 的 id 基于路径）。 */
fun childDocumentId(dirId: String, name: String) = if (dirId.endsWith(":")) dirId + name else "$dirId/$name"

fun DocumentTree.rename(folder: LibraryFolderEntity, documentId: String, newName: String): Boolean =
    runCatching { DocumentsContract.renameDocument(resolver, documentUri(folder, documentId), newName) != null }.getOrDefault(false)
