package com.tinyfish.jeekmusic.update

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.util.Log
import com.tinyfish.jeekmusic.BuildConfig
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
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
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val _state = MutableStateFlow<UpdateState>(UpdateState.Idle)
    val state: StateFlow<UpdateState> = _state.asStateFlow()

    /** 启动后在后台调用：开关打开、且距上次检查超过一天时才联网。网络错误静默忽略。 */
    suspend fun checkIfDue() {
        // 上次更新留下的安装包（已经装好了，或者没装）启动时清掉
        withContext(Dispatchers.IO) { updatesDir().deleteRecursively() }
        val current = settings.current()
        if (!current.autoUpdateCheck) return
        if (System.currentTimeMillis() - current.lastUpdateCheck < CHECK_INTERVAL_MS) return
        val info = runCatching { fetchLatest() }.onFailure { Log.i(TAG, "update check failed", it) }.getOrNull() ?: return
        settings.setLastUpdateCheck(System.currentTimeMillis())
        if (info.versionCode > BuildConfig.VERSION_CODE && info.versionCode != current.skippedUpdateVersion) {
            _state.value = UpdateState.Available(info)
        }
    }

    /** 设置里的"检查更新"：忽略检查间隔和"跳过此版本"。 */
    suspend fun checkNow(): CheckResult {
        val info = runCatching { fetchLatest() }.onFailure { Log.i(TAG, "update check failed", it) }.getOrNull()
            ?: return CheckResult.FAILED
        settings.setLastUpdateCheck(System.currentTimeMillis())
        if (info.versionCode <= BuildConfig.VERSION_CODE) return CheckResult.UP_TO_DATE
        _state.value = UpdateState.Available(info)
        return CheckResult.UPDATE_AVAILABLE
    }

    /** 关闭更新提示；[skip] 为 true 时以后不再自动提示这个版本。 */
    fun dismiss(skip: Boolean) {
        val info = (state.value as? UpdateState.Available)?.info ?: (state.value as? UpdateState.Failed)?.info
        _state.value = UpdateState.Idle
        if (skip && info != null) scope.launch { settings.setSkippedUpdateVersion(info.versionCode) }
    }

    fun downloadAndInstall(info: UpdateInfo) {
        if (state.value is UpdateState.Downloading || state.value is UpdateState.Installing) return
        _state.value = UpdateState.Downloading(info, 0f)
        scope.launch {
            try {
                val apk = download(info)
                _state.value = UpdateState.Installing(info)
                install(apk)
            } catch (e: Exception) {
                Log.w(TAG, "update failed", e)
                _state.value = UpdateState.Failed(info, e.message)
            }
        }
    }

    /** 安装结果（[InstallResultReceiver]）：成功时 App 会被系统替换并重启，不会走到这里。 */
    internal fun onInstallFailed(message: String?) {
        val info = when (val s = state.value) {
            is UpdateState.Installing -> s.info
            is UpdateState.Downloading -> s.info
            else -> return
        }
        _state.value = UpdateState.Failed(info, message)
    }

    /** 用户在系统的安装确认界面点了取消。 */
    internal fun onInstallCancelled() {
        val info = (state.value as? UpdateState.Installing)?.info ?: return
        _state.value = UpdateState.Available(info)
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
        val file = File(dir, "JeekMusic-${info.versionName}.apk")
        dir.listFiles()?.filter { it != file }?.forEach { it.delete() }
        // 已经下载过（比如第一次安装时去设置里允许了"安装未知应用"，回来再点更新），校验通过就直接用
        if (file.length() == info.sizeBytes && info.sha256 != null && sha256(file).equals(info.sha256, ignoreCase = true)) return file
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
            error("SHA-256 mismatch")
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

    private fun install(apk: File) {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            // 本 App 是上次的安装来源时可以免确认；否则系统仍会弹出确认界面
            setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        }
        val sessionId = installer.createSession(params)
        installer.openSession(sessionId).use { session ->
            session.openWrite("base.apk", 0, apk.length()).use { out ->
                apk.inputStream().use { it.copyTo(out) }
                session.fsync(out)
            }
            val intent = Intent(context, InstallResultReceiver::class.java)
            // 系统要往 Intent 里填安装结果，所以必须是 MUTABLE
            val pending = PendingIntent.getBroadcast(context, sessionId, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
            session.commit(pending.intentSender)
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
        const val TIMEOUT_MS = 15_000
        val VERSION_CODE = Regex("""\d+$""")
    }
}
