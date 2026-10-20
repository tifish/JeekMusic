package com.tinyfish.jeekmusic.update

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.util.Log
import androidx.core.content.IntentCompat
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject

/** 接收 PackageInstaller 会话的结果（docs/design.md §13.2）。 */
@AndroidEntryPoint
class InstallResultReceiver : BroadcastReceiver() {

    @Inject lateinit var updater: AppUpdater

    override fun onReceive(context: Context, intent: Intent) {
        when (val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            // 需要用户在系统界面确认（首次还会要求允许"安装未知应用"）
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                val confirm = IntentCompat.getParcelableExtra(intent, Intent.EXTRA_INTENT, Intent::class.java) ?: return
                context.startActivity(confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
            PackageInstaller.STATUS_SUCCESS -> Unit
            PackageInstaller.STATUS_FAILURE_ABORTED -> updater.onInstallCancelled()
            else -> {
                val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
                Log.w("InstallResultReceiver", "install failed: $status $message")
                updater.onInstallFailed(message)
            }
        }
    }
}
