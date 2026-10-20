package com.tinyfish.jeekmusic.data.online

import android.content.Context
import android.util.Log
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.WorkInfo
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import com.tinyfish.jeekmusic.data.cover.CoverMissLog
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.settings.FetchMode
import com.tinyfish.jeekmusic.data.settings.Settings
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 批量获取缺失的封面和歌词（需求 C-3、C-6、LY-8、LY-13，docs/design.md §7.4）：逐组检查（见 [OnlineFetcher]），
 * 封面按专辑获取一次、同专辑已有就提取；歌词逐首获取。两者都要写入同一个文件时一起写。
 *
 * 输入：[KEY_KINDS] 要获取什么；[KEY_FORCED] 其中用户手动要求的（不看获取方式和网络）；
 * [KEY_TARGETS] 只处理这些歌（列表下载，不跳过最近没找到的）。
 * 被系统停止时已保存的不会丢，重新运行时会跳过它们。
 */
class OnlineFetchWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {

    @EntryPoint
    @InstallIn(SingletonComponent::class)
    interface Dependencies {
        fun trackDao(): TrackDao
        fun fetcher(): OnlineFetcher
        fun settings(): SettingsRepository
        fun misses(): CoverMissLog
    }

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val deps = EntryPointAccessors.fromApplication(applicationContext, Dependencies::class.java)
        val s = deps.settings().current()
        val forced = kindsOf(inputData.getString(KEY_FORCED))
        // 自动获取的：排队后可能改了设置，或者现在的网络不允许（仅 Wi-Fi）
        val kinds = kindsOf(inputData.getString(KEY_KINDS)).filter { kind ->
            enabled(s, kind) && (kind in forced || networkAllows(applicationContext, fetchMode(s, kind)))
        }.toSet()
        if (kinds.isEmpty()) return@withContext Result.success()
        val targetIds = inputData.getString(KEY_TARGETS)?.let(ListDownloadScheduler::readTargets)
        val explicit = targetIds != null
        val fetcher = deps.fetcher()

        setProgress(workDataOf(KEY_CHECKING to true))
        val tracks = targetIds?.chunked(SQL_CHUNK)?.flatMap { deps.trackDao().byIds(it) } ?: deps.trackDao().all()
        // 要封面时按专辑分组（封面按专辑共用）；只要歌词时每首一组。按目录排，目录列表的缓存才能命中
        val groups = tracks.sortedWith(compareBy({ it.folderId }, { it.relativePath }))
            .groupBy { if (DownloadKind.COVERS in kinds) it.albumKey ?: it.trackKey else it.trackKey }
        val plans = groups.values.mapNotNull { group ->
            if (isStopped) null else fetcher.plan(group, kinds, explicit, quick = false)
        }
        val coversMissing = plans.count { it.coverTargets.isNotEmpty() }
        val lyricsMissing = plans.sumOf { it.lyricsTargets.size }

