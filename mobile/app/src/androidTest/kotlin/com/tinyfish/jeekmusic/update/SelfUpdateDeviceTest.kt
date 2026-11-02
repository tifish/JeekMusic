package com.tinyfish.jeekmusic.update

import android.content.Context
import android.content.ContextWrapper
import android.content.Intent
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.os.Bundle
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import dagger.hilt.android.EntryPointAccessors
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import org.json.JSONObject

/** Host-driven bridge, installed only as a signed instrumentation APK. All installs invoke the
 * production singleton inside the target package UID. It does not grant shell identity and never
 * clicks a system install button. See docs/android-update.md for the host protocol/evidence rules.
 */
@RunWith(AndroidJUnit4::class)
class SelfUpdateDeviceTest {
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private val context get() = instrumentation.targetContext
    private val arguments get() = InstrumentationRegistry.getArguments()
    private val installer get() = EntryPointAccessors.fromApplication(context, UpdateInstallerAccess::class.java).updateInstaller()

    @Test
    fun bridge() {
        when (arguments.getString("updateAction", "diagnostics")) {
            "install" -> {
                // Host supplies a fixture APK readable by this app, never a production URL/token.
                val source = File(requireNotNull(arguments.getString("updateApk")))
                val info = info(source)
                installer.apkFile.parentFile!!.mkdirs()
                source.copyTo(installer.apkFile, overwrite = true)
                File(context.noBackupFilesDir, "update-regression-marker.txt").writeText("preserve-self-update-data-v1")
                installer.install(info)
            }
            "cancel" -> installer.cancel()
            "recover" -> installer.reconcile()
            "diagnostics" -> Unit
            else -> error("Unsupported updateAction")
        }
        instrumentation.sendStatus(0, Bundle().apply {
            putString("updateDiagnostics", installer.diagnostics().toString())
            putBoolean("updateRegressionDataPreserved", File(context.noBackupFilesDir, "update-regression-marker.txt")
                .takeIf { it.isFile }?.readText() == "preserve-self-update-data-v1")
        })
    }

    @Test
    fun validatesArchiveAndRejectsForgedResults() {
        val source = File(requireNotNull(arguments.getString("updateApk")))
        val info = info(source)
        val isolated = UpdateInstaller(object : ContextWrapper(context) {
            override fun getNoBackupFilesDir() = File(context.cacheDir, "update-test-journal").apply { mkdirs() }
        })
        isolated.validate(info, source)
        for ((invalid, expected) in listOf(
            info.copy(sizeBytes = info.sizeBytes + 1) to "size_mismatch",
            info.copy(sha256 = "0".repeat(64)) to "digest_mismatch",
            info.copy(versionCode = info.versionCode + 1) to "version_mismatch",
        )) {
            try { isolated.validate(invalid, source); fail("Expected $expected") }
            catch (failure: UpdateFailure) { assertEquals(expected, failure.code) }
        }
        arguments.getString("updateWrongSignerApk")?.let { path ->
            val wrong = File(path)
            try { isolated.validate(info(wrong), wrong); fail("Expected signature mismatch") }
            catch (failure: UpdateFailure) { assertEquals("signature_mismatch", failure.code) }
        }
        arguments.getString("updateWrongPackageApk")?.let { path ->
            val wrong = File(path)
            try { isolated.validate(info(wrong), wrong); fail("Expected package mismatch") }
            catch (failure: UpdateFailure) { assertEquals("package_mismatch", failure.code) }
        }
        val before = isolated.diagnostics().toString()
        isolated.result(Intent("com.tinyfish.jeekmusic.UPDATE_STATUS")
            .putExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_SUCCESS)
            .putExtra(PackageInstaller.EXTRA_SESSION_ID, 42))
        assertEquals(before, isolated.diagnostics().toString())
        @Suppress("DEPRECATION")
        val receiver = context.packageManager.getReceiverInfo(
            android.content.ComponentName(context, InstallResultReceiver::class.java), 0)
        assertFalse(receiver.exported)
        assertFalse(before.contains("apkUrl"))
        assertFalse(before.contains("capability"))
        assertFalse(before.contains(source.absolutePath))
    }

    @Test
    fun coldTerminalCallbackRetainsCancellationAndMissingSessionIsNotSuccess() {
        val source = File(requireNotNull(arguments.getString("updateApk")))
        @Suppress("DEPRECATION")
        val nextVersion = context.packageManager.getPackageInfo(context.packageName, 0).longVersionCode.toInt() + 1
        val info = info(source).copy(versionCode = nextVersion)
        val directory = File(context.cacheDir, "update-cold-result-test").apply { mkdirs() }
        val isolatedContext = object : ContextWrapper(context) {
            override fun getNoBackupFilesDir() = directory
        }
        val id = Int.MAX_VALUE
        File(directory, "update-install.json").writeText(JSONObject()
            .put("info", JSONObject().put("versionCode", info.versionCode).put("versionName", info.versionName)
                .put("notes", "").put("apkUrl", info.apkUrl).put("sizeBytes", info.sizeBytes).put("sha256", info.sha256))
            .put("sessionId", id).put("capability", "fixture-capability").put("stage", "submitted").toString())
        val cold = UpdateInstaller(isolatedContext)
        assertEquals("session_lost", cold.diagnostics().getString("error"))
        assertEquals("failed", cold.diagnostics().getString("stage"))
        cold.result(Intent("com.tinyfish.jeekmusic.UPDATE_STATUS")
            .setData(android.net.Uri.parse("jeekmusic-update://result/$id/fixture-capability"))
            .putExtra(PackageInstaller.EXTRA_SESSION_ID, id)
            .putExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE_ABORTED))
        assertEquals("cancelled", cold.diagnostics().getString("error"))
        assertEquals(PackageInstaller.STATUS_FAILURE_ABORTED, cold.diagnostics().getInt("status"))
        assertEquals("cancelled", UpdateInstaller(isolatedContext).diagnostics().getString("error"))
    }

    private fun info(source: File): UpdateInfo {
        @Suppress("DEPRECATION")
        val archive = requireNotNull(context.packageManager.getPackageArchiveInfo(source.absolutePath,
            PackageManager.GET_SIGNING_CERTIFICATES))
        return UpdateInfo(archive.longVersionCode.toInt(), archive.versionName.orEmpty(), "",
            "https://example.invalid/private-fixture?token=never-in-diagnostics", source.length(), UpdateInstaller.sha256(source))
    }
}
