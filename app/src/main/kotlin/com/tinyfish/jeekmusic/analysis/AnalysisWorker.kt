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
import com.tinyfish.jeekmusic.data.db.MoodDao
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
        fun moodDao(): MoodDao
        fun decoder(): PcmDecoder
        fun moods(): MoodRepository
        fun scheduler(): AnalysisScheduler
    }

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_BACKGROUND)
        val deps = EntryPointAccessors.fromApplication(applicationContext, Dependencies::class.java)
        val deadline = SystemClock.elapsedRealtime() + MAX_RUN_MS
        val batch = deps.moodDao().pending(MoodRepository.ANALYZER_VERSION, MAX_TRACKS)
        var done = 0
        for (track in batch) {
            if (isStopped || SystemClock.elapsedRealtime() > deadline) break
            deps.moodDao().saveFeatures(analyze(deps.decoder(), track))
            done++
            if (done % 25 == 0) deps.moods().recomputeAll()
        }
        deps.moods().recomputeAll()
        Log.i(TAG, "analyzed $done tracks")
        // 被系统停止（比如拔掉了充电器）：交给 WorkManager 在条件满足时重试
        if (isStopped) return@withContext Result.retry()
        // 达到单次上限还有剩余：提交后续任务接着做
        if (batch.size == MAX_TRACKS || done < batch.size) {
            deps.scheduler().continueLater(inputData.getBoolean(KEY_IGNORE_CHARGING, false))
        }
        Result.success()
    }

    private fun analyze(decoder: PcmDecoder, track: TrackEntity): TrackFeaturesEntity {
        val signature = "${track.dateModified}:${track.size}"
        val uri = MediaStoreSource.contentUri(track.volumeName, track.mediaStoreId)
        val pcm = decoder.decode(uri, decoder.windows(track.startMs ?: 0, track.durationMs))
        val f = pcm?.let { runCatching { FeatureExtractor.extract(it) }.getOrNull() }
        return TrackFeaturesEntity(
            trackKey = track.trackKey,
            signature = signature,
            analyzerVersion = MoodRepository.ANALYZER_VERSION,
            bpm = f?.bpm,
            bpmConfidence = f?.bpmConfidence ?: 0.0,
            loudness = f?.loudness ?: -70.0,
            loudnessRange = f?.loudnessRange ?: 0.0,
            onsetRate = f?.onsetRate ?: 0.0,
            centroidHz = f?.centroidHz ?: 0.0,
            lowRatio = f?.lowRatio ?: 0.0,
            analyzedAt = System.currentTimeMillis(),
            failed = f == null,
        )
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
