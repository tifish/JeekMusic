package com.tinyfish.jeekmusic.update

import android.util.Log
import com.tinyfish.jeekmusic.BuildConfig
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
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
    private val settings: SettingsRepository,
    val installer: UpdateInstaller,
    val downloads: UpdateDownload,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val _state = installer.state
    val state: StateFlow<UpdateState> = _state.asStateFlow()

    private val checking = Mutex()
    private var lastAutomaticAttempt = 0L
    private var delayedCheck: Job? = null
    private var prefetch: Job? = null

    init {
        scope.launch { downloads.wifi.collect { if (it != null) scheduleCheck() } }
        scope.launch {
            settings.settings.map { it.autoUpdateCheck }.distinctUntilChanged().collect {
                downloads.automaticEnabled = it
                if (it) scheduleCheck()
            }
        }
    }

    @Synchronized
    private fun scheduleCheck() {
        if (delayedCheck?.isActive == true) return
        delayedCheck = scope.launch { delay(5_000); checkIfDue() }
    }

    /** Startup, foreground and Wi-Fi return share the daily check and short failure backoff. */
    suspend fun checkIfDue() = checking.withLock {
        installer.reconcile()
        val current = settings.current()
        downloads.automaticEnabled = current.autoUpdateCheck
        installer.pendingDownload()?.takeIf { it.versionCode != current.skippedUpdateVersion }?.let { prepareDownload(it) }
        if (!current.autoUpdateCheck) return@withLock
        if (state.value != UpdateState.Idle) return@withLock
        val now = System.currentTimeMillis()
        if (now - current.lastUpdateCheck in 0 until CHECK_INTERVAL_MS ||
            now - lastAutomaticAttempt in 0 until RETRY_INTERVAL_MS) return@withLock
        lastAutomaticAttempt = now
        val info = latestOrNull() ?: return@withLock
        settings.setLastUpdateCheck(System.currentTimeMillis())
        if (state.value == UpdateState.Idle && info.versionCode > BuildConfig.VERSION_CODE && info.versionCode != current.skippedUpdateVersion) {
            installer.offer(info)
            prepareDownload(info)
        }
    }

    /** 设置里的"检查更新"：忽略检查间隔和"跳过此版本"。 */
    suspend fun checkNow(): CheckResult = checking.withLock {
        val info = latestOrNull() ?: return@withLock CheckResult.FAILED
        settings.setLastUpdateCheck(System.currentTimeMillis())
        if (info.versionCode <= BuildConfig.VERSION_CODE) return@withLock CheckResult.UP_TO_DATE
        if (state.value is UpdateState.Installing || state.value is UpdateState.Downloading ||
            state.value is UpdateState.ConfirmationRequired || state.value is UpdateState.PermissionRequired) return@withLock CheckResult.UPDATE_AVAILABLE
        installer.offer(info)
        downloads.automaticEnabled = settings.current().autoUpdateCheck
        prepareDownload(info) // A verified cache can be shown even when automatic downloads are off.
        CheckResult.UPDATE_AVAILABLE
    }

    private suspend fun latestOrNull(): UpdateInfo? = try { fetchLatest() }
        catch (cancelled: CancellationException) { throw cancelled }
        catch (error: Exception) { Log.i(TAG, "update check failed: ${error.javaClass.simpleName}"); null }

    @Synchronized
    private fun prepareDownload(info: UpdateInfo) {
        if (prefetch?.isActive == true) return
        prefetch = scope.launch {
            try { downloads.obtain(info, automatic = true) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) { Log.i(TAG, "update preparation paused: ${error.javaClass.simpleName}") }
        }
    }

    /** 关闭更新提示；[skip] 为 true 时以后不再自动提示这个版本。 */
    fun dismiss(skip: Boolean) {
        val info = (state.value as? UpdateState.Available)?.info ?: (state.value as? UpdateState.Failed)?.info
        installer.dismiss()
        if (skip && info != null) {
            prefetch?.cancel()
            downloads.stopAutomatic()
            scope.launch { settings.setSkippedUpdateVersion(info.versionCode) }
        }
    }

    @Synchronized
    fun downloadAndInstall(info: UpdateInfo) {
        if (state.value is UpdateState.Downloading || state.value is UpdateState.Installing || state.value is UpdateState.ConfirmationRequired) return
        installer.downloading(info)
        scope.launch {
            try {
                val apk = downloads.obtain(info, automatic = false)
                installer.install(info, apk)
            } catch (cancelled: CancellationException) {
                throw cancelled
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
            scheduleCheck()
        }
    }

    fun retryConfirmation() {
        val pending = state.value as? UpdateState.ConfirmationRequired ?: return
        installer.cancel()
        downloadAndInstall(pending.info)
    }

    fun diagnostics(): JSONObject = installer.diagnostics().put("download", JSONObject()
        .put("wifiConnected", downloads.wifi.value != null).put("automaticEnabled", downloads.automaticEnabled)
        .put("versionCode", downloads.status.value.versionCode).put("ready", downloads.status.value.ready)
        .put("progress", downloads.status.value.progress ?: JSONObject.NULL))

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

    private fun open(url: String): HttpURLConnection = (URL(url).openConnection() as HttpURLConnection).apply {
        connectTimeout = TIMEOUT_MS
        readTimeout = TIMEOUT_MS
        setRequestProperty("User-Agent", "JeekMusic/${BuildConfig.VERSION_NAME} (https://github.com/tifish/JeekMusic)")
    }

    private companion object {
        const val TAG = "AppUpdater"
        const val LATEST_URL = "https://api.github.com/repos/tifish/JeekMusic/releases/latest"
        const val CHECK_INTERVAL_MS = 24 * 60 * 60 * 1000L
        const val RETRY_INTERVAL_MS = 5 * 60 * 1000L
        const val TIMEOUT_MS = 15_000
        val VERSION_CODE = Regex("""\d+$""")
    }
}
