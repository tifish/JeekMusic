package com.tinyfish.jeekmusic.update

import android.content.ContextWrapper
import android.graphics.Bitmap
import android.os.Build
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import com.tinyfish.jeekmusic.MainActivity
import com.tinyfish.jeekmusic.R
import dagger.hilt.android.EntryPointAccessors
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.UUID

/** Reproduces stale errors after Android has already replaced the application. No installs,
 * network or shell permissions: PackageManager supplies the real current version.
 */
@RunWith(AndroidJUnit4::class)
class InstalledUpdateDeviceTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun isolated() = object : ContextWrapper(context) {
        private val root = File(context.cacheDir, "installed-update-${UUID.randomUUID()}").apply { mkdirs() }
        override fun getNoBackupFilesDir() = root
        override fun getCacheDir() = File(root, "cache").apply { mkdirs() }
    }
    private fun currentInfo() = context.packageManager.getPackageInfo(context.packageName, 0).let {
        UpdateInfo(it.longVersionCode.toInt(), it.versionName.orEmpty(), "", "https://example.invalid/update.apk",
            File(context.packageCodePath).length(), null)
    }
    private fun json(info: UpdateInfo) = JSONObject().put("versionCode", info.versionCode).put("versionName", info.versionName)
        .put("notes", "").put("apkUrl", info.apkUrl).put("sizeBytes", info.sizeBytes)

    @Test
    fun coldAlreadyInstalledTargetClearsEveryInterruptedAndFailedStage() {
        val info = currentInfo()
        for (stage in listOf("failed", "permission_required", "downloading", "writing", "submitted", "confirmation_required", "available", "dismissed")) {
            val ctx = isolated()
            File(ctx.noBackupFilesDir, "update-install.json").writeText(JSONObject().put("info", json(info))
                .put("stage", stage).put("error", "version_mismatch").put("sessionId", -1).toString())
            val installer = UpdateInstaller(ctx)
            assertEquals(stage, UpdateState.Idle, installer.state.value)
            assertEquals(stage, "succeeded", installer.diagnostics().getString("stage"))
            assertTrue(installer.diagnostics().isNull("error"))
        }
    }

    @Test
    fun alreadyInstalledRequestNeedsNoArchiveAndCreatesNoSession() {
        val ctx = isolated(); val installer = UpdateInstaller(ctx); val info = currentInfo()
        installer.install(info, File(ctx.cacheDir, "missing.apk"))
        assertEquals(UpdateState.Idle, installer.state.value)
        assertEquals("succeeded", installer.diagnostics().getString("stage"))
        assertEquals(-1, installer.diagnostics().getInt("sessionId"))
        assertFalse(installer.downloading(info))
        assertFalse(installer.offer(info))
        installer.install(info.copy(versionCode = info.versionCode - 1), File(ctx.cacheDir, "missing.apk"))
        assertEquals(UpdateState.Idle, installer.state.value) // Never attempt a downgrade either.
    }

    @Test
    fun lateFailureCannotReplaceAnAlreadyInstalledVersionWithAnError() {
        val installer = UpdateInstaller(isolated()); val info = currentInfo()
        installer.fail(info, "version_mismatch")
        assertEquals(UpdateState.Idle, installer.state.value)
        installer.fail(info, "session_lost")
        assertEquals(UpdateState.Idle, installer.state.value)
        assertTrue(installer.diagnostics().isNull("error"))
    }

    @Test
    fun realClientColdStartAndRepeatedUpdateRequestDoNotShowTheStaleError() = runBlocking {
        check(Build.HARDWARE in setOf("ranchu", "goldfish") && context.packageName.endsWith(".debug"))
        val info = currentInfo()
        // This is the old production journal shape, including its missing per-event error.
        File(context.noBackupFilesDir, "update-install.json").writeText(JSONObject().put("info", json(info))
            .put("stage", "failed").put("error", "version_mismatch").put("sessionId", -1).toString())
        ActivityScenario.launch(MainActivity::class.java).use {
            val updater = EntryPointAccessors.fromApplication(context, UpdateInstallerAccess::class.java).appUpdater()
            assertEquals(UpdateState.Idle, updater.state.value)
            updater.downloadAndInstall(info)
            delay(300)
            assertEquals(UpdateState.Idle, updater.state.value)
            assertEquals("succeeded", updater.diagnostics().getString("stage"))
            assertEquals(-1, updater.diagnostics().getInt("sessionId"))
            val ui = InstrumentationRegistry.getInstrumentation().uiAutomation
            fun find(node: AccessibilityNodeInfo?, value: String): Boolean {
                if (node == null) return false
                if (node.viewIdResourceName == value || node.text?.toString() == value) return true
                return (0 until node.childCount).any { find(node.getChild(it), value) }
            }
            withTimeout(15_000) { while (!find(ui.rootInActiveWindow, "open_settings")) delay(50) }
            assertFalse(find(ui.rootInActiveWindow, context.getString(R.string.update_failed)))
            val bitmap = requireNotNull(ui.takeScreenshot())
            File(context.cacheDir, "already-installed-cold-start.png").outputStream().use { output ->
                bitmap.compress(Bitmap.CompressFormat.PNG, 100, output)
            }
            bitmap.recycle()
        }
    }

    @Test
    fun futureTargetStillFailsAndRetainsEachFailureReasonAndObservedVersion() {
        val ctx = isolated(); val installer = UpdateInstaller(ctx)
        val current = currentInfo(); val future = current.copy(versionCode = current.versionCode + 1)
        installer.fail(future, "session_lost"); installer.fail(future, "version_mismatch")
        assertTrue(installer.state.value is UpdateState.Failed)
        val history = installer.diagnostics().getJSONArray("events")
        val failures = (0 until history.length()).map { history.getJSONObject(it) }.filter { it.getString("stage") == "failed" }
        assertEquals(listOf("session_lost", "version_mismatch"), failures.map { it.getString("error") })
        assertTrue(failures.all { it.getInt("installedVersion") == current.versionCode && it.getInt("targetVersion") == future.versionCode })
        installer.fail(current, "session_lost") // A stale job cannot hide a newer real failure.
        assertEquals(future.versionCode, installer.diagnostics().getInt("targetVersion"))
        assertEquals("version_mismatch", installer.diagnostics().getString("error"))
        try {
            installer.install(future, File(context.packageCodePath))
            fail("An archive that does not match a still-needed target must remain rejected")
        } catch (failure: UpdateFailure) { assertEquals("version_mismatch", failure.code) }
        assertTrue(UpdateInstaller(ctx).state.value is UpdateState.Failed)
    }
}
