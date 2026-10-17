package com.tinyfish.jeekmusic.data.online

import android.content.Context
import android.util.Log
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.Data
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.WorkInfo
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import com.tinyfish.jeekmusic.data.cover.CoverFetchProgress
import com.tinyfish.jeekmusic.data.cover.CoverFetchWorker
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository.FetchOutcome
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

/** 列表里批量下载的内容。 */
enum class DownloadKind { COVERS, LYRICS }

/**
 * 给某个列表（文件夹、艺术家、专辑、播放列表）里的歌批量下载封面或歌词（需求 C-6、LY-13，docs/design.md §8.4）。
 * 后台用 WorkManager 运行（要求联网），每种各一个唯一任务，新的替换旧的；已经有封面 / 歌词的歌会跳过。
 */
@Singleton
class ListDownloadScheduler @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    private val workManager get() = WorkManager.getInstance(context)

    fun start(kind: DownloadKind, trackIds: List<Long>) {
        val file = targetsFile(kind)
        file.writeText(trackIds.distinct().joinToString("\n"))
        val input = Data.Builder().putString(CoverFetchWorker.KEY_TARGETS, file.path).build()
        val builder = when (kind) {
            DownloadKind.COVERS -> OneTimeWorkRequestBuilder<CoverFetchWorker>()
            DownloadKind.LYRICS -> OneTimeWorkRequestBuilder<LyricsFetchWorker>()
        }
        val request = builder
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .setInputData(input)
            // 用户在界面上点的，尽快开始
            .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
            .build()
        workManager.enqueueUniqueWork(workName(kind), ExistingWorkPolicy.REPLACE, request)
    }

    fun cancel(kind: DownloadKind) {
        workManager.cancelUniqueWork(workName(kind))
    }

    /** 当前或最近一次的进度；从没运行过时为 null。 */
    fun progress(kind: DownloadKind): Flow<CoverFetchProgress?> = workManager.getWorkInfosForUniqueWorkFlow(workName(kind)).map { infos ->
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

    private fun targetsFile(kind: DownloadKind) = File(context.filesDir, "download_targets_${kind.name.lowercase()}.txt")

    companion object {
        private fun workName(kind: DownloadKind) = "list-download-${kind.name.lowercase()}"

        fun readTargets(path: String): List<Long> = runCatching { File(path).readLines().mapNotNull { it.trim().toLongOrNull() } }.getOrDefault(emptyList())
    }
}

/**
 * 批量下载歌词（需求 LY-13）：逐首检查，已经有歌词的跳过，没有的按设置的来源查询并按设置的位置保存。
 * 进度的格式和 [CoverFetchWorker] 一样。
 */
class LyricsFetchWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {

    @EntryPoint
    @InstallIn(SingletonComponent::class)
    interface Dependencies {
        fun trackDao(): TrackDao
        fun lyrics(): LyricsRepository
        fun settings(): com.tinyfish.jeekmusic.data.settings.SettingsRepository
    }

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val deps = EntryPointAccessors.fromApplication(applicationContext, Dependencies::class.java)
        if (!deps.settings().current().onlineLyrics) return@withContext Result.success()
        val ids = inputData.getString(CoverFetchWorker.KEY_TARGETS)?.let(ListDownloadScheduler::readTargets).orEmpty()
        val tracks = ids.chunked(SQL_CHUNK).flatMap { deps.trackDao().byIds(it) }
        var done = 0
        var found = 0
        for (track in tracks) {
            if (isStopped) break
            setProgress(workDataOf(CoverFetchWorker.KEY_DONE to done, CoverFetchWorker.KEY_TOTAL to tracks.size, CoverFetchWorker.KEY_FOUND to found))
            if (deps.lyrics().fetchMissing(track) == FetchOutcome.FOUND) found++
            done++
        }
        Log.i(TAG, "lyrics: $found found, $done of ${tracks.size} checked")
        if (isStopped) return@withContext Result.retry()
        Result.success(workDataOf(CoverFetchWorker.KEY_DONE to done, CoverFetchWorker.KEY_TOTAL to tracks.size, CoverFetchWorker.KEY_FOUND to found))
    }

    private companion object {
        const val TAG = "LyricsFetchWorker"
        const val SQL_CHUNK = 900
    }
}
