package com.tinyfish.jeekmusic

import android.app.Application
import dagger.hilt.android.HiltAndroidApp

/**
 * 启动关键路径上的类：onCreate 里不要做任何同步的重活（见 docs/design.md §9）。
 */
@HiltAndroidApp
class JeekApp : Application()