        var done = 0
        var coversFound = 0
        var extracted = 0
        var lyricsFound = 0
        fun progress() = workDataOf(
            KEY_DONE to done, KEY_TOTAL to plans.size,
            KEY_COVERS_FOUND to coversFound, KEY_COVERS_MISSING to coversMissing,
            KEY_LYRICS_FOUND to lyricsFound, KEY_LYRICS_MISSING to lyricsMissing,
        )
        val misses = deps.misses()
        for (plan in plans) {
            if (isStopped) break
            setProgress(progress())
            val outcome = fetcher.execute(plan, explicit)
            if (outcome.coverSaved) coversFound++
            if (outcome.coverExtracted) extracted++
            lyricsFound += outcome.lyricsSaved
            done++
        }
        misses.save()
        Log.i(TAG, "$kinds: covers $coversFound of $coversMissing ($extracted from the same album), lyrics $lyricsFound of $lyricsMissing, $done of ${plans.size} checked")
        if (isStopped) return@withContext Result.retry()
        Result.success(progress())
    }

    companion object {
        const val TAG = "OnlineFetchWorker"
        const val KEY_KINDS = "kinds"
        const val KEY_FORCED = "forced"
        /** 只处理这些歌：保存歌曲 id 的文件路径（id 可能上千个，超出 WorkManager 输入数据的大小限制）。 */
        const val KEY_TARGETS = "targets"
        const val KEY_CHECKING = "checking"
        const val KEY_DONE = "done"
        const val KEY_TOTAL = "total"
        const val KEY_COVERS_FOUND = "covers_found"
        const val KEY_COVERS_MISSING = "covers_missing"
        const val KEY_LYRICS_FOUND = "lyrics_found"
        const val KEY_LYRICS_MISSING = "lyrics_missing"
        private const val SQL_CHUNK = 900

        fun kindsOf(value: String?): Set<DownloadKind> =
            value.orEmpty().split(',').mapNotNull { name -> DownloadKind.entries.firstOrNull { it.name == name } }.toSet()

        fun encode(kinds: Collection<DownloadKind>) = kinds.joinToString(",") { it.name }

        /** 区分任务里有哪些内容的标签：进度按它筛选，手动获取时据此合并。 */
        fun kindTag(kind: DownloadKind) = "kind:${kind.name}"
        fun forcedTag(kind: DownloadKind) = "forced:${kind.name}"

        fun enabled(s: Settings, kind: DownloadKind) = if (kind == DownloadKind.COVERS) s.onlineCovers else s.onlineLyrics
        fun fetchMode(s: Settings, kind: DownloadKind) = if (kind == DownloadKind.COVERS) s.coverFetchMode else s.lyricsFetchMode
    }
}

/**
 * 某一种内容（封面或歌词）的获取进度：正在运行，或最近一次运行的结果。
 * [waiting] 表示在排队等网络；[done] / [total] 是检查了几组；[missing] 是缺这种内容的（封面按专辑，歌词按首），[found] 是找到并保存的。
 */
data class FetchProgress(
    val running: Boolean,
    val waiting: Boolean,
    val checking: Boolean,
    val done: Int,
    val total: Int,
    val found: Int,
    val missing: Int,
) {
    companion object {
        fun of(info: WorkInfo, kind: DownloadKind): FetchProgress? {
            val running = info.state == WorkInfo.State.RUNNING
            val waiting = info.state == WorkInfo.State.ENQUEUED || info.state == WorkInfo.State.BLOCKED
            if (!running && !waiting && info.state != WorkInfo.State.SUCCEEDED) return null
            val data = if (info.state == WorkInfo.State.SUCCEEDED) info.outputData else info.progress
            val covers = kind == DownloadKind.COVERS
            return FetchProgress(
                running = running,
                waiting = waiting,
                checking = running && data.getBoolean(OnlineFetchWorker.KEY_CHECKING, false),
                done = data.getInt(OnlineFetchWorker.KEY_DONE, 0),
                total = data.getInt(OnlineFetchWorker.KEY_TOTAL, 0),
                found = data.getInt(if (covers) OnlineFetchWorker.KEY_COVERS_FOUND else OnlineFetchWorker.KEY_LYRICS_FOUND, 0),
                missing = data.getInt(if (covers) OnlineFetchWorker.KEY_COVERS_MISSING else OnlineFetchWorker.KEY_LYRICS_MISSING, 0),
            )
        }
    }
}

/**
 * 整个曲库的批量获取（需求 C-3、LY-8）。两个唯一任务：
 * - [WORK_NAME]：手动获取，和获取方式是"自动（任何网络）"的内容，要求联网
 * - [WIFI_WORK_NAME]：获取方式是"仅 Wi-Fi 下自动"的内容，等不计流量的网络
 * 封面和歌词的获取方式相同时它们在同一个任务里，同一个文件的封面和歌词一起写入。
 */
