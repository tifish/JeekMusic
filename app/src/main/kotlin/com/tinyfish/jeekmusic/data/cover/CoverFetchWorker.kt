package com.tinyfish.jeekmusic.data.cover

import android.content.Context
import android.util.Log
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkInfo
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.online.ListDownloadScheduler
import com.tinyfish.jeekmusic.data.settings.CoverSaveMode
import com.tinyfish.jeekmusic.data.settings.FetchMode
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.withContext
import java.io.File
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 批量获取缺失的封面（需求 C-3，docs/design.md §7.4）：按专辑（没有专辑时按歌曲）逐个检查。
 * - 同专辑已经有封面（别的文件里内嵌的、同名图片等）：直接提取，不重复下载。隐藏目录模式下整张专辑共用，什么也不用做；
 *   写入文件 / 同名图片模式下给还没有的文件补上
 * - 整张专辑都没有：在线搜索一次，只接受歌名和艺术家都对得上的结果，保存给整张专辑
 *
 * 找不到的记下来，30 天内不再查；被系统停止时已保存的封面不会丢，重新运行时会跳过它们。
 */
class CoverFetchWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {

    @EntryPoint
    @InstallIn(SingletonComponent::class)
    interface Dependencies {
        fun trackDao(): TrackDao
        fun coverStore(): CoverStore
        fun coverClient(): CoverClient
        fun settings(): SettingsRepository
    }

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val deps = EntryPointAccessors.fromApplication(applicationContext, Dependencies::class.java)
        val settings = deps.settings().current()
        if (!settings.onlineCovers) return@withContext Result.success()
        val misses = MissLog(File(applicationContext.filesDir, MISSES_FILE))
        // 只处理某个列表里的歌（需求 C-6）：用户明确要求的，不跳过 30 天内没找到的
        val targets = inputData.getString(KEY_TARGETS)?.let(ListDownloadScheduler::readTargets)

        setProgress(workDataOf(KEY_CHECKING to true))
        val store = deps.coverStore()
        // 选了写入文件但没有权限时，保存总会改存到隐藏目录：按隐藏目录的方式判断，否则每次都会重复保存
        val mode = settings.coverSaveMode.takeUnless { it == CoverSaveMode.EMBED && !store.canEmbed() } ?: CoverSaveMode.HIDDEN_FOLDER
        // 每张专辑里还需要封面的文件
        val tracks = targets?.chunked(SQL_CHUNK)?.flatMap { deps.trackDao().byIds(it) } ?: deps.trackDao().all()
        val missing = tracks.groupBy { it.albumKey ?: it.trackKey }.mapNotNull { (key, tracks) ->
            if (isStopped) return@mapNotNull null
            val album = store.albumOf(tracks.first())
            val targets = when (mode) {
                CoverSaveMode.HIDDEN_FOLDER -> if (store.hasCover(album.first())) emptyList() else album
                else -> album.filter { !store.hasOwnCover(it, mode) }
            }
            if (targets.isEmpty()) null else key to targets
        }
        var done = 0
        var found = 0
        var extracted = 0
        for ((key, targets) in missing) {
            if (isStopped) break
            setProgress(workDataOf(KEY_DONE to done, KEY_TOTAL to missing.size, KEY_FOUND to found))
            done++
            // 同专辑已有的封面直接提取，不用下载
            store.albumImage(targets.first())?.let { existing ->
                if (store.save(targets, existing, mode) != null) {
                    found++
                    extracted++
                }
                continue
            }
            if (targets == null && misses.recent(key)) continue
            // 没有艺术家的歌只凭歌名很容易配错，不自动获取
            val track = targets.firstOrNull { it.artistDisplay.isNotBlank() }
            if (track == null) {
                misses.add(key)
                continue
            }
            val result = runCatching { deps.coverClient().fetchBest(track.titleTag ?: track.title, track.artistDisplay, track.album) }
                .onFailure { Log.w(TAG, "fetch failed: ${track.title}", it) }
            val image = result.getOrNull()
            when {
                image != null && store.save(listOf(track) + (targets - track), image, mode) != null -> found++
                // 网络出错的不算"找不到"，下次还会再试
                result.isSuccess -> misses.add(key)
            }
        }
        misses.save()
        Log.i(TAG, "covers: $found found ($extracted from the same album), $done checked, ${missing.size} missing")
        if (isStopped) return@withContext Result.retry()
        Result.success(workDataOf(KEY_DONE to done, KEY_TOTAL to missing.size, KEY_FOUND to found))
    }

    /** 最近找不到封面的专辑（或歌曲），每行"时间\t键"。 */
    private class MissLog(private val file: File) {
        private val entries: MutableMap<String, Long> = runCatching {
            file.readLines().mapNotNull { line ->
                val (time, key) = line.split('\t', limit = 2).takeIf { it.size == 2 } ?: return@mapNotNull null
                time.toLongOrNull()?.let { key to it }
            }.toMap(HashMap())
        }.getOrDefault(HashMap())
        private val now = System.currentTimeMillis()

        fun recent(key: String) = entries[key]?.let { now - it < RETRY_AFTER_MS } == true
        fun add(key: String) {
            entries[key] = now
        }

        fun save() = runCatching {
            file.writeText(entries.filter { now - it.value < RETRY_AFTER_MS }.entries.joinToString("") { "${it.value}\t${it.key}\n" })
        }
    }

    companion object {
        const val TAG = "CoverFetchWorker"
        const val KEY_CHECKING = "checking"
        const val KEY_DONE = "done"
        const val KEY_TOTAL = "total"
        const val KEY_FOUND = "found"
        /** 只处理这些歌：保存歌曲 id 的文件路径（id 可能上千个，超出 WorkManager 输入数据的大小限制）。 */
        const val KEY_TARGETS = "targets"
        private const val SQL_CHUNK = 900
        private const val MISSES_FILE = "cover_misses.tsv"
        private const val RETRY_AFTER_MS = 30L * 24 * 3600 * 1000
    }
}

