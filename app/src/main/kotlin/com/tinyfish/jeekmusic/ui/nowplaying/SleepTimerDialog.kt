package com.tinyfish.jeekmusic.ui.nowplaying

import android.os.SystemClock
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.playback.SleepTimerState
import com.tinyfish.jeekmusic.ui.components.formatDuration
import kotlinx.coroutines.delay

private val MINUTE_OPTIONS = listOf(15, 30, 45, 60, 90)

/** 睡眠定时（需求 P-8）。 */
@Composable
fun SleepTimerDialog(viewModel: PlayerViewModel, timer: SleepTimerState, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.sleep_timer_title)) },
        text = {
            Column {
                MINUTE_OPTIONS.forEach { minutes ->
                    Option(pluralStringResource(R.plurals.sleep_minutes, minutes, minutes), Modifier.testTag("sleep_$minutes")) {
                        viewModel.sleepAfterMinutes(minutes)
                        onDismiss()
                    }
                }
                Option(stringResource(R.string.sleep_end_of_track), Modifier.testTag("sleep_end_of_track")) {
                    viewModel.sleepAtEndOfTrack()
                    onDismiss()
                }
                if (timer.active) {
                    Option(stringResource(R.string.sleep_cancel), Modifier.testTag("sleep_cancel")) {
                        viewModel.cancelSleepTimer()
                        onDismiss()
                    }
                }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}

/** 标题栏上的紧凑倒计时：只显示剩余时间，"播完这首"时显示"本首"。 */
@Composable
fun SleepTimerCountdown(timer: SleepTimerState, modifier: Modifier = Modifier) {
    if (!timer.active) return
    Text(
        if (timer.endOfTrack) stringResource(R.string.sleep_short_end_of_track) else formatDuration(rememberSleepRemaining(timer)),
        modifier.testTag("top_sleep_remaining"),
        style = MaterialTheme.typography.labelMedium,
        color = MaterialTheme.colorScheme.primary,
    )
}

/** 睡眠定时的剩余毫秒数，每秒刷新一次。 */
@Composable
private fun rememberSleepRemaining(timer: SleepTimerState): Long {
    var now by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    LaunchedEffect(timer.endElapsedMs) {
        while (true) {
            now = SystemClock.elapsedRealtime()
            delay(1_000)
        }
    }
    return (timer.endElapsedMs - now).coerceAtLeast(0)
}

@Composable
private fun Option(text: String, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Text(text, modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = 12.dp), style = MaterialTheme.typography.bodyLarge)
}

/** 播放页上的剩余时间提示；只在定时开启时每秒刷新。 */
@Composable
fun SleepTimerLabel(timer: SleepTimerState, modifier: Modifier = Modifier) {
    if (!timer.active) return
    if (timer.endOfTrack) {
        Text(stringResource(R.string.sleep_remaining_end_of_track), modifier, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
        return
    }
    Text(
        stringResource(R.string.sleep_remaining, formatDuration(rememberSleepRemaining(timer))),
        modifier.testTag("sleep_remaining"),
        style = MaterialTheme.typography.labelMedium,
        color = MaterialTheme.colorScheme.primary,
    )
}
