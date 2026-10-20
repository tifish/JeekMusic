package com.tinyfish.jeekmusic.data.online

import android.os.SystemClock
import com.tinyfish.jeekmusic.BuildConfig
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.IOException
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

/**
 * 在线歌词、封面共用的 HTTP 工具：系统自带的 HttpURLConnection，不引入网络库（docs/design.md §8.4）。
 * 都在 IO 线程上同步调用。
 */
object Http {
    /** MusicBrainz、LRCLIB 要求标明客户端。 */
    val APP_AGENT = "JeekMusic/${BuildConfig.VERSION_NAME} (https://github.com/tifish/JeekMusic)"

    /** 网易云音乐、QQ 音乐的网页接口按浏览器的请求头来。 */
    const val BROWSER_AGENT = "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130 Mobile Safari/537.36"

    private const val TIMEOUT_MS = 15_000
    private const val RETRIES = 2
    private const val RETRY_DELAY_MS = 2_000L

    /** 服务暂时不可用（503，多半是限流）。 */
    class Unavailable(url: String) : IOException("HTTP 503 for $url")

    /**
     * 返回响应体文本；404 返回 null；其他错误抛出异常。503 等一下重试（MusicBrainz、LRCLIB 偶尔会限流）。
     * [body] 不为 null 时用 POST 发送（表单）。
     */
    fun text(url: String, headers: Map<String, String> = emptyMap(), body: String? = null, timeoutMs: Int = TIMEOUT_MS): String? {
        repeat(RETRIES) {
            try {
                return connect(url, headers, body, timeoutMs) { it.readBytes().toString(Charsets.UTF_8) }
            } catch (_: Unavailable) {
                Thread.sleep(RETRY_DELAY_MS)
            }
        }
        return connect(url, headers, body, timeoutMs) { it.readBytes().toString(Charsets.UTF_8) }
    }

    /** 下载图片等二进制内容；失败时返回 null。 */
    fun bytes(url: String, headers: Map<String, String> = emptyMap(), timeoutMs: Int = TIMEOUT_MS): ByteArray? =
        runCatching { connect(url, headers, null, timeoutMs) { it.readBytes() } }.getOrNull()

    fun <T> connect(url: String, headers: Map<String, String>, body: String?, timeoutMs: Int, read: (InputStream) -> T): T? {
        val connection = URL(url).openConnection() as HttpURLConnection
        try {
            connection.connectTimeout = timeoutMs
            connection.readTimeout = timeoutMs
            connection.setRequestProperty("User-Agent", APP_AGENT)
            headers.forEach { (k, v) -> connection.setRequestProperty(k, v) }
            if (body != null) {
                connection.requestMethod = "POST"
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/x-www-form-urlencoded")
                connection.outputStream.use { it.write(body.toByteArray()) }
            }
            return when (val code = connection.responseCode) {
                200 -> connection.inputStream.use(read)
                404 -> null
                503 -> throw Unavailable(url)
                else -> throw IOException("HTTP $code for $url")
            }
        } finally {
            connection.disconnect()
        }
    }

    fun encode(value: String): String = URLEncoder.encode(value, "UTF-8")

    /** 两次请求之间至少间隔 [intervalMs]。 */
    class RateLimit(private val intervalMs: Long) {
        private val mutex = Mutex()
        private var last = 0L

        suspend fun <T> run(block: () -> T): T = mutex.withLock {
            val wait = last + intervalMs - SystemClock.elapsedRealtime()
            if (wait > 0) delay(wait)
            try {
                block()
            } finally {
                last = SystemClock.elapsedRealtime()
            }
        }
    }
}
