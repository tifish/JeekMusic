package com.tinyfish.jeekmusic.baselineprofile

import androidx.benchmark.macro.junit4.BaselineProfileRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.filters.LargeTest
import androidx.test.uiautomator.By
import androidx.test.uiautomator.Direction
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * 生成 Baseline Profile 和 Startup Profile：
 * ./gradlew :app:generateReleaseBaselineProfile（需要连接真机）
 *
 * 覆盖的关键路径：冷启动、滚动歌曲列表。后续里程碑补充切换标签、打开播放页（docs/design.md §9.4）。
 */
@RunWith(AndroidJUnit4::class)
@LargeTest
class BaselineProfileGenerator {

    @get:Rule
    val rule = BaselineProfileRule()

    @Test
    fun generate() = rule.collect(
        packageName = TARGET_PACKAGE,
        includeInStartupProfile = true,
    ) {
        pressHome()
        startActivityAndWait()

        device.findObject(By.res("track_list"))?.let { list ->
            list.setGestureMargin(device.displayWidth / 5)
            list.fling(Direction.DOWN)
            device.waitForIdle()
            list.fling(Direction.UP)
        }
    }
}

internal const val TARGET_PACKAGE = "com.tinyfish.jeekmusic"
