package com.tinyfish.jeekmusic.ui.nowplaying

import com.tinyfish.jeekmusic.ui.components.verticalScrollWithScrollbar
import android.os.SystemClock
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Checkbox
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.playback.SleepTimerState
import com.tinyfish.jeekmusic.ui.components.formatDuration
import kotlinx.coroutines.delay

private val MINUTE_OPTIONS = listOf(15, 30, 45, 60, 90)
private const val MAX_MINUTES = 999
private const val MAX_SONGS = 99

/**
 * 睡眠定时（需求 P-8）：按时间（预设或自定义分钟数，可选"时间到后播完当前这首再停止"），
 * 或者播完 N 首后停止。上次的选择记在设置里，下次打开时沿用。
 */
@Composable
fun SleepTimerDialog(viewModel: PlayerViewModel, timer: SleepTimerState, onDismiss: () -> Unit) {
    val prefs by viewModel.sleepPrefs.collectAsStateWithLifecycle()
    var finishSong by remember(prefs) { mutableStateOf(prefs.finishSong) }
    var customText by remember(prefs) { mutableStateOf(prefs.customMinutes.toString()) }
    var songCount by remember(prefs) { mutableIntStateOf(prefs.songCount) }
    val customMinutes = customText.toIntOrNull()?.takeIf { it in 1..MAX_MINUTES }

    fun startMinutes(minutes: Int, custom: Boolean) {
        viewModel.sleepAfterMinutes(minutes, finishSong, custom)
        onDismiss()
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.sleep_timer_title)) },
        text = {
            Column(Modifier.verticalScrollWithScrollbar(rememberScrollState())) {
                SectionLabel(stringResource(R.string.sleep_by_time))
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    MINUTE_OPTIONS.forEach { minutes ->
                        AssistChip(
                            onClick = { startMinutes(minutes, custom = false) },
                            label = { Text(pluralStringResource(R.plurals.sleep_minutes, minutes, minutes)) },
                            modifier = Modifier.testTag("sleep_$minutes"),
                        )
                    }
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        value = customText,
                        onValueChange = { text -> customText = text.filter { it.isDigit() }.take(3) },
                        modifier = Modifier.width(120.dp).testTag("sleep_custom_minutes"),
                        label = { Text(stringResource(R.string.sleep_custom)) },
                        suffix = { Text(stringResource(R.string.sleep_minutes_unit)) },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        isError = customMinutes == null,
                    )
                    Spacer(Modifier.width(8.dp))
                    TextButton(
                        onClick = { customMinutes?.let { startMinutes(it, custom = true) } },
                        enabled = customMinutes != null,
                        modifier = Modifier.testTag("sleep_custom_start"),
                    ) { Text(stringResource(R.string.sleep_start)) }
                }
                Row(
                    Modifier.fillMaxWidth().clickable { finishSong = !finishSong }.padding(vertical = 4.dp).testTag("sleep_finish_song"),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Checkbox(checked = finishSong, onCheckedChange = { finishSong = it })
                    Text(stringResource(R.string.sleep_finish_song), style = MaterialTheme.typography.bodyMedium)
                }

                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                SectionLabel(stringResource(R.string.sleep_by_songs))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.sleep_songs_prefix), style = MaterialTheme.typography.bodyLarge)
                    IconButton(onClick = { songCount = (songCount - 1).coerceAtLeast(1) }, enabled = songCount > 1, modifier = Modifier.testTag("sleep_songs_minus")) {
                        Text("−", style = MaterialTheme.typography.titleLarge)
                    }
                    Text(songCount.toString(), Modifier.testTag("sleep_songs_count"), style = MaterialTheme.typography.titleMedium)
                    IconButton(onClick = { songCount = (songCount + 1).coerceAtMost(MAX_SONGS) }, enabled = songCount < MAX_SONGS, modifier = Modifier.testTag("sleep_songs_plus")) {
                        Text("+", style = MaterialTheme.typography.titleLarge)
                    }
                    Text(stringResource(R.string.sleep_songs_suffix), Modifier.weight(1f), style = MaterialTheme.typography.bodyLarge)
                    TextButton(
                        onClick = { viewModel.sleepAfterSongs(songCount); onDismiss() },
                        modifier = Modifier.testTag("sleep_songs_start"),
                    ) { Text(stringResource(R.string.sleep_start)) }
                }

                if (timer.active) {
                    HorizontalDivider(Modifier.padding(vertical = 8.dp))
                    Text(
                        stringResource(R.string.sleep_cancel),
                        Modifier.fillMaxWidth().clickable { viewModel.cancelSleepTimer(); onDismiss() }.padding(vertical = 12.dp).testTag("sleep_cancel"),
                        style = MaterialTheme.typography.bodyLarge,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}

@Composable
private fun SectionLabel(text: String) {
    Text(text, Modifier.padding(bottom = 4.dp), style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
}

/** 标题栏上的紧凑提示：按时间时显示剩余时间；按歌曲数时显示"本首"或"N 首"。 */
@Composable
fun SleepTimerCountdown(timer: SleepTimerState, modifier: Modifier = Modifier) {
    if (!timer.active) return
    Text(
        when {
            timer.endElapsedMs > 0 -> formatDuration(rememberSleepRemaining(timer))
            timer.songsLeft == 1 -> stringResource(R.string.sleep_short_end_of_track)
            else -> pluralStringResource(R.plurals.sleep_short_songs, timer.songsLeft, timer.songsLeft)
        },
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


/** 播放页上的剩余时间提示；只在定时开启时每秒刷新。 */
@Composable
fun SleepTimerLabel(timer: SleepTimerState, modifier: Modifier = Modifier) {
    if (!timer.active) return
    val text = when {
        timer.endElapsedMs > 0 -> stringResource(
            if (timer.finishSong) R.string.sleep_remaining_finish_song else R.string.sleep_remaining,
            formatDuration(rememberSleepRemaining(timer)),
        )
        timer.songsLeft == 1 -> stringResource(R.string.sleep_remaining_end_of_track)
        else -> pluralStringResource(R.plurals.sleep_remaining_songs, timer.songsLeft, timer.songsLeft)
    }
    Text(text, modifier.testTag("sleep_remaining"), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
}