@Singleton
class OnlineFetchScheduler @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val settings: SettingsRepository,
) {
    private val workManager get() = WorkManager.getInstance(context)

    /** 手动获取：任何网络都行；正在进行或排队的其他内容合并进来一起做。 */
    suspend fun start(kind: DownloadKind) {
        val active = active(WORK_NAME)
        enqueue(WORK_NAME, kindsOf(active) + kind, forcedOf(active) + kind, NetworkType.CONNECTED, expedited = true)
    }

    /** 停止某一种内容的获取；同一任务里的另一种继续。 */
    suspend fun cancel(kind: DownloadKind) {
        for (name in listOf(WORK_NAME, WIFI_WORK_NAME)) {
            val active = active(name) ?: continue
            val kinds = kindsOf(active)
            if (kind !in kinds) continue
            if (kinds.size == 1) {
                workManager.cancelUniqueWork(name)
            } else {
                val network = if (name == WIFI_WORK_NAME) NetworkType.UNMETERED else NetworkType.CONNECTED
                enqueue(name, kinds - kind, forcedOf(active) - kind, network)
            }
        }
    }

    /**
     * 自动获取：App 启动、扫描到新歌、改设置时调用。手动模式或没开在线获取的内容不管；
     * 已有任务包含了要做的内容时保持不变。有的和最近没找到的都会跳过，所以重复调用不会重复联网。
     */
    suspend fun scheduleAuto() {
        val s = settings.current()
        fun auto(mode: FetchMode) = DownloadKind.entries.filter { OnlineFetchWorker.enabled(s, it) && OnlineFetchWorker.fetchMode(s, it) == mode }.toSet()
        ensure(WORK_NAME, auto(FetchMode.AUTO), NetworkType.CONNECTED)
        ensure(WIFI_WORK_NAME, auto(FetchMode.WIFI), NetworkType.UNMETERED)
    }

    /** 改了获取方式：去掉按旧方式排队、还没开始的任务，再按新方式安排。 */
    suspend fun onModeChanged() {
        for (name in listOf(WORK_NAME, WIFI_WORK_NAME)) {
            val info = active(name) ?: continue
            if (info.state != WorkInfo.State.RUNNING) workManager.cancelUniqueWork(name)
        }
        scheduleAuto()
    }

    private suspend fun ensure(name: String, kinds: Set<DownloadKind>, network: NetworkType) {
        if (kinds.isEmpty()) return
        val active = active(name)
        val current = kindsOf(active)
        if (current.containsAll(kinds)) return
        enqueue(name, current + kinds, forcedOf(active), network)
    }

    private fun enqueue(name: String, kinds: Set<DownloadKind>, forced: Set<DownloadKind>, network: NetworkType, expedited: Boolean = false) {
        val request = OneTimeWorkRequestBuilder<OnlineFetchWorker>()
            .setConstraints(Constraints.Builder().setRequiredNetworkType(network).build())
            .setInputData(workDataOf(OnlineFetchWorker.KEY_KINDS to OnlineFetchWorker.encode(kinds), OnlineFetchWorker.KEY_FORCED to OnlineFetchWorker.encode(forced)))
            .apply {
                kinds.forEach { addTag(OnlineFetchWorker.kindTag(it)) }
                forced.forEach { addTag(OnlineFetchWorker.forcedTag(it)) }
                // 用户在界面上点的，尽快开始
                if (expedited) setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
            }
            .build()
        workManager.enqueueUniqueWork(name, ExistingWorkPolicy.REPLACE, request)
    }

    private suspend fun active(name: String): WorkInfo? = withContext(Dispatchers.IO) {
        workManager.getWorkInfosForUniqueWork(name).get().firstOrNull { !it.state.isFinished }
    }

    private fun kindsOf(info: WorkInfo?) = DownloadKind.entries.filter { info != null && OnlineFetchWorker.kindTag(it) in info.tags }.toSet()
    private fun forcedOf(info: WorkInfo?) = DownloadKind.entries.filter { info != null && OnlineFetchWorker.forcedTag(it) in info.tags }.toSet()

    /** 某种内容当前或最近一次的进度；从没运行过时为 null。正在运行的优先，其次是在等网络的。 */
    fun progress(kind: DownloadKind): Flow<FetchProgress?> = combine(
        workManager.getWorkInfosForUniqueWorkFlow(WORK_NAME),
        workManager.getWorkInfosForUniqueWorkFlow(WIFI_WORK_NAME),
    ) { a, b ->
        val infos = (a + b).filter { OnlineFetchWorker.kindTag(kind) in it.tags }.mapNotNull { FetchProgress.of(it, kind) }
        infos.firstOrNull { it.running } ?: infos.firstOrNull { it.waiting } ?: infos.firstOrNull()
    }

    companion object {
        const val WORK_NAME = "online-fetch"
        const val WIFI_WORK_NAME = "online-fetch-wifi"
    }
}
