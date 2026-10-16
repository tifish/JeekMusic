package com.tinyfish.jeekmusic

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import com.tinyfish.jeekmusic.dev.DevScreen
import com.tinyfish.jeekmusic.ui.theme.JeekTheme
import dagger.hilt.android.AndroidEntryPoint

@AndroidEntryPoint
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        installSplashScreen()
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            JeekTheme {
                // M0：开发验证页面。M1 起替换为正式的主界面（标签栏 + 列表）。
                DevScreen()
            }
        }
    }
}
