package com.tinyfish.jeekmusic.baselineprofile

import androidx.benchmark.macro.BaselineProfileMode
import androidx.benchmark.macro.CompilationMode
import androidx.benchmark.macro.StartupMode
import androidx.benchmark.macro.StartupTimingMetric
import androidx.benchmark.macro.junit4.MacrobenchmarkRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * 冷启动耗时：对比不编译和使用 Baseline Profile 两种情况。
 * ANDROID_SERIAL=<真机序列号> ./gradlew :baselineprofile:connectedBenchmarkReleaseAndroidTest（在真机上跑，模拟器的数据没有参考价值）
 *
 * 结果里的 timeToInitialDisplayMs 即 TTID，timeToFullDisplayMs 即 TTFD（由 ReportDrawnWhen 上报）。
 */
@RunWith(AndroidJUnit4::class)
class StartupBenchmarks {

    @get:Rule
    val rule = MacrobenchmarkRule()

    @Test
    fun startupCompilationNone() = startup(CompilationMode.None())

    @Test
    fun startupBaselineProfile() = startup(CompilationMode.Partial(BaselineProfileMode.Require))

    private fun startup(compilationMode: CompilationMode) = rule.measureRepeated(
        packageName = TARGET_PACKAGE,
        metrics = listOf(StartupTimingMetric()),
        compilationMode = compilationMode,
        startupMode = StartupMode.COLD,
        iterations = 10,
        setupBlock = {
            device.executeShellCommand("pm grant $TARGET_PACKAGE android.permission.READ_MEDIA_AUDIO")
            pressHome()
        },
    ) {
        startActivityAndWait()
    }
}
