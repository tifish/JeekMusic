package com.tinyfish.jeekmusic.data.lyrics

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.util.LruCache
import com.tinyfish.jeekmusic.data.db.LyricsCacheEntity
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.library.appDirectory
import com.tinyfish.jeekmusic.data.library.AudioFileEditor
import com.tinyfish.jeekmusic.data.library.create
import com.tinyfish.jeekmusic.data.library.writeFile
import com.tinyfish.jeekmusic.data.settings.FetchMode
import com.tinyfish.jeekmusic.data.settings.OnlineSource
import com.tinyfish.jeekmusic.data.library.delete
import com.tinyfish.jeekmusic.data.library.listById
import com.tinyfish.jeekmusic.data.library.write
import com.tinyfish.jeekmusic.data.settings.LyricsSaveLocation
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
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
 * 1. .jeekmusic/lyrics/ 里保存的在线歌词
 * 2. 同目录同名 .lrc（通过 SAF 树 URI 查找，大小写不敏感）
 * 3. 内嵌歌词
 * 4. lyrics_cache（选择了"仅 App 内"，或在线获取了但没能写进音乐目录的）
 */
@Singleton
class LyricsRepository @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val folderDao: FolderDao,
    private val lyricsDao: LyricsDao,
    private val tree: DocumentTree,
    private val settings: SettingsRepository,
    private val online: dagger.Lazy<OnlineLyricsClient>,
    private val editor: AudioFileEditor,
) {
    /** 按目录缓存子文件列表；写入 .lrc 后要使对应目录失效。 */
    private val directoryCache = LruCache<String, List<DocumentTree.Child>>(32)

    suspend fun load(track: TrackEntity): Lyrics? = withContext(Dispatchers.IO) {
        hiddenLrc(track)?.let { return@withContext it }
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
        return readLrc(folder, track.relativePath, track)
    }

    /** 隐藏目录里与歌曲同名的 .lrc（需求 LY-11）。 */
    private suspend fun hiddenLrc(track: TrackEntity): Lyrics? {
        val folder = folderDao.byId(track.folderId) ?: return null
        return readLrc(folder, DocumentTree.appPath(folder, LYRICS_DIR), track)
    }

    private fun readLrc(folder: LibraryFolderEntity, relativePath: String, track: TrackEntity): Lyrics? {
        val text = findLrc(folder, relativePath, track)?.let { readText(folder, it) } ?: return null
        return LrcParser.parse(text).takeIf { it.lines.isNotEmpty() }
    }

    private fun findLrc(folder: LibraryFolderEntity, relativePath: String, track: TrackEntity): DocumentTree.Child? {
        val cacheKey = "${folder.id}:$relativePath"
        val children = directoryCache.get(cacheKey) ?: tree.list(folder, relativePath).also { directoryCache.put(cacheKey, it) }
        return lrcNames(track).firstNotNullOfOrNull { name -> children.firstOrNull { !it.isDirectory && it.name.equals(name, ignoreCase = true) } }
    }

    private fun readText(folder: LibraryFolderEntity, file: DocumentTree.Child): String? =
        tree.read(folder, file.documentId)?.let { CharsetDetector.decode(it) }

    /**
     * 写入文件后删掉这首歌下载成文件的歌词（隐藏目录、同名 .lrc）和 App 内缓存的（需求 LY-14）：
     * 它们读取时都排在内嵌歌词前面（缓存除外），不删的话写进去的歌词不会显示。
     */
    private suspend fun deleteDownloaded(folder: LibraryFolderEntity, track: TrackEntity) {
        for (path in listOf(DocumentTree.appPath(folder, LYRICS_DIR), track.relativePath)) {
            findLrc(folder, path, track)?.let { tree.delete(folder, it.documentId) }
            invalidateDirectory(folder.id, path)
        }
        lyricsDao.deleteCached(track.trackKey)
    }

    /**
     * 把已经下载的歌词写入音乐文件（需求 LY-14）：每首歌用读取时排在最前的那份（隐藏目录 > 同名 .lrc > App 内缓存），
     * 写入成功后删掉这些文件和缓存。CUE 整轨的歌不能写进整个文件，不动。返回写入了几首。
     */
    suspend fun embedDownloaded(tracks: List<TrackEntity>, isStopped: () -> Boolean = { false }): Int = withContext(Dispatchers.IO) {
        if (!editor.canEdit()) return@withContext 0
        val cached = lyricsDao.cachedWithContent().associate { it.trackKey to it.content }
        val folders = HashMap<Long, LibraryFolderEntity?>()
        var count = 0
        // 按目录排，目录列表的缓存才能命中
        for (t in tracks.filter { it.cueTrackNo == null }.distinctBy { it.fileKey }.sortedWith(compareBy({ it.folderId }, { it.relativePath }))) {
            if (isStopped()) break
            val folder = folders.getOrPut(t.folderId) { folderDao.byId(t.folderId) } ?: continue
            val file = findLrc(folder, DocumentTree.appPath(folder, LYRICS_DIR), t) ?: findLrc(folder, t.relativePath, t)
            // 文件读不出来时不用缓存代替：写进去后文件会被删掉
            val text = (if (file != null) readText(folder, file) else cached[t.trackKey]) ?: continue
            if (text.isBlank() || !embed(folder, t, text.replace("\r\n", "\n"))) continue
            deleteDownloaded(folder, t)
            count++
        }
        count
    }

    private fun embedded(track: TrackEntity): Lyrics? = runCatching {
        val uri = MediaStoreSource.contentUri(track.volumeName, track.mediaStoreId)
        context.contentResolver.openFileDescriptor(uri, "r")?.use { pfd ->
            // TagLib 的 FileStream 会关闭传入的 fd，所以传 dup 出来的副本
            val map = TagLib.getMetadata(pfd.dup().detachFd(), readPictures = false)?.propertyMap ?: return null
            TagReader.lyricsKeys(map.keys).firstNotNullOfOrNull { key -> map[key]?.firstOrNull { it.isNotBlank() } }
        }
    }.getOrNull()
        ?.let { CharsetFixer.fix(it) }
        ?.takeIf { it.isNotBlank() }
        ?.let { LrcParser.parse(it) }
        ?.takeIf { it.lines.isNotEmpty() }

    /**
     * 自动获取（需求 LY-8）：播放到没有歌词的歌时，如果打开了在线歌词和自动获取（仅 Wi-Fi 时要求当前是不计流量的网络）、
     * 7 天内没有查过"未找到"，就精确查询 LRCLIB，找到就保存。
     */
    suspend fun autoFetch(track: TrackEntity): Lyrics? = withContext(Dispatchers.IO) {
        val s = settings.current()
        val allowed = when (s.lyricsFetchMode) {
            FetchMode.MANUAL -> false
            FetchMode.AUTO -> true
            FetchMode.WIFI -> isUnmetered()
        }
        if (!s.onlineLyrics || !allowed) return@withContext null
        val cached = lyricsDao.cached(track.trackKey)
        if (cached != null && cached.content == null && System.currentTimeMillis() - cached.fetchedAt < NEGATIVE_CACHE_MS) {
            return@withContext null
        }
        val artist = track.artistDisplay.ifEmpty { return@withContext null }
        val result = runCatching { online.get().best(track.titleTag ?: track.title, artist, track.album, track.durationMs / 1000) }
            .getOrElse { return@withContext null }
        if (result?.text == null) {
            lyricsDao.cache(LyricsCacheEntity(track.trackKey, SOURCE, null, false, System.currentTimeMillis()))
            return@withContext null
        }
        save(track, result.text!!)
        LrcParser.parse(result.text!!)
    }

    /** 批量下载一首歌的结果。 */
    enum class FetchOutcome { ALREADY_HAS, FOUND, NOT_FOUND, FAILED }

    /**
     * 批量下载（需求 LY-13）：已经有歌词的跳过；没有的按设置的来源精确查询，找到就保存。
     * 是用户对某个列表明确要求的，所以不看"7 天内没找到"的记录；没有艺术家的歌只凭歌名容易配错，不查。
     */
    suspend fun fetchMissing(track: TrackEntity): FetchOutcome = withContext(Dispatchers.IO) {
        if (load(track) != null) return@withContext FetchOutcome.ALREADY_HAS
        val artist = track.artistDisplay.ifEmpty { return@withContext FetchOutcome.NOT_FOUND }
        val result = runCatching { online.get().best(track.titleTag ?: track.title, artist, track.album, track.durationMs / 1000) }
            .getOrElse { return@withContext FetchOutcome.FAILED }
        val text = result?.text
        if (text == null) {
            lyricsDao.cache(LyricsCacheEntity(track.trackKey, SOURCE, null, false, System.currentTimeMillis()))
            return@withContext FetchOutcome.NOT_FOUND
        }
        save(track, text)
        FetchOutcome.FOUND
    }

    suspend fun lyricsSources(): List<OnlineSource> = online.get().enabledSources()

    suspend fun search(source: OnlineSource, title: String, artist: String?): List<OnlineLyrics> = online.get().search(source, title, artist)

    /**
     * 保存在线歌词（需求 LY-10、LY-11），返回实际保存的位置（docs/design.md §8.5）：
     * - 同名 .lrc：UTF-8，与歌曲同目录；不覆盖已有文件
     * - 隐藏目录：.jeekmusic/lyrics/ 下与歌曲同名的 .lrc
     * - 写入文件：歌词标签（MP3 的 USLT 等）；CUE 整轨的一首歌不能写进整个文件，改存到隐藏目录
     * 写同名 .lrc 或写入文件失败时改存到隐藏目录，再失败或选择了"仅 App 内"时存入 lyrics_cache。
     */
    suspend fun save(track: TrackEntity, text: String): LyricsSaveLocation = withContext(Dispatchers.IO) {
        val normalized = text.replace("\r\n", "\n")
        val mode = settings.current().lyricsSaveLocation
        val folder = folderDao.byId(track.folderId)
        val saved = folder?.let {
            when (mode) {
                LyricsSaveLocation.SIDECAR -> LyricsSaveLocation.SIDECAR.takeIf { writeLrc(folder, track, normalized) }
                LyricsSaveLocation.EMBED -> LyricsSaveLocation.EMBED.takeIf { track.cueTrackNo == null && embed(folder, track, normalized) }
                    ?.also { deleteDownloaded(folder, track) }
                LyricsSaveLocation.HIDDEN_FOLDER, LyricsSaveLocation.APP_CACHE -> null
            } ?: LyricsSaveLocation.HIDDEN_FOLDER.takeIf { mode != LyricsSaveLocation.APP_CACHE && writeHidden(folder, track, normalized) }
        }
        if (saved != null) return@withContext saved
        lyricsDao.cache(LyricsCacheEntity(track.trackKey, SOURCE, normalized, LrcParser.parse(normalized).synced, System.currentTimeMillis()))
        LyricsSaveLocation.APP_CACHE
    }

    private fun writeHidden(folder: LibraryFolderEntity, track: TrackEntity, text: String): Boolean {
        val dirId = tree.appDirectory(folder, LYRICS_DIR) ?: return false
        return tree.writeFile(folder, dirId, lrcNames(track).first(), text.toByteArray(Charsets.UTF_8))
            .also { invalidateDirectory(folder.id, DocumentTree.appPath(folder, LYRICS_DIR)) }
    }

    /**
     * 写进歌词标签：TagLib 的属性表要整个写回，所以先读出原有的再加上歌词。
     * 没有"所有文件访问权限"时不写入（保持修改时间不变需要它），改存到隐藏目录。
     */
    private suspend fun embed(folder: LibraryFolderEntity, track: TrackEntity, text: String): Boolean = editor.edit(folder, track) { pfd ->
        val map = TagLib.getMetadata(pfd.dup().detachFd(), readPictures = false)?.propertyMap ?: return@edit false
        TagReader.lyricsKeys(map.keys).forEach { map.remove(it) }
        map["LYRICS"] = arrayOf(text)
        TagLib.savePropertyMap(pfd.dup().detachFd(), map)
    }

    private fun writeLrc(folder: LibraryFolderEntity, track: TrackEntity, text: String): Boolean {
        val dirId = DocumentTree.directoryDocumentId(folder, track.relativePath)
        val name = lrcNames(track).first()
        // 再次确认目录里没有同名 .lrc，有的话不覆盖
        if (tree.listById(folder, dirId).any { it.name.equals(name, ignoreCase = true) }) return false
        // mime 不用 text/plain：部分 DocumentsProvider 会自作主张地追加 ".txt"
        val created = tree.create(folder, dirId, "application/octet-stream", name) ?: return false
        if (created.name != name) {
            tree.delete(folder, created.documentId)
            return false
        }
        if (!tree.write(folder, created.documentId, text.toByteArray(Charsets.UTF_8))) {
            tree.delete(folder, created.documentId)
            return false
        }
        invalidateDirectory(folder.id, track.relativePath)
        return true
    }

    suspend fun clearCache() = lyricsDao.clearCache()

    private fun isUnmetered(): Boolean {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return false
        return caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
    }

    fun invalidateDirectory(folderId: Long, relativePath: String) {
        directoryCache.remove("$folderId:$relativePath")
    }

    suspend fun offset(trackKey: String): Long = lyricsDao.offset(trackKey) ?: 0

    suspend fun setOffset(trackKey: String, offsetMs: Long) {
        if (offsetMs == 0L) lyricsDao.clearOffset(trackKey) else lyricsDao.setOffset(LyricsOffsetEntity(trackKey, offsetMs))
    }

    private companion object {
        /** lyrics_cache 的来源列；以前只有 LRCLIB 时是 "lrclib"。 */
        const val SOURCE = "online"
        const val NEGATIVE_CACHE_MS = 7 * 24 * 3600_000L
        const val LYRICS_DIR = "lyrics"
    }

    private fun sanitize(name: String) = name.replace(Regex("""[\\/:*?"<>|]"""), "_").trim()
}
