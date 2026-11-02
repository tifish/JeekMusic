package com.tinyfish.jeekmusic.update

import android.content.Context
import android.util.Log
import com.tinyfish.jeekmusic.BuildConfig
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import javax.inject.Inject
import javax.inject.Singleton

/** GitHub Releases 上的一个版本。 */
data class UpdateInfo(
    val versionCode: Int,
    val versionName: String,
    val notes: String,
    val apkUrl: String,
    val sizeBytes: Long,
    /** GitHub 给出的 SHA-256（十六进制）；没有时不校验。 */
    val sha256: String?,
)

sealed interface UpdateState {
    data object Idle : UpdateState
    data class Available(val info: UpdateInfo) : UpdateState
    data class Downloading(val info: UpdateInfo, val progress: Float) : UpdateState
    data class Installing(val info: UpdateInfo) : UpdateState
    data class PermissionRequired(val info: UpdateInfo) : UpdateState
    data class ConfirmationRequired(val info: UpdateInfo) : UpdateState
    data class Failed(val info: UpdateInfo, val message: String?) : UpdateState
}

enum class CheckResult { UPDATE_AVAILABLE, UP_TO_DATE, FAILED }

/**
 * 从 GitHub Releases 自动更新（需求 U-6，docs/design.md §13.2）：
 * - 每天最多自动检查一次（设置里可以关闭），也可以在设置里手动检查
 * - 版本号是 Git 提交数：标签为 v1.0.<versionCode>，比当前的 versionCode 大就是新版本
 * - 下载 APK 后用 PackageInstaller 安装；签名必须和已安装的一致，否则系统会拒绝
 */
