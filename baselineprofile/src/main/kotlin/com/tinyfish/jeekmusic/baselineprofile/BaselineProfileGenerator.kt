package com.tinyfish.jeekmusic.baselineprofile

import androidx.benchmark.macro.MacrobenchmarkScope
import androidx.benchmark.macro.junit4.BaselineProfileRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.uiautomator.By
import androidx.test.uiautomator.Direction
import androidx.test.uiautomator.Until
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * 生成 Baseline Profile 和 Startup Profile：
 * ANDROID_SERIAL=emulator-5554 ./gradlew :app:generateReleaseBaselineProfile \
 *     -Pandroid.injected.androidTest.leaveApksInstalledAfterRun=true
 *
 * 优先在模拟器上生成（不需要在手机上确认安装），生成的 profile 与 CPU 架构无关。
 * 前提：模拟器上的 App 已经添加过音乐文件夹（覆盖安装会保留数据），测试素材见 tools/push-testdata.sh。
 *
 * 覆盖的关键路径（docs/design.md §9.4）：冷启动、打开文件夹并滚动歌曲列表、切换到艺术家标签并滚动。
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

        if (device.wait(Until.hasObject(By.res("folder_list")), 5_000)) {
            device.findObject(By.res("folder_list")).children.firstOrNull()?.click()
            scrollList("folder_content")
            device.pressBack()
        }
        device.findObject(By.res("tab_1"))?.click()
        scrollList("artist_list")
    }

    private fun MacrobenchmarkScope.scrollList(tag: String) {
        if (!device.wait(Until.hasObject(By.res(tag)), 5_000)) return
        val list = device.findObject(By.res(tag))
        list.setGestureMargin(device.displayWidth / 5)
        list.fling(Direction.DOWN)
        device.waitForIdle()
        list.fling(Direction.UP)
        device.waitForIdle()
    }
}

internal const val TARGET_PACKAGE = "com.tinyfish.jeekmusic"
