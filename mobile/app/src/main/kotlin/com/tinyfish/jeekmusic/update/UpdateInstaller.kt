package com.tinyfish.jeekmusic.update

import android.Manifest
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.util.AtomicFile
import android.util.Log
import androidx.core.content.IntentCompat
import com.tinyfish.jeekmusic.R
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.flow.MutableStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

internal class UpdateFailure(val code: String) : Exception(code)

/** In-process entry point for instrumentation; no Android component or external permission. */
@EntryPoint
@InstallIn(SingletonComponent::class)
interface UpdateInstallerAccess {
    fun updateInstaller(): UpdateInstaller
}

/** The same install path is used by downloads, permission recovery and device instrumentation.
 * The private journal survives process replacement; diagnostics deliberately exclude the release
 * URL, callback capability, confirmation intent, paths and arbitrary platform error messages.
 */
@Singleton
class UpdateInstaller @Inject constructor(@param:ApplicationContext private val context: Context) {
    private val pm get() = context.packageManager
    private val installer get() = pm.packageInstaller
    private val journal = AtomicFile(File(context.noBackupFilesDir, "update-install.json"))
    private var record = runCatching { JSONObject(journal.openRead().bufferedReader().use { it.readText() }) }
        .getOrElse { JSONObject() }
    internal val state = MutableStateFlow<UpdateState>(UpdateState.Idle)
    private var confirmation: Intent? = null
    val apkFile get() = File(context.cacheDir, "updates/update.apk")

    init { reconcile(recoverInterrupted = true) }

    fun canInstall(): Boolean = pm.canRequestPackageInstalls()

