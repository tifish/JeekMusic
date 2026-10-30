package com.tinyfish.jeekmusic.analysis

import android.content.Context
import android.os.Process
import android.os.SystemClock
import android.util.Log
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.Data
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.tinyfish.jeekmusic.data.db.TagDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.db.TrackFeaturesEntity
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource
import com.tinyfish.jeekmusic.data.settings.AnalysisMode
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.concurrent.TimeUnit
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 后台分析（docs/design.md §6.5）。每次最多处理 300 首或 8 分钟，到上限后提交一个后续任务接着做；
 * 被系统停止时立即退出（已分析的结果都已保存）。
 */
class AnalysisWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {

    @EntryPoint
    @InstallIn(SingletonComponent::class)
    interface Dependencies {
        fun tagDao(): TagDao
        fun decoder(): PcmDecoder
        fun tagger(): AudioTagger
        fun tags(): TagRepository
        fun scheduler(): AnalysisScheduler
    }

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_BACKGROUND)
        val deps = EntryPointAccessors.fromApplication(applicationContext, Dependencies::class.java)
        val deadline = SystemClock.elapsedRealtime() + MAX_RUN_MS
        val batch = deps.tagDao().pending(TagRepository.ANALYZER_VERSION, MAX_TRACKS)
        var done = 0
        try {
            for (track in batch) {
                if (isStopped || SystemClock.elapsedRealtime() > deadline) break
                deps.tagDao().saveFeatures(analyze(deps.decoder(), deps.tagger(), track))
                done++
                if (done % 25 == 0) deps.tags().recomputeAll()
            }
        } finally {
            deps.tagger().close()
        }
        deps.tags().recomputeAll()
        Log.i(TAG, "analyzed $done tracks")
        // 被系统停止（比如拔掉了充电器）：交给 WorkManager 在条件满足时重试
        if (isStopped) return@withContext Result.retry()
        // 达到单次上限还有剩余：提交后续任务接着做
        if (batch.size == MAX_TRACKS || done < batch.size) {
            deps.scheduler().continueLater(inputData.getBoolean(KEY_IGNORE_CHARGING, false))
        }
        Result.success()
    }

    private suspend fun analyze(decoder: PcmDecoder, tagger: AudioTagger, track: TrackEntity): TrackFeaturesEntity {
        val uri = MediaStoreSource.contentUri(track.volumeName, track.mediaStoreId)
        return TrackAnalyzer.analyze(track,
            { window, check -> decoder.decode(uri, listOf(window), checkCancelled = check) },
            { pcm, check -> tagger.analyzeWindows(pcm, check).let { it.first?.let(AudioTagger::scores) to it.second } })
    }

    companion object {
        const val TAG = "AnalysisWorker"
        const val KEY_IGNORE_CHARGING = "ignore_charging"
        private const val MAX_TRACKS = 300
        private const val MAX_RUN_MS = 8 * 60_000L
    }
}

/** 调度分析任务（需求 M-5）：默认要求充电且电量不低；设置为"随时"时只要求电量不低。 */
@Singleton
class AnalysisScheduler @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val settings: SettingsRepository,
) {
    /** 扫描发现新歌、App 启动后调用；已有排队的任务时保持不变。 */
    suspend fun schedule() = enqueue(ignoreCharging = false, policy = ExistingWorkPolicy.KEEP)

    /** "立即分析"：不要求充电，替换掉正在等待充电的任务。 */
    suspend fun runNow() = enqueue(ignoreCharging = true, policy = ExistingWorkPolicy.REPLACE)

    suspend fun continueLater(ignoreCharging: Boolean) = enqueue(ignoreCharging, ExistingWorkPolicy.APPEND_OR_REPLACE)

    private suspend fun enqueue(ignoreCharging: Boolean, policy: ExistingWorkPolicy) {
        val anytime = ignoreCharging || settings.current().analysisMode == AnalysisMode.ANYTIME
        val constraints = Constraints.Builder()
            .setRequiresBatteryNotLow(true)
            .setRequiresCharging(!anytime)
            .build()
        val request = OneTimeWorkRequestBuilder<AnalysisWorker>()
            .setConstraints(constraints)
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 1, TimeUnit.MINUTES)
            .setInputData(Data.Builder().putBoolean(AnalysisWorker.KEY_IGNORE_CHARGING, ignoreCharging).build())
            .build()
        WorkManager.getInstance(context).enqueueUniqueWork(WORK_NAME, policy, request)
    }

    companion object {
        const val WORK_NAME = "mood-analysis"
    }
}