@Singleton
class AppUpdater @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val settings: SettingsRepository,
    val installer: UpdateInstaller,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val _state = installer.state
    val state: StateFlow<UpdateState> = _state.asStateFlow()

    /** 启动后在后台调用：开关打开、且距上次检查超过一天时才联网。网络错误静默忽略。 */
    suspend fun checkIfDue() {
        installer.reconcile()
        if (state.value != UpdateState.Idle) return
        val current = settings.current()
        if (!current.autoUpdateCheck) return
        if (System.currentTimeMillis() - current.lastUpdateCheck < CHECK_INTERVAL_MS) return
        val info = runCatching { fetchLatest() }.onFailure { Log.i(TAG, "update check failed: ${it.javaClass.simpleName}") }.getOrNull() ?: return
        settings.setLastUpdateCheck(System.currentTimeMillis())
        if (state.value == UpdateState.Idle && info.versionCode > BuildConfig.VERSION_CODE && info.versionCode != current.skippedUpdateVersion) {
            _state.value = UpdateState.Available(info)
        }
    }

    /** 设置里的"检查更新"：忽略检查间隔和"跳过此版本"。 */
    suspend fun checkNow(): CheckResult {
        val info = runCatching { fetchLatest() }.onFailure { Log.i(TAG, "update check failed: ${it.javaClass.simpleName}") }.getOrNull()
            ?: return CheckResult.FAILED
        settings.setLastUpdateCheck(System.currentTimeMillis())
        if (info.versionCode <= BuildConfig.VERSION_CODE) return CheckResult.UP_TO_DATE
        if (state.value is UpdateState.Installing || state.value is UpdateState.Downloading ||
            state.value is UpdateState.ConfirmationRequired || state.value is UpdateState.PermissionRequired) return CheckResult.UPDATE_AVAILABLE
        _state.value = UpdateState.Available(info)
        return CheckResult.UPDATE_AVAILABLE
    }

    /** 关闭更新提示；[skip] 为 true 时以后不再自动提示这个版本。 */
    fun dismiss(skip: Boolean) {
        val info = (state.value as? UpdateState.Available)?.info ?: (state.value as? UpdateState.Failed)?.info
        installer.dismiss()
        if (skip && info != null) scope.launch { settings.setSkippedUpdateVersion(info.versionCode) }
    }

    @Synchronized
    fun downloadAndInstall(info: UpdateInfo) {
        if (state.value is UpdateState.Downloading || state.value is UpdateState.Installing || state.value is UpdateState.ConfirmationRequired) return
        installer.downloading(info)
        scope.launch {
            try {
                val apk = download(info)
                installer.install(info, apk)
            } catch (e: Exception) {
                installer.fail(info, (e as? UpdateFailure)?.code ?: "download_or_install_error")
            }
        }
    }

    /** Permission is checked before creating a session, so granting it does not leave an old
     * confirmation session waiting for a second system button press. */
    fun onForeground() {
        scope.launch {
            installer.reconcile()
            val pending = state.value as? UpdateState.PermissionRequired
            if (pending != null && installer.canInstall()) downloadAndInstall(pending.info)
        }
    }

    fun retryConfirmation() {
        val pending = state.value as? UpdateState.ConfirmationRequired ?: return
        installer.cancel()
        downloadAndInstall(pending.info)
    }

    private suspend fun fetchLatest(): UpdateInfo? = withContext(Dispatchers.IO) {
        val json = JSONObject(get(LATEST_URL))
        val tag = json.getString("tag_name")
        val versionCode = VERSION_CODE.find(tag)?.value?.toIntOrNull() ?: return@withContext null
        val assets = json.getJSONArray("assets")
        val apk = (0 until assets.length()).map { assets.getJSONObject(it) }.firstOrNull { it.getString("name").endsWith(".apk") }
            ?: return@withContext null
        UpdateInfo(
            versionCode = versionCode,
            versionName = tag.removePrefix("v"),
            notes = json.optString("body").trim(),
            apkUrl = apk.getString("browser_download_url"),
            sizeBytes = apk.optLong("size"),
            sha256 = apk.optString("digest").takeIf { it.startsWith("sha256:") }?.removePrefix("sha256:"),
        )
    }

    private fun get(url: String): String {
        val connection = open(url)
        try {
            connection.setRequestProperty("Accept", "application/vnd.github+json")
            if (connection.responseCode !in 200..299) error("HTTP ${connection.responseCode}")
            return connection.inputStream.bufferedReader().use { it.readText() }
        } finally {
            connection.disconnect()
        }
    }

    private fun updatesDir() = File(context.cacheDir, "updates")

    private fun download(info: UpdateInfo): File {
        val dir = updatesDir().apply { mkdirs() }
        val file = installer.apkFile
        dir.listFiles()?.filter { it != file }?.forEach { it.delete() }
        // 已经下载过（比如第一次安装时去设置里允许了"安装未知应用"，回来再点更新），校验通过就直接用
        if (file.length() == info.sizeBytes && runCatching { installer.validate(info, file) }.isSuccess) return file
        val digest = MessageDigest.getInstance("SHA-256")
        // GitHub 的下载地址会重定向到另一个 https 域名，HttpURLConnection 默认会跟随
        val connection = open(info.apkUrl)
        try {
            if (connection.responseCode !in 200..299) error("HTTP ${connection.responseCode}")
            val total = connection.contentLengthLong.takeIf { it > 0 } ?: info.sizeBytes
            connection.inputStream.use { input ->
                file.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var done = 0L
                    while (true) {
                        val n = input.read(buffer)
                        if (n < 0) break
                        output.write(buffer, 0, n)
                        digest.update(buffer, 0, n)
                        done += n
                        if (total > 0) _state.value = UpdateState.Downloading(info, (done.toFloat() / total).coerceIn(0f, 1f))
                    }
                }
            }
        } finally {
            connection.disconnect()
        }
        val actual = digest.digest().toHex()
        if (info.sha256 != null && !info.sha256.equals(actual, ignoreCase = true)) {
            file.delete()
            throw UpdateFailure("digest_mismatch")
        }
        if (info.sizeBytes <= 0 || file.length() != info.sizeBytes) {
            file.delete()
            throw UpdateFailure("size_mismatch")
        }
        return file
    }

    private fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val n = input.read(buffer)
                if (n < 0) break
                digest.update(buffer, 0, n)
            }
        }
        return digest.digest().toHex()
    }

    private fun ByteArray.toHex() = joinToString("") { "%02x".format(it) }

    private fun open(url: String): HttpURLConnection = (URL(url).openConnection() as HttpURLConnection).apply {
        connectTimeout = TIMEOUT_MS
        readTimeout = TIMEOUT_MS
        setRequestProperty("User-Agent", "JeekMusic/${BuildConfig.VERSION_NAME} (https://github.com/tifish/JeekMusic)")
    }

    private companion object {
        const val TAG = "AppUpdater"
        const val LATEST_URL = "https://api.github.com/repos/tifish/JeekMusic/releases/latest"
        const val CHECK_INTERVAL_MS = 24 * 60 * 60 * 1000L
        const val TIMEOUT_MS = 15_000
        val VERSION_CODE = Regex("""\d+$""")
    }
}
