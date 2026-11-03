package com.tinyfish.jeekmusic.data.sync

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.os.FileObserver
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
import org.json.JSONObject
import java.util.concurrent.TimeUnit

data class LanSchedule(val trigger: String="startup",val dirty: Boolean=false,val running: Boolean=false,val paused: Boolean=false,val cancelled: Boolean=false,val foreground: Boolean=false,val backoffUntil: Long=0,val lastSuccess: Long=0,val nextReconcile: Long=0,val state: String="idle",val watchDirectories: Int=0,val watchFailed: Boolean=false,val lastMetadataObservations: Long=0,val lastHashBytes: Long=0,val lastTransferBytes: Long=0) {
    fun json()=JSONObject().put("trigger",trigger).put("dirty",dirty).put("running",running).put("paused",paused).put("cancelled",cancelled).put("foreground",foreground).put("backoffUntil",backoffUntil).put("lastSuccess",lastSuccess).put("nextReconcile",nextReconcile).put("state",state).put("watchDirectories",watchDirectories).put("watchFailed",watchFailed).put("lastMetadataObservations",lastMetadataObservations).put("lastHashBytes",lastHashBytes).put("lastTransferBytes",lastTransferBytes)
}

/** Events are hints only. All writes/deletes go through the same serialized metadata reconciler. */
internal class LanAutoSync(private val context: Context,private val scope: CoroutineScope,private val service: LanSyncService) {
    private val lock=Any()
    private val _state=MutableStateFlow(LanSchedule())
    val state=_state.asStateFlow()
    @Volatile private var foreground=false
    private var initialized=false
    private var timer: Job?=null
    private var loop: Job?=null
    private var firstHint=0L
    private var failures=0
    private var workerOwned=false
    private val watches=mutableListOf<FileObserver>()
    private val revisions=mutableMapOf<String,String>()
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
        runCatching { context.getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(object: ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) { if(foreground)hint("network_available") }
        }) }
        configure()
    }
    fun configure() {
        scope.launch(Dispatchers.IO) {
            val enabled=eligible()
            if(enabled)WorkManager.getInstance(context).enqueueUniquePeriodicWork(WORK,ExistingPeriodicWorkPolicy.KEEP,
                PeriodicWorkRequestBuilder<LanSyncWorker>(15,TimeUnit.MINUTES).setBackoffCriteria(BackoffPolicy.EXPONENTIAL,15,TimeUnit.MINUTES).setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build())
            else WorkManager.getInstance(context).cancelUniqueWork(WORK)
            synchronized(lock) { _state.value=_state.value.copy(paused=!enabled,cancelled=held()) }
            rebuildWatches()
            if(enabled)hint("mapping_or_startup")
            service.reportFileSchedule(_state.value.json())
        }
    }
    private fun enterForeground(value: Boolean) {
        foreground=value;synchronized(lock) { _state.value=_state.value.copy(foreground=value) }
        loop?.cancel()
        if(!value) {
            synchronized(lock) { watches.forEach { it.stopWatching() };watches.clear();timer?.cancel() }
            val needsHandoff=synchronized(lock) { if(_state.value.running)!workerOwned else _state.value.dirty }
            if(eligible() && !held() && needsHandoff) {
                // Foreground coroutines are not a background execution entitlement.
                service.cancelScheduled()
                scope.launch(Dispatchers.IO) { WorkManager.getInstance(context).enqueueUniqueWork(WORK+"-handoff",ExistingWorkPolicy.KEEP,
                    OneTimeWorkRequestBuilder<LanSyncWorker>().setBackoffCriteria(BackoffPolicy.EXPONENTIAL,15,TimeUnit.MINUTES).setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build()) }
            }
            return
        }
        loop=scope.launch(Dispatchers.IO) {
            rebuildWatches();hint("foreground")
            while(isActive) {
                delay(30_000)
                if(!eligible() || held())continue
                if(System.currentTimeMillis()<_state.value.backoffUntil)continue
                try {
                    var changed=false
                    service.store.mappings().filter { LanMappingTargets.isCurrent(it) && it.optBoolean("enabled",true) }.forEach { mapping ->
                        val revision=service.pollFileRevision(mapping)
                        val key=mapping.getString("key")
                        synchronized(lock) { if(revisions.put(key,revision)!=revision)changed=true }
                    }
                    if(changed)hint("remote_revision")
                    else if(System.currentTimeMillis()>=_state.value.nextReconcile)hint("metadata_fallback")
                }catch(_: Exception) { failed("offline_or_unavailable") }
            }
        }
    }
    fun hint(reason: String,manual: Boolean=false) {
        synchronized(lock) {
            if(!eligible())return
            if(manual) { service.store.update { it.put("autoCancelled",false) };failures=0;_state.value=_state.value.copy(backoffUntil=0) }
            if(held())return
            _state.value=_state.value.copy(trigger=reason,dirty=true,cancelled=false)
            if(_state.value.running)return
            if(!foreground && !manual)return // WorkManager owns background execution, no wakeup loop.
            val now=System.currentTimeMillis();if(firstHint==0L)firstHint=now
            timer?.cancel()
            val wait=if(manual)0L else minOf(2000L,(firstHint+10_000-now).coerceAtLeast(0))
            timer=scope.launch(Dispatchers.IO) {
                delay(maxOf(wait,(_state.value.backoffUntil-System.currentTimeMillis()).coerceAtLeast(0)))
                synchronized(lock) { firstHint=0 }
                if(!held() && eligible())service.startScheduled(reason)
            }
        }
    }
    fun begin(reason: String) { synchronized(lock) { workerOwned=reason=="workmanager_fallback";_state.value=_state.value.copy(trigger=reason,running=true,dirty=false,state="running") } }
    fun finish(success: Boolean,metadata: Long,hash: Long,bytes: Long) {
        synchronized(lock) {
            val old=_state.value;val now=System.currentTimeMillis()
            _state.value=old.copy(running=false,lastSuccess=if(success)now else old.lastSuccess,nextReconcile=now+900_000,lastMetadataObservations=metadata,lastHashBytes=hash,lastTransferBytes=bytes,state=if(held())"cancelled" else if(success)"idle" else "backoff")
            if(success) { failures=0;_state.value=_state.value.copy(backoffUntil=0) }else failed("retry_backoff")
            if(old.dirty && !held())hint("dirty_followup")
        }
        scope.launch(Dispatchers.IO) { rebuildWatches();service.reportFileSchedule(_state.value.json()) }
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
    private suspend fun rebuildWatches() {
        synchronized(lock) { watches.forEach { it.stopWatching() };watches.clear() }
        if(!foreground || !eligible())return
        var failed=false
        val paths=mutableListOf<java.io.File>()
        for(mapping in service.store.mappings().filter { LanMappingTargets.isCurrent(it) && it.optBoolean("enabled",true) }) {
            try { paths+=service.localWatchDirectories(mapping) }catch(_: Exception) { failed=true }
        }
        synchronized(lock) {
            if(!foreground)return
            for(path in paths.distinct().take(4096)) {
                val observer=object: FileObserver(path,CLOSE_WRITE or MOVED_FROM or MOVED_TO or CREATE or DELETE or DELETE_SELF or MOVE_SELF or ATTRIB or MODIFY) {
                    override fun onEvent(event: Int,name: String?) {
                        // inotify also delivers lifecycle flags (notably IN_IGNORED when we
                        // replace a watch). Those are not file changes and must not self-trigger.
                        if(event and WATCH_MASK==0)return
                        if(name?.startsWith(".")==true)return
                        hint(if(event and 0x4000!=0)"watch_overflow" else "local_directory")
                    }
                }
                try { observer.startWatching();watches+=observer }catch(_: Exception) { failed=true }
            }
            _state.value=_state.value.copy(watchDirectories=watches.size,watchFailed=failed || paths.size>4096)
        }
    }
    suspend fun background(): Boolean {
        if(!eligible() || held())return true
        if(System.currentTimeMillis()<_state.value.backoffUntil)return true
        if(service.isSyncRunning())return true
        service.startScheduled("workmanager_fallback")
        return try { withTimeout(8*60_000L) { service.awaitSync() };service.status.value.problems.isEmpty() }
        catch(e: CancellationException) { service.cancelScheduled();throw e }
    }
    companion object {
        const val WORK="lan-metadata-sync"
        const val WATCH_MASK=FileObserver.CLOSE_WRITE or FileObserver.MOVED_FROM or FileObserver.MOVED_TO or FileObserver.CREATE or FileObserver.DELETE or FileObserver.DELETE_SELF or FileObserver.MOVE_SELF or FileObserver.ATTRIB or FileObserver.MODIFY or 0x4000
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
