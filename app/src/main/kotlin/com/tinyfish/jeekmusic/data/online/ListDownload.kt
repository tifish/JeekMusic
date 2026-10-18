package com.tinyfish.jeekmusic.data.online

import android.content.Context
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.WorkManager
import androidx.work.workDataOf
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import java.io.File
import javax.inject.Inject
import javax.inject.Singleton

/** 列表里批量下载的内容。 */
enum class DownloadKind { COVERS, LYRICS }

/**
 * 给某个列表（文件夹、艺术家、专辑、播放列表）里的歌批量下载封面或歌词（需求 C-6、LY-13，docs/design.md §8.4）。
 * 后台用 WorkManager 运行 [OnlineFetchWorker]（要求联网），每种各一个唯一任务，新的替换旧的；已经有封面 / 歌词的歌会跳过。
 */
@Singleton
class ListDownloadScheduler @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    private val workManager get() = WorkManager.getInstance(context)

    fun start(kind: DownloadKind, trackIds: List<Long>) {
        val file = targetsFile(kind)
        file.writeText(trackIds.distinct().joinToString("\n"))
        val kinds = OnlineFetchWorker.encode(listOf(kind))
        val input = workDataOf(OnlineFetchWorker.KEY_TARGETS to file.path, OnlineFetchWorker.KEY_KINDS to kinds, OnlineFetchWorker.KEY_FORCED to kinds)
        val request = OneTimeWorkRequestBuilder<OnlineFetchWorker>()
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
    fun progress(kind: DownloadKind): Flow<FetchProgress?> = workManager.getWorkInfosForUniqueWorkFlow(workName(kind)).map { infos ->
        infos.lastOrNull()?.let { FetchProgress.of(it, kind) }
    }

    private fun targetsFile(kind: DownloadKind) = File(context.filesDir, "download_targets_${kind.name.lowercase()}.txt")

    companion object {
        private fun workName(kind: DownloadKind) = "list-download-${kind.name.lowercase()}"

        fun readTargets(path: String): List<Long> = runCatching { File(path).readLines().mapNotNull { it.trim().toLongOrNull() } }.getOrDefault(emptyList())
    }
}
