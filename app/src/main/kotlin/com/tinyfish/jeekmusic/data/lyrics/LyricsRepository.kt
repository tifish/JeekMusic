package com.tinyfish.jeekmusic.data.lyrics

import android.content.Context
import android.util.LruCache
import com.kyant.taglib.TagLib
import com.tinyfish.jeekmusic.data.db.FolderDao
import com.tinyfish.jeekmusic.data.db.LyricsDao
import com.tinyfish.jeekmusic.data.db.LyricsOffsetEntity
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.library.DocumentTree
import com.tinyfish.jeekmusic.data.scanner.CharsetDetector
import com.tinyfish.jeekmusic.data.scanner.CharsetFixer
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource
import com.tinyfish.jeekmusic.data.scanner.TagReader
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 歌词来源与优先级（docs/design.md §8.1）：
 * 1. 同目录同名 .lrc（通过 SAF 树 URI 查找，大小写不敏感）
 * 2. 内嵌歌词
 * 3. lyrics_cache（在线获取了但没能写回音乐目录的）
 */
@Singleton
class LyricsRepository @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val folderDao: FolderDao,
    private val lyricsDao: LyricsDao,
    private val tree: DocumentTree,
) {
    /** 按目录缓存子文件列表；写入 .lrc 后要使对应目录失效。 */
    private val directoryCache = LruCache<String, List<DocumentTree.Child>>(32)

    suspend fun load(track: TrackEntity): Lyrics? = withContext(Dispatchers.IO) {
        lrcFile(track)?.let { return@withContext it }
        if (track.cueTrackNo == null) embedded(track)?.let { return@withContext it }
        lyricsDao.cached(track.trackKey)?.content?.let { LrcParser.parse(it) }
    }

    /**
     * 候选文件名（需求 LY-1、CUE-7）：
     * - 普通歌曲：<文件名去扩展名>.lrc
     * - CUE 虚拟歌曲：<音轨号>. <歌名>.lrc，其次 <歌名>.lrc
     */
    fun lrcNames(track: TrackEntity): List<String> = if (track.cueTrackNo != null) {
        val title = sanitize(track.titleTag ?: track.title)
        listOf("%02d. %s.lrc".format(track.cueTrackNo, title), "$title.lrc")
    } else {
        listOf(track.fileName.substringBeforeLast('.') + ".lrc")
    }

    private suspend fun lrcFile(track: TrackEntity): Lyrics? {
        val folder = folderDao.byId(track.folderId) ?: return null
        val cacheKey = "${folder.id}:${track.relativePath}"
        val children = directoryCache.get(cacheKey) ?: tree.list(folder, track.relativePath).also { directoryCache.put(cacheKey, it) }
        val file = lrcNames(track).firstNotNullOfOrNull { name -> children.firstOrNull { !it.isDirectory && it.name.equals(name, ignoreCase = true) } }
            ?: return null
        val bytes = tree.read(folder, file.documentId) ?: return null
        return LrcParser.parse(CharsetDetector.decode(bytes)).takeIf { it.lines.isNotEmpty() }
    }

    private fun embedded(track: TrackEntity): Lyrics? = runCatching {
        val uri = MediaStoreSource.contentUri(track.volumeName, track.mediaStoreId)
        context.contentResolver.openFileDescriptor(uri, "r")?.use { pfd ->
            // TagLib 的 FileStream 会关闭传入的 fd，所以传 dup 出来的副本
            val map = TagLib.getMetadata(pfd.dup().detachFd(), readPictures = false)?.propertyMap ?: return null
            TagReader.LYRICS_KEYS.firstNotNullOfOrNull { key -> map[key]?.firstOrNull { it.isNotBlank() } }
        }
    }.getOrNull()
        ?.let { CharsetFixer.fix(it) }
        ?.takeIf { it.isNotBlank() }
        ?.let { LrcParser.parse(it) }
        ?.takeIf { it.lines.isNotEmpty() }

    fun invalidateDirectory(folderId: Long, relativePath: String) {
        directoryCache.remove("$folderId:$relativePath")
    }

    suspend fun offset(trackKey: String): Long = lyricsDao.offset(trackKey) ?: 0

    suspend fun setOffset(trackKey: String, offsetMs: Long) {
        if (offsetMs == 0L) lyricsDao.clearOffset(trackKey) else lyricsDao.setOffset(LyricsOffsetEntity(trackKey, offsetMs))
    }

    private fun sanitize(name: String) = name.replace(Regex("""[\\/:*?"<>|]"""), "_").trim()
}
