package com.tinyfish.jeekmusic.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.activity.compose.LocalActivity
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat
import androidx.compose.ui.platform.LocalContext
import com.tinyfish.jeekmusic.data.settings.ThemeMode

/**
 * minSdk 31，动态取色总是可用。颜色来自系统壁纸，不按封面取色（省电，见需求 U-1）。
 */
@Composable
fun JeekTheme(
    themeMode: ThemeMode = ThemeMode.SYSTEM,
    content: @Composable () -> Unit,
) {
    val dark = when (themeMode) {
        ThemeMode.SYSTEM -> isSystemInDarkTheme()
        ThemeMode.LIGHT -> false
        ThemeMode.DARK -> true
    }
    val context = LocalContext.current
    val colorScheme = if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)

    // 系统栏图标颜色跟随 App 的主题，而不是系统的深浅色模式
    val view = LocalView.current
    if (!view.isInEditMode) {
        val window = LocalActivity.current?.window
        SideEffect {
            if (window != null) {
                WindowCompat.getInsetsController(window, view).run {
                    isAppearanceLightStatusBars = !dark
                    isAppearanceLightNavigationBars = !dark
                }
            }
        }
    }
    MaterialTheme(colorScheme = colorScheme, content = content)
}
