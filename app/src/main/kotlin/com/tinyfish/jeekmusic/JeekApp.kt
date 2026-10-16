package com.tinyfish.jeekmusic

import android.app.Application
import android.util.Log
import androidx.work.Configuration
import dagger.hilt.android.HiltAndroidApp

/**
 * 启动关键路径上的类：onCreate 里不要做任何同步的重活（见 docs/design.md §9）。
 * WorkManager 按需初始化（第一次调度分析任务时），不在启动时初始化（§9.3）。
 */
@HiltAndroidApp
class JeekApp : Application(), Configuration.Provider {
    override val workManagerConfiguration: Configuration
        get() = Configuration.Builder().setMinimumLoggingLevel(Log.INFO).build()
}