    fun permissionIntent() = Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
        Uri.parse("package:${context.packageName}"))

    @Synchronized
    internal fun downloading(info: UpdateInfo) {
        saveInfo(info)
        record.remove("error")
        record.remove("reason")
        record.remove("status")
        record.put("confirmationSeen", false)
        record.put("sessionId", -1)
        record.remove("capability")
        record.remove("requireUserAction")
        event("downloading")
        state.value = UpdateState.Downloading(info, 0f)
    }

    /** Validate the actual archive, not the URL/tag or setAppPackageName hint. Android's package
     * manager still performs the final signature/APK verification at commit. A signing-key change
     * is intentionally rejected here; a planned key rotation needs its own verified migration.
     */
    internal fun validate(info: UpdateInfo, apk: File) {
        if (info.sizeBytes <= 0 || apk.length() != info.sizeBytes) throw UpdateFailure("size_mismatch")
        if (info.sha256 != null && !sha256(apk).equals(info.sha256, true)) throw UpdateFailure("digest_mismatch")
        @Suppress("DEPRECATION")
        val archive = pm.getPackageArchiveInfo(apk.absolutePath, PackageManager.GET_SIGNING_CERTIFICATES)
            ?: throw UpdateFailure("invalid_apk")
        @Suppress("DEPRECATION")
        val installed = pm.getPackageInfo(context.packageName, PackageManager.GET_SIGNING_CERTIFICATES)
        if (archive.packageName != context.packageName) throw UpdateFailure("package_mismatch")
        if (archive.longVersionCode != info.versionCode.toLong() || archive.longVersionCode <= installed.longVersionCode) {
            throw UpdateFailure("version_mismatch")
        }
        val incoming = archive.signingInfo?.apkContentsSigners?.map { it.toCharsString() }?.toSet()
        val current = installed.signingInfo?.apkContentsSigners?.map { it.toCharsString() }?.toSet()
        if (incoming.isNullOrEmpty() || incoming != current) throw UpdateFailure("signature_mismatch")
    }

    @Synchronized
    internal fun install(info: UpdateInfo, apk: File = apkFile) {
        validate(info, apk)
        saveInfo(info)
        record.remove("error")
        if (!canInstall()) {
            event("permission_required")
            state.value = UpdateState.PermissionRequired(info)
            return
        }
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            setSize(apk.length())
            // Self-update is an independent qualification; a previous self-install is not required.
            setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
            if (Build.VERSION.SDK_INT >= 33) setPackageSource(PackageInstaller.PACKAGE_SOURCE_DOWNLOADED_FILE)
        }
        val id = installer.createSession(params)
        val capability = UUID.randomUUID().toString()
        record.put("sessionId", id).put("capability", capability)
            .put("requireUserAction", PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
            .put("confirmationSeen", false).remove("status")
        record.remove("reason")
        event("writing") // Must reach disk before commit, including when this process is killed.
        try {
            installer.openSession(id).use { session ->
                session.openWrite("base.apk", 0, apk.length()).use { output ->
                    apk.inputStream().use { it.copyTo(output) }
                    session.fsync(output)
                }
                // Explicit, non-exported receiver + unguessable identity. Mutable is necessary for
                // PackageInstaller's result extras; do not expose an exported activity callback.
                val intent = Intent(context, InstallResultReceiver::class.java)
                    .setAction(ACTION_STATUS).setData(callbackUri(id, capability))
                val pending = PendingIntent.getBroadcast(context, id, intent,
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
                event("submitted")
                state.value = UpdateState.Installing(info)
                session.commit(pending.intentSender)
            }
        } catch (error: Exception) {
            runCatching { installer.abandonSession(id) }
            fail(info, "session_error")
            throw UpdateFailure("session_error")
        }
    }

    @Synchronized
    internal fun result(intent: Intent) {
        val id = record.optInt("sessionId", -1)
        val capability = record.optString("capability")
        val stage = record.optString("stage")
        // A terminal callback can start a cold process after PackageInstaller removed the session.
        // Reconciliation may already have observed that absence; still accept this authenticated
        // callback so that the actual failure/cancellation status is not replaced by session_lost.
        val expectsResult = stage in setOf("submitted", "confirmation_required") ||
            (stage == "failed" && record.optString("error") == "session_lost")
        if (id < 0 || capability.isEmpty() || intent.action != ACTION_STATUS ||
            intent.data != callbackUri(id, capability) ||
            intent.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1) != id ||
            !expectsResult) return
        val info = readInfo() ?: return
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        record.put("status", status)
        // Only a stable platform error identifier is retained. Messages can include private paths.
        val reason = Regex("\\bINSTALL_(?:FAILED|PARSE_FAILED)_[A-Z0-9_]+\\b")
            .find(intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE).orEmpty())?.value
        if (reason != null) record.put("reason", reason)
        when (status) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                record.put("confirmationSeen", true)
                confirmation = IntentCompat.getParcelableExtra(intent, Intent.EXTRA_INTENT, Intent::class.java)
                if (confirmation == null) { fail(info, "missing_confirmation"); return }
                event("confirmation_required")
                state.value = UpdateState.ConfirmationRequired(info)
            }
            PackageInstaller.STATUS_SUCCESS -> {
                if (installedVersion() >= info.versionCode) complete() else fail(info, "success_unverified")
            }
            PackageInstaller.STATUS_FAILURE_ABORTED -> fail(info, "cancelled")
            else -> fail(info, "install_status_$status")
        }
    }

    /** Runs at launch/resume and after MY_PACKAGE_REPLACED. Never infer success from a missing
     * session. Never delete a downloaded APK while permission/confirmation recovery may need it.
     */
    @Synchronized
    fun reconcile(recoverInterrupted: Boolean = false) {
        val info = readInfo() ?: return
        val stage = record.optString("stage")
        if (stage == "succeeded" || stage == "dismissed") return
        if (stage in setOf("submitted", "confirmation_required") && installedVersion() >= info.versionCode) {
            complete()
            return
        }
        when (stage) {
            "permission_required" -> state.value = UpdateState.PermissionRequired(info)
            "failed" -> state.value = UpdateState.Failed(info, failureMessage(record.optString("error")))
            "submitted", "confirmation_required" -> {
                val session = installer.getSessionInfo(record.optInt("sessionId", -1))
                if (session == null) fail(info, "session_lost")
                else if (stage == "confirmation_required") state.value = UpdateState.ConfirmationRequired(info)
                else state.value = UpdateState.Installing(info)
            }
            "downloading", "writing" -> {
                if (recoverInterrupted) {
                    abandon()
                    fail(info, "interrupted")
                }
            }
        }
    }

    @Synchronized
    fun takeConfirmation(): Intent? = confirmation.also { confirmation = null }

    @Synchronized
    fun cancel() {
        val info = readInfo() ?: return
        abandon()
        fail(info, "cancelled")
    }

    @Synchronized
    internal fun dismiss() {
        if (state.value is UpdateState.PermissionRequired) event("dismissed")
        if (state.value is UpdateState.Failed) event("dismissed")
        state.value = UpdateState.Idle
    }

    @Synchronized
    internal fun fail(info: UpdateInfo, code: String) {
        abandon()
        saveInfo(info)
        record.put("error", code)
        event("failed")
        state.value = UpdateState.Failed(info, failureMessage(code))
    }

    private fun failureMessage(code: String): String = context.getString(when (code) {
        "cancelled" -> R.string.update_cancelled
        "signature_mismatch" -> R.string.update_signature_mismatch
        "package_mismatch", "version_mismatch", "invalid_apk", "size_mismatch", "digest_mismatch" -> R.string.update_invalid_package
        "interrupted", "session_lost" -> R.string.update_interrupted
        else -> R.string.update_install_error
    }) + " ($code)"

    @Synchronized
    fun diagnostics(): JSONObject {
        val source = runCatching { pm.getInstallSourceInfo(context.packageName) }.getOrNull()
        return JSONObject().put("schema", 1).put("android", Build.VERSION.RELEASE)
            .put("api", Build.VERSION.SDK_INT).put("targetSdk", context.applicationInfo.targetSdkVersion)
            .put("package", context.packageName).put("installedVersion", installedVersion())
            .put("canRequestPackageInstalls", canInstall())
            .put("withoutUserActionPermission", context.checkSelfPermission(Manifest.permission.UPDATE_PACKAGES_WITHOUT_USER_ACTION) == PackageManager.PERMISSION_GRANTED)
            .put("installer", source?.installingPackageName ?: JSONObject.NULL)
            .put("initiatingPackage", source?.initiatingPackageName ?: JSONObject.NULL)
            .put("originatingPackage", source?.originatingPackageName ?: JSONObject.NULL)
            .put("updateOwner", if (Build.VERSION.SDK_INT >= 34) source?.updateOwnerPackageName ?: JSONObject.NULL else JSONObject.NULL)
            .put("packageSource", if (Build.VERSION.SDK_INT >= 33) source?.packageSource ?: JSONObject.NULL else JSONObject.NULL)
            .put("selfUpdate", true)
            .put("stage", record.optString("stage", "idle"))
            .put("sessionId", record.optInt("sessionId", -1))
            .put("requestedRequireUserAction", record.opt("requireUserAction") ?: JSONObject.NULL)
            .put("targetVersion", readInfo()?.versionCode ?: JSONObject.NULL)
            .put("confirmationSeen", record.optBoolean("confirmationSeen"))
            .put("status", record.opt("status") ?: JSONObject.NULL)
            .put("error", record.opt("error") ?: JSONObject.NULL)
            .put("platformReason", record.opt("reason") ?: JSONObject.NULL)
            .put("events", record.optJSONArray("events") ?: JSONArray())
    }

    private fun saveInfo(info: UpdateInfo) {
        record.put("info", JSONObject().put("versionCode", info.versionCode).put("versionName", info.versionName)
            .put("notes", info.notes).put("apkUrl", info.apkUrl).put("sizeBytes", info.sizeBytes).put("sha256", info.sha256))
    }

    private fun readInfo(): UpdateInfo? = runCatching { record.optJSONObject("info")?.let {
        UpdateInfo(it.getInt("versionCode"), it.getString("versionName"), it.getString("notes"),
            it.getString("apkUrl"), it.getLong("sizeBytes"), it.optString("sha256").takeIf(String::isNotBlank))
    } }.getOrNull()

    private fun installedVersion(): Long {
        @Suppress("DEPRECATION")
        return pm.getPackageInfo(context.packageName, 0).longVersionCode
    }

    private fun complete() {
        record.remove("error")
        event("succeeded")
        record.remove("capability")
        persist()
        state.value = UpdateState.Idle
        apkFile.delete()
    }

    private fun abandon() {
        record.optInt("sessionId", -1).takeIf { it >= 0 }?.let { runCatching { installer.abandonSession(it) } }
        confirmation = null
    }

    private fun event(stage: String) {
        record.put("stage", stage)
        val prior = record.optJSONArray("events") ?: JSONArray()
        val events = JSONArray()
        for (i in maxOf(0, prior.length() - 23) until prior.length()) events.put(prior.get(i))
        events.put(JSONObject().put("stage", stage).put("at", System.currentTimeMillis())
            .put("sessionId", record.optInt("sessionId", -1)).put("status", record.opt("status") ?: JSONObject.NULL)
            .put("canRequestPackageInstalls", canInstall()))
        record.put("events", events)
        persist()
        Log.i("JeekUpdate", "stage=$stage session=${record.optInt("sessionId", -1)} status=${record.opt("status")}")
    }

    private fun persist() {
        val output = journal.startWrite()
        try { output.write(record.toString().toByteArray(Charsets.UTF_8)); journal.finishWrite(output) }
        catch (error: Exception) { journal.failWrite(output); throw error }
    }

    private fun callbackUri(id: Int, capability: String) = Uri.parse("jeekmusic-update://result/$id/$capability")

    companion object {
        private const val ACTION_STATUS = "com.tinyfish.jeekmusic.UPDATE_STATUS"
        internal fun sha256(file: File): String {
            val digest = MessageDigest.getInstance("SHA-256")
            file.inputStream().use { input ->
                val buffer = ByteArray(64 * 1024)
                while (true) { val n = input.read(buffer); if (n < 0) break; digest.update(buffer, 0, n) }
            }
            return digest.digest().joinToString("") { "%02x".format(it) }
        }
    }
}
