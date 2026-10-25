package com.tinyfish.jeekmusic.data.library

import android.content.Context
import android.net.Uri
import android.os.Environment
import android.os.storage.StorageManager
import android.system.Os
import android.util.Log
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.db.TagDao
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.playback.PlaybackLog
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File
import com.tinyfish.jeekmusic.data.tags.SafeTagWriter
import com.tinyfish.jeekmusic.data.tags.TagEdit
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 修改音乐文件的标签（写入封面、歌词，需求 C-4、LY-11），并保持文件的修改时间不变：
 * "按修改时间排序"和电脑上的同步工具都依赖这个时间，写个标签不应该让歌曲跑到最前面。
 *
 * Android 不允许普通 App 修改别人文件的修改时间（即使通过 SAF 有写权限）；有"所有文件访问权限"时，
 * 对打开的文件描述符设置修改时间才会被放行（docs/design.md §7.4）。所以没有这个权限时不修改文件，
 * 由调用方改存到隐藏目录。
 */
@Singleton
class AudioFileEditor @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val tree: DocumentTree,
    private val trackDao: TrackDao,
    private val tagDao: TagDao,
    private val playingFiles: PlayingFiles,
    private val settings: SettingsRepository,
    private val log: PlaybackLog,
) {
    /** 正在修改的文件：批量获取封面、写入已下载的封面和歌词等可能同时改同一个文件，必须一个一个来。 */
    private val locks = ConcurrentHashMap<String, Mutex>()

    /** 有没有"所有文件访问权限"，也就是能不能写入音乐文件。 */
    fun canEdit(): Boolean = Environment.isExternalStorageManager()

    /** 这首歌正在播放，写入推迟到播放器用完这个文件之后。 */
    fun isDeferred(track: TrackEntity) = playingFiles.isDeferred(track.fileKey)

    /**
     * SAF 验证写授权及源文件身份，在同目录副本中只修改 [change] 指定的目标。
     * 独立验证非目标元数据、音频与目标全文，再检查源 SHA/身份和播放状态，以 rename 原子提交。
     * 权限、格式、验证或提交失败时返回 false，调用方使用外部文件回退。
     *
     * 播放器正在用这个文件（[PlayingFiles]）时也不写、返回 false：调用方会先存成下载的文件，
     * 播放器用完后再写进来（需求 P-14）。顺便清掉"下载的都已写入"标记，万一 App 在那之前被关掉，下次启动时补上。
     *
     * 文件大小变了：立即更新曲库里的大小，智能标签的签名（修改时间:大小）也随之更新——音频本身没变，不需要重新分析。
     * 两者都更新后，之后的扫描看到修改时间和大小都没变，也不会再处理这个文件。
     */
    suspend fun edit(folder: LibraryFolderEntity, track: TrackEntity, change: TagEdit): Boolean {
        if (change.cover == null && change.lyrics == null) return true
        if (!canEdit()) return false
        if (playingFiles.isBusy(track.fileKey)) {
            playingFiles.defer(track)
            settings.clearDownloadedEmbedded()
            log.add("edit deferred (playing): ${track.fileName}")
            return false
        }
        return locks.getOrPut(track.fileKey) { Mutex() }.withLock { editLocked(folder, track, change) }
    }

    private suspend fun editLocked(folder: LibraryFolderEntity, track: TrackEntity, change: TagEdit): Boolean {
        var oldSize: Long? = null
        var newSize: Long? = null
        val ok = runCatching {
            require(Uri.parse(folder.treeUri).authority == "com.android.externalstorage.documents") { "Atomic local commit requires ExternalStorageProvider" }
            val fileId = childDocumentId(DocumentTree.directoryDocumentId(folder, track.relativePath), track.fileName)
            tree.resolver.openFileDescriptor(tree.documentUri(folder, fileId), "rw")?.use { pfd ->
                val before = Os.fstat(pfd.fileDescriptor)
                // 用文件的实际大小，不用 [track] 里的：同一首歌连着写封面和歌词时，[track] 还是第一次写之前的
                oldSize = before.st_size
                val volume = context.getSystemService(StorageManager::class.java).storageVolumes
                    .singleOrNull { it.mediaStoreVolumeName == track.volumeName }?.directory ?: error("No local storage volume")
                val source = File(volume, track.relativePath + track.fileName).canonicalFile
                require(source.path.startsWith(volume.canonicalPath + File.separator)) { "Invalid local path" }
                val changed = AtomicAudioFile.replace(source, pfd.fileDescriptor, { stage ->
                    SafeTagWriter.write(stage, change, track.fileName.substringAfterLast('.'))
                }, { stage ->
                    SafeTagWriter.validate(source, stage, change, track.fileName.substringAfterLast('.'))
                }, { !playingFiles.isBusy(track.fileKey) })
                if (!changed && playingFiles.isBusy(track.fileKey)) {
                    playingFiles.defer(track)
                    settings.clearDownloadedEmbedded()
                }
                if (changed) newSize = source.length()
                changed
            } ?: false
        }.onFailure {
            Log.w(TAG, "edit refused: ${track.fileName}", it)
            log.add("edit refused: ${track.fileName}: ${it.message}")
        }.getOrDefault(false)
        val from = oldSize
        log.add("edit ${if (ok) "done" else "failed"}: ${track.fileName} size $from -> $newSize")
        newSize?.takeIf { ok && from != null && it != from }?.let { size ->
            runCatching {
                trackDao.updateSize(track.fileKey, size)
                for (old in setOf(from, track.size)) tagDao.renameSignature(track.fileKey, "${track.dateModified}:$old", "${track.dateModified}:$size")
            }.onFailure { Log.w(TAG, "Committed tags; database size refresh failed", it) }
        }
        return ok
    }

    private companion object {
        const val TAG = "AudioFileEditor"
    }
}
