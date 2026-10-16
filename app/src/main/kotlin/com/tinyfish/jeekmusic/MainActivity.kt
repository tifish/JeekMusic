package com.tinyfish.jeekmusic

import android.os.Bundle
import android.os.SystemClock
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import com.tinyfish.jeekmusic.ui.main.JeekMusicApp
import com.tinyfish.jeekmusic.ui.main.MainViewModel
import dagger.hilt.android.AndroidEntryPoint

@AndroidEntryPoint
class MainActivity : ComponentActivity() {

    private val viewModel: MainViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        val splash = installSplashScreen()
        super.onCreate(savedInstanceState)
        // 启动快照读完之前保持启动画面，避免首帧闪一下空白标签栏；最多等 200ms（docs/design.md §9.2）
        val deadline = SystemClock.uptimeMillis() + 200
        splash.setKeepOnScreenCondition { !viewModel.ready.value && SystemClock.uptimeMillis() < deadline }
        enableEdgeToEdge()
        setContent { JeekMusicApp(viewModel) }
    }
}
