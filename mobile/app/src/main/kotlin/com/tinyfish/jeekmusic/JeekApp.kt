package com.tinyfish.jeekmusic

import android.app.Application
import android.util.Log
import androidx.work.Configuration
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.startup.StartupSnapshot
import dagger.Lazy
import dagger.hilt.android.HiltAndroidApp
import javax.inject.Inject

/**
 * 启动关键路径上的类：onCreate 里不要做任何同步的重活（见 docs/design.md §9）。
 * WorkManager 按需初始化（第一次调度分析任务时），不在启动时初始化（§9.3）。
 */
@HiltAndroidApp
class JeekApp : Application(), Configuration.Provider {

    @Inject lateinit var snapshot: StartupSnapshot
    @Inject lateinit var database: Lazy<JeekDatabase>
    @Inject lateinit var lanSync: Lazy<com.tinyfish.jeekmusic.data.sync.LanSyncService>

    /**
     * 主线程上只发起两个后台任务，都不等待（docs/design.md §9.2）：
     * 读启动快照（冷启动时 DataStore 初始化要 100ms 以上）、打开数据库（模拟器上要几百毫秒）。
     * 界面需要它们时通常已经准备好了。
     */
    override fun onCreate() {
        super.onCreate()
        snapshot.preload()
        Thread({ runCatching { database.get().openHelper.writableDatabase; lanSync.get().startRecovery() } }, "db-warmup").start()
    }

    override val workManagerConfiguration: Configuration
        get() = Configuration.Builder().setMinimumLoggingLevel(Log.INFO).build()
}
