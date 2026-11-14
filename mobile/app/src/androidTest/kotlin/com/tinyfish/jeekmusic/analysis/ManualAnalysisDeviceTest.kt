package com.tinyfish.jeekmusic.analysis

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.work.*
import com.tinyfish.jeekmusic.data.settings.*
import kotlinx.coroutines.guava.await
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.TimeUnit

@RunWith(AndroidJUnit4::class)
class ManualAnalysisDeviceTest {
    @Test fun scansDoNotEnqueueAutomaticWorkOrCancelAnExplicitRun() = runBlocking {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val settings=SettingsRepository(context); val original=settings.current().analysisMode
        val manager=WorkManager.getInstance(context)
        // This acceptance is run on the explicitly selected fixture emulator in Manual mode.
        assertTrue(manager.getWorkInfosForUniqueWork(AnalysisScheduler.WORK_NAME).await().none { !it.state.isFinished })
        val scheduler=AnalysisScheduler(context,settings)
        try {
            settings.setAnalysisMode(AnalysisMode.MANUAL)
            scheduler.schedule()
            assertTrue(manager.getWorkInfosForUniqueWork(AnalysisScheduler.WORK_NAME).await().none { !it.state.isFinished })
            // A day-delayed predecessor keeps the real explicit continuation from ever executing.
            val predecessor=OneTimeWorkRequestBuilder<AnalysisWorker>().setInitialDelay(1,TimeUnit.DAYS).build()
            manager.enqueueUniqueWork(AnalysisScheduler.WORK_NAME,ExistingWorkPolicy.REPLACE,predecessor).result.await()
            scheduler.schedule()
            assertEquals(WorkInfo.State.ENQUEUED,manager.getWorkInfoById(predecessor.id).await()!!.state)
            scheduler.continueLater(true)
            assertEquals(2,manager.getWorkInfosForUniqueWork(AnalysisScheduler.WORK_NAME).await().count { !it.state.isFinished })
            scheduler.modeChanged()
            assertTrue(manager.getWorkInfosForUniqueWork(AnalysisScheduler.WORK_NAME).await().all { it.state.isFinished })
        } finally {
            manager.cancelUniqueWork(AnalysisScheduler.WORK_NAME).result.await()
            settings.setAnalysisMode(original)
        }
    }
}
