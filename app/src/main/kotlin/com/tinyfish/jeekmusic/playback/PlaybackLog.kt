package com.tinyfish.jeekmusic.playback

import android.content.Context
import android.util.Log
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 播放事件日志（docs/design.md §7.2）：换歌、出错、低功耗播放的状态，用来排查只在真机上出现的问题
 * （比如低功耗播放时歌没放完就换到下一首）。写在 App 的外部私有目录，连上电脑后可以取出：
 * `adb pull /sdcard/Android/data/com.tinyfish.jeekmusic/files/playback-log.txt`
 * 超过 [MAX_BYTES] 时只保留后一半。
 */
class PlaybackLog(context: Context) {
    private val file = File(context.getExternalFilesDir(null) ?: context.filesDir, "playback-log.txt")
    private val format = SimpleDateFormat("MM-dd HH:mm:ss", Locale.US)

    @Synchronized
    fun add(event: String) {
        Log.i(TAG, event)
        runCatching {
            if (file.length() > MAX_BYTES) {
                val text = file.readText()
                file.writeText(text.substring(text.length / 2).substringAfter('\n'))
            }
            file.appendText("${format.format(Date())} $event\n")
        }
    }

    private companion object {
        const val TAG = "PlaybackLog"
        const val MAX_BYTES = 256 * 1024L
    }
}
