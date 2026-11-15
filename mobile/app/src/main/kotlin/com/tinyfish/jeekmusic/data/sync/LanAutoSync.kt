package com.tinyfish.jeekmusic.data.sync

import android.content.Context
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.ProcessLifecycleOwner
import androidx.work.*
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import org.json.JSONObject
import java.util.concurrent.TimeUnit

data class LanSchedule(val trigger: String="startup",val dirty: Boolean=false,val running: Boolean=false,val paused: Boolean=false,val cancelled: Boolean=false,val foreground: Boolean=false,val backoffUntil: Long=0,val lastSuccess: Long=0,val nextReconcile: Long=0,val state: String="idle",val watchDirectories: Int=0,val watchFailed: Boolean=false,val lastMetadataObservations: Long=0,val lastHashBytes: Long=0,val lastTransferBytes: Long=0,val wifiOnly: Boolean=true,val waitingForWifi: Boolean=false,val lastPreparationMs: Long=0,val lastFirstTransferMs: Long=-1,val lastProtocolRequests: Long=0,val lastManifestCacheHit: Boolean=false,val lastMetadataRows: Long=0,val lastIncremental: Boolean=false,val lastAudioReusedBytes: Long=0,val lastAudioReusedFiles: Int=0,val lastLocalIndexHits: Int=0,val lastLocalIndexMisses: Int=0,val lastLocalIndexRefreshedFiles: Int=0,val lastLibraryMetadataReusedFiles: Int=0) {
    fun json()=JSONObject().put("trigger",trigger).put("dirty",dirty).put("running",running).put("paused",paused).put("cancelled",cancelled).put("foreground",foreground).put("backoffUntil",backoffUntil).put("lastSuccess",lastSuccess).put("nextReconcile",nextReconcile).put("state",state).put("watchDirectories",watchDirectories).put("watchFailed",watchFailed).put("lastMetadataObservations",lastMetadataObservations).put("lastHashBytes",lastHashBytes).put("lastTransferBytes",lastTransferBytes).put("wifiOnly",wifiOnly).put("waitingForWifi",waitingForWifi).put("lastPreparationMs",lastPreparationMs).put("lastFirstTransferMs",lastFirstTransferMs).put("lastProtocolRequests",lastProtocolRequests).put("lastManifestCacheHit",lastManifestCacheHit).put("lastMetadataRows",lastMetadataRows).put("lastIncremental",lastIncremental).put("lastAudioReusedBytes",lastAudioReusedBytes).put("lastAudioReusedFiles",lastAudioReusedFiles).put("lastLocalIndexHits",lastLocalIndexHits).put("lastLocalIndexMisses",lastLocalIndexMisses).put("lastLocalIndexRefreshedFiles",lastLocalIndexRefreshedFiles).put("lastLibraryMetadataReusedFiles",lastLibraryMetadataReusedFiles)
}

