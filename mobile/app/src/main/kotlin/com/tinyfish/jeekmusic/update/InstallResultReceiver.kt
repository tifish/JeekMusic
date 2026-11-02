package com.tinyfish.jeekmusic.update

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject

/** Non-exported status capability receiver. Never starts an activity from the background. */
@AndroidEntryPoint
class InstallResultReceiver : BroadcastReceiver() {
    @Inject lateinit var installer: UpdateInstaller

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == Intent.ACTION_MY_PACKAGE_REPLACED) installer.reconcile()
        else installer.result(intent)
    }
}
