package com.tinyfish.jeekmusic.ui.moods

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.ui.components.CenteredMessage
import com.tinyfish.jeekmusic.ui.navigation.TabRoute

/** 智能标签页（需求 §3.6）。音频分析在 M4 实现，之前先显示说明。 */
@Composable
fun MoodScreen(@Suppress("UNUSED_PARAMETER") route: TabRoute.MoodTracks) {
    CenteredMessage(stringResource(R.string.mood_not_ready))
}