/**
 * 批量获取的进度：正在运行，或最近一次运行的结果。
 * [waiting] 表示自动获取在排队等网络（仅 Wi-Fi 时等不计流量的网络）。
 */
data class CoverFetchProgress(val running: Boolean, val waiting: Boolean, val checking: Boolean, val done: Int, val total: Int, val found: Int)

@Singleton
class CoverFetchScheduler @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val settings: SettingsRepository,
) {
    private val workManager get() = WorkManager.getInstance(context)

    /** 手动获取：任何网络都行，替换掉在等 Wi-Fi 的自动任务。 */
    fun start() = enqueue(NetworkType.CONNECTED, ExistingWorkPolicy.REPLACE)

    /**
     * 自动获取（需求 C-3）：App 启动、扫描到新歌、改设置时调用。手动模式或没开在线封面时什么也不做；
     * 已有任务（正在运行或排队）时保持不变。有封面的和 30 天内找不到的都会跳过，所以重复调用不会重复联网。
     */
    suspend fun scheduleAuto() {
        val s = settings.current()
        if (!s.onlineCovers) return
        when (s.coverFetchMode) {
            FetchMode.MANUAL -> Unit
            FetchMode.AUTO -> enqueue(NetworkType.CONNECTED, ExistingWorkPolicy.KEEP)
            FetchMode.WIFI -> enqueue(NetworkType.UNMETERED, ExistingWorkPolicy.KEEP)
        }
    }

    /** 改了获取方式：去掉按旧方式排队、还没开始的任务，再按新方式安排。 */
    suspend fun onModeChanged() {
        val infos = withContext(Dispatchers.IO) { workManager.getWorkInfosForUniqueWork(WORK_NAME).get() }
        if (infos.any { it.state == WorkInfo.State.ENQUEUED || it.state == WorkInfo.State.BLOCKED }) cancel()
        scheduleAuto()
    }

    fun cancel() {
        workManager.cancelUniqueWork(WORK_NAME)
    }

    private fun enqueue(network: NetworkType, policy: ExistingWorkPolicy) {
        val request = OneTimeWorkRequestBuilder<CoverFetchWorker>()
            .setConstraints(Constraints.Builder().setRequiredNetworkType(network).build())
            .build()
        workManager.enqueueUniqueWork(WORK_NAME, policy, request)
    }

    /** 当前或最近一次的进度；从没运行过时为 null。 */
    val progress: Flow<CoverFetchProgress?> = workManager.getWorkInfosForUniqueWorkFlow(WORK_NAME).map { infos ->
        val info = infos.lastOrNull() ?: return@map null
        val running = info.state == WorkInfo.State.RUNNING
        val waiting = info.state == WorkInfo.State.ENQUEUED || info.state == WorkInfo.State.BLOCKED
        val data = if (info.state == WorkInfo.State.SUCCEEDED) info.outputData else info.progress
        CoverFetchProgress(
            running = running,
            waiting = waiting,
            checking = running && data.getBoolean(CoverFetchWorker.KEY_CHECKING, false),
            done = data.getInt(CoverFetchWorker.KEY_DONE, 0),
            total = data.getInt(CoverFetchWorker.KEY_TOTAL, 0),
            found = data.getInt(CoverFetchWorker.KEY_FOUND, 0),
        ).takeIf { running || waiting || info.state == WorkInfo.State.SUCCEEDED }
    }

    companion object {
        const val WORK_NAME = "cover-fetch"
    }
}
