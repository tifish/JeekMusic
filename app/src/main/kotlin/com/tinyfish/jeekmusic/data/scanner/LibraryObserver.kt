package com.tinyfish.jeekmusic.data.scanner

import android.content.Context
import android.database.ContentObserver
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 监听 MediaStore 音频变化，防抖 3 秒后触发增量扫描（docs/design.md §4.2）。
 * 只在 App 进程存活时生效；注册推迟到首帧之后（§9.3）。
 */
@Singleton
class LibraryObserver @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val scanner: LibraryScanner,
) {
    private val handler = Handler(Looper.getMainLooper())
    private val trigger = Runnable { scanner.requestScan() }
    private var registered = false

    private val observer = object : ContentObserver(handler) {
        override fun onChange(selfChange: Boolean, uri: Uri?) {
            handler.removeCallbacks(trigger)
            handler.postDelayed(trigger, DEBOUNCE_MS)
        }
    }

    fun register() {
        if (registered) return
        registered = true
        context.contentResolver.registerContentObserver(MediaStore.Audio.Media.EXTERNAL_CONTENT_URI, true, observer)
    }

    private companion object {
        const val DEBOUNCE_MS = 3_000L
    }
}
