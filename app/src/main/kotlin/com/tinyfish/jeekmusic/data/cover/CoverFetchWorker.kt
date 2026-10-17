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
 * 批量获取缺失的封面（需求 C-3，docs/design.md §7.4）：按专辑（没有专辑时按歌曲）逐个检查，
 * 没有封面的在线搜索，只接受歌名和艺术家都对得上的结果。
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

        setProgress(workDataOf(KEY_CHECKING to true))
        val groups = deps.trackDao().all().groupBy { it.albumKey ?: it.trackKey }
        val missing = groups.filter { (key, tracks) -> !isStopped && !misses.recent(key) && !deps.coverStore().hasCover(tracks.first()) }
        var done = 0
        var found = 0
        for ((key, tracks) in missing) {
            if (isStopped) break
            setProgress(workDataOf(KEY_DONE to done, KEY_TOTAL to missing.size, KEY_FOUND to found))
            done++
            // 没有艺术家的歌只凭歌名很容易配错，不自动获取
            val track = tracks.firstOrNull { it.artistDisplay.isNotBlank() }
            if (track == null) {
                misses.add(key)
                continue
            }
            val result = runCatching { deps.coverClient().fetchBest(track.titleTag ?: track.title, track.artistDisplay, track.album) }
                .onFailure { Log.w(TAG, "fetch failed: ${track.title}", it) }
            val image = result.getOrNull()
            when {
                image != null && deps.coverStore().save(track, image, settings.coverSaveMode) != null -> found++
                // 网络出错的不算"找不到"，下次还会再试
                result.isSuccess -> misses.add(key)
            }
        }
        misses.save()
        Log.i(TAG, "covers: $found found, $done checked, ${missing.size} missing")
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
        private const val MISSES_FILE = "cover_misses.tsv"
        private const val RETRY_AFTER_MS = 30L * 24 * 3600 * 1000
    }
}

/** 批量获取的进度：正在运行，或最近一次运行的结果。 */
data class CoverFetchProgress(val running: Boolean, val checking: Boolean, val done: Int, val total: Int, val found: Int)

@Singleton
class CoverFetchScheduler @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    private val workManager get() = WorkManager.getInstance(context)

    /** 需要联网；已经在运行时保持不变。 */
    fun start() {
        val request = OneTimeWorkRequestBuilder<CoverFetchWorker>()
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .build()
        workManager.enqueueUniqueWork(WORK_NAME, ExistingWorkPolicy.KEEP, request)
    }

    fun cancel() {
        workManager.cancelUniqueWork(WORK_NAME)
    }

    /** 当前或最近一次的进度；从没运行过时为 null。 */
    val progress: Flow<CoverFetchProgress?> = workManager.getWorkInfosForUniqueWorkFlow(WORK_NAME).map { infos ->
        val info = infos.lastOrNull() ?: return@map null
        val running = !info.state.isFinished
        val data = if (info.state == WorkInfo.State.SUCCEEDED) info.outputData else info.progress
        CoverFetchProgress(
            running = running,
            checking = running && data.getBoolean(CoverFetchWorker.KEY_CHECKING, false),
            done = data.getInt(CoverFetchWorker.KEY_DONE, 0),
            total = data.getInt(CoverFetchWorker.KEY_TOTAL, 0),
            found = data.getInt(CoverFetchWorker.KEY_FOUND, 0),
        ).takeIf { running || info.state == WorkInfo.State.SUCCEEDED }
    }

    companion object {
        const val WORK_NAME = "cover-fetch"
    }
}
