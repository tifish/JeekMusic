package com.tinyfish.jeekmusic.data.scanner

import android.os.Process
import android.util.Log
import androidx.room.withTransaction
import com.tinyfish.jeekmusic.analysis.AnalysisScheduler
import com.tinyfish.jeekmusic.data.cover.CoverFetchScheduler
import com.tinyfish.jeekmusic.data.db.ArtistDao
import com.tinyfish.jeekmusic.data.db.ArtistEntity
import com.tinyfish.jeekmusic.data.db.FolderDao
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.db.TrackArtistEntity
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.db.UNKNOWN_ARTIST_KEY
import com.tinyfish.jeekmusic.data.library.DocumentTree
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.di.ApplicationScope
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.async
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.Collections
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger
import javax.inject.Inject
import javax.inject.Singleton

sealed interface ScanState {
    data object Idle : ScanState
    data class Running(val done: Int, val total: Int) : ScanState
}

data class ScanResult(val added: Int, val updated: Int, val removed: Int, val unsupported: Int, val elapsedMs: Long)

/**
 * 曲库扫描（docs/design.md §4.2）。同一时间只有一个扫描任务，扫描期间的新请求会合并成一次后续扫描。
 */
@Singleton
class LibraryScanner @Inject constructor(
    private val db: JeekDatabase,
    private val folderDao: FolderDao,
    private val trackDao: TrackDao,
    private val artistDao: ArtistDao,
    private val mediaStore: MediaStoreSource,
    private val tagReader: TagReader,
    private val cueResolver: CueResolver,
    private val analysisScheduler: dagger.Lazy<AnalysisScheduler>,
    private val coverFetch: dagger.Lazy<CoverFetchScheduler>,
    private val settings: SettingsRepository,
    @param:ApplicationScope private val scope: CoroutineScope,
) {
    private val _state = MutableStateFlow<ScanState>(ScanState.Idle)
    val state: StateFlow<ScanState> = _state.asStateFlow()

    /** 有扫描正在进行或排队（恢复备份要等扫描完成）。 */
    private val _busy = MutableStateFlow(false)
    val busy: StateFlow<Boolean> = _busy.asStateFlow()

    private val _lastResult = MutableStateFlow<ScanResult?>(null)
    val lastResult: StateFlow<ScanResult?> = _lastResult.asStateFlow()

    private val mutex = Mutex()
    private val requestLock = Any()
    private var running = false
    private var pending: Boolean? = null // null = 无；true/false = 有待执行的全量/增量请求
    private var pendingRecheckCue = false

    /** 后台优先级的 4 线程池，不和 UI、播放抢 CPU。 */
    private val dispatcher = Executors.newFixedThreadPool(PARALLELISM) { runnable ->
        Thread({
            Process.setThreadPriority(Process.THREAD_PRIORITY_BACKGROUND)
            runnable.run()
        }, "scanner").apply { isDaemon = true }
    }.asCoroutineDispatcher()

    private val sortKeys by lazy { SortKeys.android() }

    /**
     * @param full 重新读取所有文件的标签（"重建曲库"）
     * @param recheckCue 对没有变化的整轨候选文件也重新检测 CUE（"重新扫描"）：新增或修改 .cue 不会触发 MediaStore 音频变化
     */
    fun requestScan(full: Boolean = false, recheckCue: Boolean = false) {
        synchronized(requestLock) {
            if (running) {
                pending = (pending ?: false) || full
                pendingRecheckCue = pendingRecheckCue || recheckCue
                return
            }
            running = true
            _busy.value = true
        }
        scope.launch(dispatcher) {
            var next: Boolean? = full
            var cue = recheckCue
            while (next != null) {
                runCatching { scan(next, cue) }.onFailure { Log.e(TAG, "scan failed", it) }
                cue = synchronized(requestLock) { pendingRecheckCue.also { pendingRecheckCue = false } }
                next = synchronized(requestLock) {
                    pending.also {
                        pending = null
                        if (it == null) {
                            running = false
                            _busy.value = false
                        }
                    }
                }
            }
        }
    }

    suspend fun scan(full: Boolean, recheckCue: Boolean = false): ScanResult = mutex.withLock {
        val start = System.currentTimeMillis()
        val config = settings.current().scan
        val factory = TrackFactory(config)

        // 1-3. 查询 MediaStore，过滤不支持的格式
        val found = LinkedHashMap<String, Pair<MediaStoreAudio, LibraryFolderEntity>>()
        var unsupported = 0
        for (folder in folderDao.all()) {
            for (audio in mediaStore.query(folder.volumeName, folder.relativePath)) {
                if (SupportedFormats.isSupported(audio.fileName)) found[audio.fileKey] = audio to folder else unsupported++
            }
        }

        // 4. 与库中记录比对
        val existing = trackDao.signatures().groupBy { it.fileKey }
        val removedIds = existing.filterKeys { it !in found }.values.flatten().map { it.id }
        val toParse = found.values.filter { (audio, _) ->
            val old = existing[audio.fileKey]?.firstOrNull()
            full || old == null || old.dateModified != audio.dateModified || old.size != audio.size ||
                (recheckCue && cueResolver.isCandidate(audio))
        }
        val directoryCache = ConcurrentHashMap<String, List<DocumentTree.Child>>()
        val staleIds = Collections.synchronizedList(mutableListOf<Long>())

        _state.value = ScanState.Running(0, toParse.size)
        val done = AtomicInteger()
        var added = 0
        var updated = 0

        // 5-7. 分批解析和写入；整轨文件可能展开成多条 CUE 虚拟歌曲
        for (chunk in toParse.chunked(BATCH_SIZE)) {
            val built = coroutineScope {
                chunk.map { (audio, folder) ->
                    async {
                        val tags = tagReader.read(MediaStoreSource.contentUri(audio.volumeName, audio.mediaStoreId), config.legacyCharset)
                        val old = existing[audio.fileKey].orEmpty()
                        val idsByKey = old.associate { it.trackKey to it.id }
                        val cue = if (tags?.cueSheet != null || cueResolver.isCandidate(audio)) {
                            runCatching { cueResolver.resolve(folder, audio, tags?.cueSheet, directoryCache) }.getOrNull()
                        } else {
                            null
                        }
                        val tracks = if (cue != null) {
                            factory.buildCue(audio, folder.id, tags, cue, idsByKey)
                        } else {
                            listOf(factory.build(audio, folder.id, tags, idsByKey[audio.fileKey] ?: 0))
                        }
                        // 原来的虚拟歌曲在新结果中不存在了（比如删掉了 .cue），要删除
                        val keys = tracks.map { it.entity.trackKey }.toSet()
                        old.filter { it.trackKey !in keys }.forEach { staleIds += it.id }
                        _state.value = ScanState.Running(done.incrementAndGet(), toParse.size)
                        tracks
                    }
                }.awaitAll().flatten()
            }
            db.withTransaction {
                val (updates, inserts) = built.partition { it.entity.id != 0L }
                trackDao.update(updates.map { it.entity })
                val insertedIds = trackDao.insert(inserts.map { it.entity })
                val withIds = updates.map { it.entity.id to it.artists } +
                    inserts.mapIndexed { i, b -> insertedIds[i] to b.artists }
                writeArtistLinks(withIds)
                added += inserts.size
                updated += updates.size
            }
        }

        db.withTransaction {
            (removedIds + staleIds).chunked(BATCH_SIZE).forEach { trackDao.delete(it) }
            artistDao.deleteOrphans()
            artistDao.updateTrackCounts()
        }
        settings.setUnsupportedFileCount(unsupported)

        val result = ScanResult(added, updated, removedIds.size, unsupported, System.currentTimeMillis() - start)
        if (added + updated > 0) {
            analysisScheduler.get().schedule()
            coverFetch.get().scheduleAuto()
        }
        Log.i(TAG, "scan(full=$full) $result")
        _lastResult.value = result
        _state.value = ScanState.Idle
        result
    }

    /** 修改艺术家拆分或文件名推断设置后，用库里已存的字段重新计算，不重新解析文件。 */
    suspend fun rebuildArtists() = mutex.withLock {
        val factory = TrackFactory(settings.current().scan)
        val rebuilt = trackDao.all().map(factory::rebuild)
        db.withTransaction {
            artistDao.deleteAllLinks()
            rebuilt.chunked(BATCH_SIZE).forEach { chunk ->
                trackDao.update(chunk.map { it.entity })
                writeArtistLinks(chunk.map { it.entity.id to it.artists })
            }
            artistDao.deleteOrphans()
            artistDao.updateTrackCounts()
        }
    }

    private suspend fun writeArtistLinks(tracks: List<Pair<Long, List<String>>>) {
        if (tracks.isEmpty()) return
        val namesByKey = LinkedHashMap<String, String>()
        for ((_, artists) in tracks) {
            if (artists.isEmpty()) namesByKey.putIfAbsent(UNKNOWN_ARTIST_KEY, "")
            for (name in artists) namesByKey.putIfAbsent(ArtistSplitter.nameKey(name), name)
        }
        val keys = namesByKey.keys.toList()
        val known = keys.chunked(BATCH_SIZE).flatMap { artistDao.byNameKeys(it) }.associateBy { it.nameKey }
        val missing = keys.filter { it !in known }.map { key ->
            val name = namesByKey.getValue(key)
            val sortKey = if (key == UNKNOWN_ARTIST_KEY) "￿" else sortKeys.sortKey(name)
            ArtistEntity(name = name, nameKey = key, sortKey = sortKey, indexLetter = if (key == UNKNOWN_ARTIST_KEY) "#" else sortKeys.indexLetter(sortKey))
        }
        artistDao.insert(missing)
        val ids = keys.chunked(BATCH_SIZE).flatMap { artistDao.byNameKeys(it) }.associate { it.nameKey to it.id }

        artistDao.deleteLinks(tracks.map { it.first })
        artistDao.insertLinks(
            tracks.flatMap { (trackId, artists) ->
                val artistKeys = if (artists.isEmpty()) listOf(UNKNOWN_ARTIST_KEY) else artists.map(ArtistSplitter::nameKey)
                artistKeys.distinct().map { TrackArtistEntity(trackId, ids.getValue(it)) }
            },
        )
    }

    private companion object {
        const val TAG = "LibraryScanner"
        const val PARALLELISM = 4
        const val BATCH_SIZE = 500
    }
}
