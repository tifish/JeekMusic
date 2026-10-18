package com.tinyfish.jeekmusic.data.lyrics

import android.content.Context
import android.os.ParcelFileDescriptor
import android.util.LruCache
import com.tinyfish.jeekmusic.data.db.LyricsCacheEntity
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.library.appDirectory
import com.tinyfish.jeekmusic.data.library.AudioFileEditor
import com.tinyfish.jeekmusic.data.library.create
import com.tinyfish.jeekmusic.data.library.writeFile
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
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.library.DocumentTree
import com.tinyfish.jeekmusic.data.scanner.CharsetDetector
import com.tinyfish.jeekmusic.data.scanner.CharsetFixer
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource
import com.tinyfish.jeekmusic.data.scanner.TagReader
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 歌词来源与优先级（docs/design.md §8.1）：本 App 保存的在线歌词在前，这样手动换的歌词（需求 LY-15）一定能显示。
 * 1. .jeekmusic/lyrics/ 里保存的在线歌词
 * 2. lyrics_cache（选择了"仅 App 内"，或在线获取了但没能写进音乐目录的）
 * 3. 同目录同名 .lrc（通过 SAF 树 URI 查找，大小写不敏感）
 * 4. 内嵌歌词
 * 保存时删掉这首歌排在新保存位置前面的、本 App 保存的那份，见 [save]。
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
    private val trackDao: TrackDao,
) {
    private val _changes = MutableStateFlow(0)

    /** 每保存一次歌词加一：后台获取的歌词保存后，播放页据此重新读取（需求 LY-8）。 */
    val changes: StateFlow<Int> = _changes

    private fun changed() = _changes.update { it + 1 }

    /** 按目录缓存子文件列表；写入 .lrc 后要使对应目录失效。 */
    private val directoryCache = LruCache<String, List<DocumentTree.Child>>(32)

    suspend fun load(track: TrackEntity): Lyrics? = withContext(Dispatchers.IO) {
        hiddenLrc(track)?.let { return@withContext it }
        lyricsDao.cached(track.trackKey)?.content?.let { LrcParser.parse(it) }?.takeIf { it.lines.isNotEmpty() }?.let { return@withContext it }
        lrcFile(track)?.let { return@withContext it }
        if (track.cueTrackNo == null) embedded(track) else null
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

    private suspend fun deleteHidden(folder: LibraryFolderEntity, track: TrackEntity) {
        val path = DocumentTree.appPath(folder, LYRICS_DIR)
        findLrc(folder, path, track)?.let { tree.delete(folder, it.documentId) }
        invalidateDirectory(folder.id, path)
    }

    /**
     * 把已经下载的歌词写入音乐文件（需求 LY-14）：每首歌用读取时排在最前的那份（隐藏目录 > App 内缓存 > 同名 .lrc），
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
            val hidden = findLrc(folder, DocumentTree.appPath(folder, LYRICS_DIR), t)
            // 文件读不出来时不用下一份代替：写进去后它会被删掉
            val text = when {
                hidden != null -> readText(folder, hidden)
                cached[t.trackKey] != null -> cached[t.trackKey]
                else -> findLrc(folder, t.relativePath, t)?.let { readText(folder, it) }
            } ?: continue
            if (text.isBlank() || !embed(folder, t, text.replace("\r\n", "\n"))) continue
            deleteDownloaded(folder, t)
            count++
        }
        if (count > 0) changed()
        count
    }

    /**
     * 有没有歌词：先看曲库里记下的、目录列表和缓存；都没有时再读一次文件确认内嵌歌词——
     * 以前本 App 写入的歌词没有更新 has_embedded_lyrics，扫描又因为修改时间、大小没变而跳过这些文件。
     * 读到了就记下来，下次不用再读。
     */
    suspend fun hasLyrics(track: TrackEntity): Boolean = withContext(Dispatchers.IO) {
        if (track.cueTrackNo == null && track.hasEmbeddedLyrics) return@withContext true
        val folder = folderDao.byId(track.folderId)
        if (folder != null && (findLrc(folder, DocumentTree.appPath(folder, LYRICS_DIR), track) != null || findLrc(folder, track.relativePath, track) != null)) {
            return@withContext true
        }
        if (lyricsDao.cached(track.trackKey)?.content != null) return@withContext true
        (track.cueTrackNo == null && embedded(track) != null).also { if (it) trackDao.setHasEmbeddedLyrics(track.fileKey) }
    }

    /** 7 天内在线查过而没有找到（需求 LY-8）。 */
    suspend fun recentlyNotFound(track: TrackEntity): Boolean {
        val cached = lyricsDao.cached(track.trackKey) ?: return false
        return cached.content == null && System.currentTimeMillis() - cached.fetchedAt < NEGATIVE_CACHE_MS
    }

    /**
     * 按设置的来源精确查询一首歌的歌词（只采用歌名、艺术家对得上，时长相差不到 5 秒的）。
     * 没找到时记下来（7 天内自动获取不再查）并返回 null；网络出错时抛出异常，不算"没找到"。
     */
    suspend fun fetchOnline(track: TrackEntity): String? = withContext(Dispatchers.IO) {
        val result = online.get().best(track.titleTag ?: track.title, track.artistDisplay, track.album, track.durationMs / 1000)
        val text = result?.text?.takeIf { it.isNotBlank() }
        if (text == null) lyricsDao.cache(LyricsCacheEntity(track.trackKey, SOURCE, null, false, System.currentTimeMillis()))
        text
    }

    /** 按现在的设置，这首歌的歌词会不会写进音乐文件（CUE 整轨的不会）。 */
    suspend fun embedsInFile(track: TrackEntity): Boolean =
        track.cueTrackNo == null && editor.canEdit() && settings.current().lyricsSaveLocation == LyricsSaveLocation.EMBED

    /**
     * 把歌词写进已经打开的音乐文件，供和封面一起写入时用（docs/design.md §7.4）：TagLib 的属性表要整个写回，所以先读出原有的再加上歌词。
     * 成功后要调用 [onEmbedded]。
     */
    fun embedOperation(text: String): (ParcelFileDescriptor) -> Boolean = { pfd ->
        val map = TagLib.getMetadata(pfd.dup().detachFd(), readPictures = false)?.propertyMap
        if (map == null) {
            false
        } else {
            TagReader.lyricsKeys(map.keys).forEach { map.remove(it) }
            map["LYRICS"] = arrayOf(text.replace("\r\n", "\n"))
            TagLib.savePropertyMap(pfd.dup().detachFd(), map)
        }
    }

    /** 歌词经 [embedOperation] 写入文件后：记下文件里有歌词，删掉原来下载的，通知界面。 */
    suspend fun onEmbedded(track: TrackEntity) = withContext(Dispatchers.IO) {
        trackDao.setHasEmbeddedLyrics(track.fileKey)
        folderDao.byId(track.folderId)?.let { deleteDownloaded(it, track) }
        changed()
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

    suspend fun lyricsSources(): List<OnlineSource> = online.get().enabledSources()

    suspend fun search(source: OnlineSource, title: String, artist: String?): List<OnlineLyrics> = online.get().search(source, title, artist)

    /**
     * 保存在线歌词（需求 LY-10、LY-11），返回实际保存的位置（docs/design.md §8.5）：
     * - 同名 .lrc：UTF-8，与歌曲同目录；不覆盖已有文件
     * - 隐藏目录：.jeekmusic/lyrics/ 下与歌曲同名的 .lrc
     * - 写入文件：歌词标签（MP3 的 USLT 等）；CUE 整轨的一首歌不能写进整个文件，改存到隐藏目录
     * 写同名 .lrc 或写入文件失败时改存到隐藏目录，再失败或选择了"仅 App 内"时存入 lyrics_cache。
     *
     * 也用于手动替换现有的歌词（需求 LY-15）：保存成功后删掉本 App 保存的、读取时排在前面的那份
     * （存到同名 .lrc 时删隐藏目录和缓存，存到隐藏目录时删缓存，存到缓存时删隐藏目录），写入文件时还删同名 .lrc。
     * 同名 .lrc 已存在时不覆盖，改存到隐藏目录（排在它前面）。
     */
    suspend fun save(track: TrackEntity, text: String): LyricsSaveLocation = withContext(Dispatchers.IO) {
        val normalized = text.replace("\r\n", "\n")
        val mode = settings.current().lyricsSaveLocation
        val folder = folderDao.byId(track.folderId)
        val saved = folder?.let {
            when (mode) {
                LyricsSaveLocation.SIDECAR -> LyricsSaveLocation.SIDECAR.takeIf { writeLrc(folder, track, normalized) }
                    ?.also {
                        deleteHidden(folder, track)
                        lyricsDao.deleteCached(track.trackKey)
                    }
                LyricsSaveLocation.EMBED -> LyricsSaveLocation.EMBED.takeIf { track.cueTrackNo == null && embed(folder, track, normalized) }
                    ?.also { deleteDownloaded(folder, track) }
                LyricsSaveLocation.HIDDEN_FOLDER, LyricsSaveLocation.APP_CACHE -> null
            } ?: LyricsSaveLocation.HIDDEN_FOLDER.takeIf { mode != LyricsSaveLocation.APP_CACHE && writeHidden(folder, track, normalized) }
                ?.also { lyricsDao.deleteCached(track.trackKey) }
        }
        if (saved == null) {
            lyricsDao.cache(LyricsCacheEntity(track.trackKey, SOURCE, normalized, LrcParser.parse(normalized).synced, System.currentTimeMillis()))
            folder?.let { deleteHidden(it, track) }
        }
        changed()
        saved ?: LyricsSaveLocation.APP_CACHE
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
    private suspend fun embed(folder: LibraryFolderEntity, track: TrackEntity, text: String): Boolean =
        editor.edit(folder, track, embedOperation(text)).also { if (it) trackDao.setHasEmbeddedLyrics(track.fileKey) }

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