/** Events are hints only. All writes/deletes go through the same serialized metadata reconciler. */
internal class LanAutoSync(private val context: Context,private val scope: CoroutineScope,private val service: LanSyncService) {
    private val lock=Any()
    private val _state=MutableStateFlow(LanSchedule(wifiOnly=service.networkPolicy.state.value.wifiOnly,waitingForWifi=service.networkPolicy.state.value.waitingForWifi))
    val state=_state.asStateFlow()
    @Volatile private var foreground=false
    private var initialized=false
    private var timer: Job?=null
    private var loop: Job?=null
    private var conflictLoop: Job?=null
    private var reportJob: Job?=null
    private var reportPending=false
    private var firstHint=0L
    private var failures=0
    private var workerOwned=false
    private fun eligible()=service.store.read().has("connection") && service.store.mappings().any { LanMappingTargets.isCurrent(it) && it.optBoolean("enabled",true) }
    private fun held()=service.store.read().optBoolean("autoCancelled")
    fun initialize() {
        synchronized(lock) { if(initialized)return;initialized=true }
        scope.launch(Dispatchers.Main) {
            ProcessLifecycleOwner.get().lifecycle.addObserver(object: DefaultLifecycleObserver {
                override fun onStart(owner: LifecycleOwner) { enterForeground(true) }
                override fun onStop(owner: LifecycleOwner) { enterForeground(false) }
            })
        }
        scope.launch(Dispatchers.IO) {
            service.networkPolicy.state.collect { network ->
                synchronized(lock) {
                    _state.value=_state.value.copy(wifiOnly=network.wifiOnly,waitingForWifi=network.waitingForWifi)
                    if(network.waitingForWifi) {
                        timer?.cancel();firstHint=0
                        _state.value=_state.value.copy(state="waiting_for_wifi")
                    } else { failures=0;_state.value=_state.value.copy(backoffUntil=0) }
                }
                if(!network.waitingForWifi && foreground)hint("network_available")
            }
        }
        scope.launch(Dispatchers.IO) { service.localFileIndex.changed.collect { hint("local_edit") } }
        configure()
    }
    fun configure() {
        scope.launch(Dispatchers.IO) {
            val enabled=eligible()
            service.localFileIndex.retainMappings(service.store.mappings().map { it.getString("key") }.toSet())
            // LAN-only Wi-Fi need not have validated internet. The production socket policy
            // checks the actual transport before any DNS/TCP; CONNECTED would block such LANs.
            if(enabled)WorkManager.getInstance(context).enqueueUniquePeriodicWork(WORK,ExistingPeriodicWorkPolicy.UPDATE,
                PeriodicWorkRequestBuilder<LanSyncWorker>(15,TimeUnit.MINUTES).setBackoffCriteria(BackoffPolicy.EXPONENTIAL,15,TimeUnit.MINUTES).build())
            else WorkManager.getInstance(context).cancelUniqueWork(WORK)
            synchronized(lock) { _state.value=_state.value.copy(paused=!enabled,cancelled=held()) }
            if(enabled)hint("mapping_or_startup")
            report()
        }
    }
    private fun enterForeground(value: Boolean) {
        foreground=value;synchronized(lock) { _state.value=_state.value.copy(foreground=value) }
        loop?.cancel()
        conflictLoop?.cancel()
        if(!value) {
            synchronized(lock) { timer?.cancel();firstHint=0 }
            val needsHandoff=synchronized(lock) { if(_state.value.running)!workerOwned else _state.value.dirty }
            val userRequested=synchronized(lock) { _state.value.trigger=="manual" }
            if(eligible() && !held() && needsHandoff) {
                // Foreground coroutines are not a background execution entitlement.
                service.cancelScheduled("background")
                scope.launch(Dispatchers.IO) {
                    // The worker must not mistake the cancelling foreground job for a task
                    // that is still taking care of this round and immediately return success.
                    service.awaitSync()
                    if(!eligible() || held())return@launch
                    if(foreground) { hint("foreground_return");return@launch }
                    val request=OneTimeWorkRequestBuilder<LanSyncWorker>().setBackoffCriteria(BackoffPolicy.EXPONENTIAL,15,TimeUnit.MINUTES)
                    if(userRequested && android.os.Build.VERSION.SDK_INT>=31)request.setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
                    WorkManager.getInstance(context).enqueueUniqueWork(WORK+"-handoff",ExistingWorkPolicy.REPLACE,request.build())
                }
            }
            return
        }
        conflictLoop=scope.launch(Dispatchers.IO) {
            while(isActive) {
                try { if(eligible() && !held() && !service.networkPolicy.waitingForWifi)service.exchangeConflictStatus() }
                catch(e: CancellationException) { throw e }
                catch(_: Exception) { /* Offline/busy retains local intent; no full-library retry storm. */ }
                delay(2000)
            }
        }
        loop=scope.launch(Dispatchers.IO) {
            hint("foreground")
            while(isActive) {
                delay(30_000)
                if(!eligible() || held() || service.networkPolicy.waitingForWifi)continue
                if(System.currentTimeMillis()<_state.value.backoffUntil)continue
                hint("periodic_check")
            }
        }
    }
    fun hint(reason: String,manual: Boolean=false) {
        synchronized(lock) {
            if(!eligible())return
            if(manual) { service.store.update { it.put("autoCancelled",false) };failures=0;_state.value=_state.value.copy(backoffUntil=0) }
            if(held())return
            // trigger identifies the current/last execution. A pending hint must not
            // relabel a WorkManager run (including a completed run with dirty follow-up).
            _state.value=_state.value.copy(dirty=true,cancelled=false)
            if(service.networkPolicy.waitingForWifi) { _state.value=_state.value.copy(state="waiting_for_wifi");return }
            if(_state.value.running)return
            if(!foreground && !manual)return // WorkManager owns background execution, no wakeup loop.
            val now=System.currentTimeMillis();if(firstHint==0L)firstHint=now
            timer?.cancel()
            val wait=if(manual)0L else minOf(2000L,(firstHint+10_000-now).coerceAtLeast(0))
            timer=scope.launch(Dispatchers.IO) {
                delay(maxOf(wait,(_state.value.backoffUntil-System.currentTimeMillis()).coerceAtLeast(0)))
                synchronized(lock) { firstHint=0 }
                if(!held() && eligible()) {
                    try {
                        if(manual)service.startScheduled(reason)
                        else {
                            synchronized(lock) { _state.value=_state.value.copy(state="checking",dirty=false) }
                            val started=service.checkScheduled(reason)
                            if(!started)synchronized(lock) { _state.value=_state.value.copy(state="idle") }
                        }
                    } catch(e: CancellationException) { throw e }
                    catch(_: Exception) { failed("offline_or_unavailable") }
                }
            }
        }
    }
    fun begin(reason: String) { synchronized(lock) { workerOwned=reason=="workmanager_fallback";_state.value=_state.value.copy(trigger=reason,running=true,dirty=false,state="running") } }
    fun report() { synchronized(lock) {
        reportPending=true
        if(reportJob?.isActive==true)return
        reportJob=scope.launch(Dispatchers.IO) {
            while(isActive) {
                val snapshot=synchronized(lock) {
                    if(!reportPending) { reportJob=null;return@launch }
                    reportPending=false;_state.value.json()
                }
                service.reportFileSchedule(snapshot)
                delay(200)
            }
        }
    } }
    fun phase(stage: String) { synchronized(lock) {
        if(_state.value.running && _state.value.state!=stage) { _state.value=_state.value.copy(state=stage);report() }
    } }
    fun finish(success: Boolean,metadata: Long,hash: Long,bytes: Long,handoff: Boolean=false,preparationMs: Long=0,firstTransferMs: Long=-1,requests: Long=0,cacheHit: Boolean=false,metadataRows: Long=0,incremental: Boolean=false,reusedBytes: Long=0,reusedFiles: Int=0,indexHits: Int=0,indexMisses: Int=0,indexRefreshed: Int=0,libraryReused: Int=0) {
        synchronized(lock) {
            val old=_state.value;val now=System.currentTimeMillis()
            _state.value=old.copy(running=false,lastSuccess=if(success)now else old.lastSuccess,nextReconcile=0,lastMetadataObservations=metadata,lastHashBytes=hash,lastTransferBytes=bytes,lastPreparationMs=preparationMs,lastFirstTransferMs=firstTransferMs,lastProtocolRequests=requests,lastManifestCacheHit=cacheHit,lastMetadataRows=metadataRows,lastIncremental=incremental,lastAudioReusedBytes=reusedBytes,lastAudioReusedFiles=reusedFiles,lastLocalIndexHits=indexHits,lastLocalIndexMisses=indexMisses,lastLocalIndexRefreshedFiles=indexRefreshed,lastLibraryMetadataReusedFiles=libraryReused,state=if(held())"cancelled" else if(success)"idle" else "backoff")
            if(service.networkPolicy.waitingForWifi) _state.value=_state.value.copy(state="waiting_for_wifi",waitingForWifi=true,backoffUntil=0)
            else if(handoff && !held())_state.value=_state.value.copy(state="waiting_for_background",backoffUntil=0)
            else if(success) { failures=0;_state.value=_state.value.copy(backoffUntil=0) }else failed("retry_backoff")
            if(old.dirty && !held())hint("dirty_followup")
        }
        report()
    }
    private fun failed(reason: String) { synchronized(lock) {
        failures=(failures+1).coerceAtMost(6)
        _state.value=_state.value.copy(state=reason,backoffUntil=System.currentTimeMillis()+minOf(900_000L,30_000L*(1L shl (failures-1))))
    } }
    fun cancel() { synchronized(lock) {
        service.store.update { it.put("autoCancelled",true) };timer?.cancel();firstHint=0
        _state.value=_state.value.copy(dirty=false,cancelled=true,state="cancelled")
    } }
    fun resume() { service.store.update { it.put("autoCancelled",false) };synchronized(lock) { failures=0;_state.value=_state.value.copy(backoffUntil=0) };configure() }
    suspend fun background(): Boolean {
        if(!service.networkPolicy.hasSyncNetwork)return true
        if(!eligible() || held())return true
        if(System.currentTimeMillis()<_state.value.backoffUntil)return true
        if(service.isSyncRunning())return true
        try { service.checkScheduled("workmanager_fallback") }
        catch(e: CancellationException) { throw e }
        catch(_: Exception) { failed("offline_or_unavailable");return false }
        return try { withTimeout(8*60_000L) { service.awaitSync() };service.status.value.problems.isEmpty() }
        catch(e: CancellationException) { service.cancelScheduled("interrupted");throw e }
    }
    companion object {
        const val WORK="lan-metadata-sync"
    }
}

class LanSyncWorker(context: Context,params: WorkerParameters): CoroutineWorker(context,params) {
    @EntryPoint @InstallIn(SingletonComponent::class)
    interface Dependencies { fun lan(): LanSyncService }
    override suspend fun doWork(): Result {
        val service=EntryPointAccessors.fromApplication(applicationContext,Dependencies::class.java).lan()
        return if(service.backgroundSync())Result.success() else Result.retry()
    }
}
