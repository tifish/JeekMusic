package com.tinyfish.jeekmusic.baselineprofile

import androidx.benchmark.macro.junit4.BaselineProfileRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.uiautomator.By
import androidx.test.uiautomator.Direction
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * 生成 Baseline Profile 和 Startup Profile：
 * ANDROID_SERIAL=emulator-5554 ./gradlew :app:generateReleaseBaselineProfile
 *
 * 优先在模拟器上生成（不需要在手机上确认安装），生成的 profile 与 CPU 架构无关。
 * 设备上需要有一些音频文件，列表才有内容可滚动，见 tools/push-testdata.sh。
 *
 * 覆盖的关键路径：冷启动、滚动歌曲列表。后续里程碑补充切换标签、打开播放页（docs/design.md §9.4）。
 */
@RunWith(AndroidJUnit4::class)
class BaselineProfileGenerator {

    @get:Rule
    val rule = BaselineProfileRule()

    @Test
    fun generate() = rule.collect(
        packageName = TARGET_PACKAGE,
        includeInStartupProfile = true,
    ) {
        // 每次生成都会重新安装 App，权限会被重置
        device.executeShellCommand("pm grant $TARGET_PACKAGE android.permission.READ_MEDIA_AUDIO")
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
