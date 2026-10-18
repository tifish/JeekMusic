package com.tinyfish.jeekmusic.data.library

import android.content.Context
import android.util.Log
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import com.tinyfish.jeekmusic.data.cover.CoverStore
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import com.tinyfish.jeekmusic.data.settings.CoverSaveMode
import com.tinyfish.jeekmusic.data.settings.LyricsSaveLocation
import com.tinyfish.jeekmusic.data.settings.Settings
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 保存位置是"写入音乐文件"时，把以前下载成文件的封面和歌词写进音乐文件，再删掉那些文件（需求 C-7、LY-14）。
 * 不用联网；全部处理完才记下"已写入"，被系统停止时下次接着做（已写入的文件已经删掉，不会重复）。
 *
 * 带 [KEY_TRACKS] 时只处理这几首：播放时推迟写入的歌，播放器用完后由播放服务安排（需求 P-14），不改"已写入"标记。
 */
class EmbedDownloadedWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {

    @EntryPoint
    @InstallIn(SingletonComponent::class)
    interface Dependencies {
        fun trackDao(): TrackDao
        fun coverStore(): CoverStore
        fun lyrics(): LyricsRepository
        fun settings(): SettingsRepository
    }

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val deps = EntryPointAccessors.fromApplication(applicationContext, Dependencies::class.java)
        val settings = deps.settings()
        val s = settings.current()
        if (!deps.coverStore().canEmbed()) return@withContext Result.success()
        inputData.getLongArray(KEY_TRACKS)?.let { ids ->
            val tracks = ids.toList().mapNotNull { deps.trackDao().byId(it) }
            if (s.coverSaveMode == CoverSaveMode.EMBED) deps.coverStore().embedDownloaded(tracks) { isStopped }
            if (s.lyricsSaveLocation == LyricsSaveLocation.EMBED) {
                // 重新读一遍：写入封面后文件大小变了
                deps.lyrics().embedDownloaded(ids.toList().mapNotNull { deps.trackDao().byId(it) }) { isStopped }
            }
            return@withContext Result.success()
        }
        if (needsCovers(s)) {
            val count = deps.coverStore().embedDownloaded(deps.trackDao().all()) { isStopped }
            Log.i(TAG, "embedded $count downloaded covers")
            if (isStopped) return@withContext Result.retry()
            settings.setCoversEmbedded()
        }
        if (needsLyrics(s)) {
            // 重新读一遍：写入封面后文件大小变了
            val count = deps.lyrics().embedDownloaded(deps.trackDao().all()) { isStopped }
            Log.i(TAG, "embedded $count downloaded lyrics")
            if (isStopped) return@withContext Result.retry()
            settings.setLyricsEmbedded()
        }
        Result.success()
    }

    companion object {
        private const val TAG = "EmbedDownloadedWorker"
        const val KEY_TRACKS = "tracks"

        fun needsCovers(s: Settings) = s.coverSaveMode == CoverSaveMode.EMBED && !s.coversEmbedded
        fun needsLyrics(s: Settings) = s.lyricsSaveLocation == LyricsSaveLocation.EMBED && !s.lyricsEmbedded
    }
}

@Singleton
class EmbedDownloadedScheduler @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val settings: SettingsRepository,
    private val editor: AudioFileEditor,
) {
    /**
     * App 启动、改了保存位置、刚授予"所有文件访问权限"时调用；没有要做的（或没有权限）时什么也不做。
     * 正在运行时排在它后面，免得漏掉刚改的设置。
     */
    suspend fun schedule() {
        val s = settings.current()
        if (!editor.canEdit() || !EmbedDownloadedWorker.needsCovers(s) && !EmbedDownloadedWorker.needsLyrics(s)) return
        WorkManager.getInstance(context)
            .enqueueUniqueWork(WORK_NAME, ExistingWorkPolicy.APPEND_OR_REPLACE, OneTimeWorkRequestBuilder<EmbedDownloadedWorker>().build())
    }

    /** 把播放时推迟写入的这几首歌写进文件（[PlayingFiles]），和其他写入任务排队执行。 */
    fun scheduleFor(trackIds: List<Long>) {
        if (trackIds.isEmpty() || !editor.canEdit()) return
        val request = OneTimeWorkRequestBuilder<EmbedDownloadedWorker>()
            .setInputData(workDataOf(EmbedDownloadedWorker.KEY_TRACKS to trackIds.toLongArray()))
            .build()
        WorkManager.getInstance(context).enqueueUniqueWork(WORK_NAME, ExistingWorkPolicy.APPEND_OR_REPLACE, request)
    }

    private companion object {
        const val WORK_NAME = "embed-downloaded"
    }
}
