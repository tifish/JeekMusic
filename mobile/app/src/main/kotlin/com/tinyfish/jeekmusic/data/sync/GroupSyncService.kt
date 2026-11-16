package com.tinyfish.jeekmusic.data.sync

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

/** Network orchestration only. SyncService owns the shared business/replica transaction. */
@Singleton
class GroupSyncService @Inject constructor(
    private val sync: SyncService,
    private val store: LanSyncStore,
    private val networkPolicy: SyncNetworkPolicy,
) {
    private val gate=Mutex()
    private val _status=MutableStateFlow(JSONObject().put("joined",false))
    val status=_status.asStateFlow()
    @Volatile private var transport: LanProtocol?=null
    suspend fun refresh(): JSONObject=sync.groupStatus().also { value ->
        val old=_status.value
        if(old.optString("groupId")==value.optString("groupId") && old.optString("incarnation")==value.optString("incarnation"))
            for(field in listOf("lastError","lastSuccess"))if(old.has(field))value.put(field,old.get(field))
        _status.value=value
    }
    fun cancel() { transport?.cancel() }
    suspend fun pause(value: Boolean) { sync.pauseGroup(value);if(value)cancel();refresh() }
    suspend fun join(peer: String)=withContext(Dispatchers.IO) { gate.withLock {
        val incarnation=sync.groupJoinIncarnation(peer)
        LanProtocol(store.connection(peer),networkPolicy).use { client ->
            transport=client
            try {
                if(client.call("capabilities").optInt("syncGroupProtocol")!=1)throw GroupSyncException("upgrade_required")
                val remote=client.call("group_join",JSONObject().put("syncGroupProtocol",1).put("incarnation",incarnation))
                val actor=sync.lanDeviceId()
                sync.joinGroup(remote,actor,incarnation,peer)
            } finally { transport=null }
        }
        refresh()
    } }
    suspend fun recover()=withContext(Dispatchers.IO) { gate.withLock {
        val local=refresh();check(local.optBoolean("joined"))
        sync.groupFrame { true }
        val incarnation=sync.groupRecoveryIncarnation()
        LanProtocol(store.connection(local.getString("peerId")),networkPolicy).use { client ->
            transport=client
            try {
                val remote=client.call("group_recover",JSONObject().put("syncGroupProtocol",1).put("groupId",local.getString("groupId")).put("incarnation",incarnation))
                sync.recoverGroup(remote,incarnation)
            } finally { transport=null }
        }
        refresh()
    } }
    internal suspend fun synchronize(client: LanProtocol)=gate.withLock {
        var local=refresh()
        if(!local.optBoolean("joined") || local.optBoolean("paused"))return@withLock
        transport=client
        fun call(op: String,extra: JSONObject=JSONObject()): JSONObject {
            val request=JSONObject().put("syncGroupProtocol",1).put("incarnation",local.getString("incarnation")).put("groupId",local.getString("groupId"))
            for(key in extra.keys())request.put(key,extra.get(key))
            return client.call(op,request)
        }
        try {
            sync.groupFrame { true }
            val remote=call("group_info")
            if(remote.getString("groupId")!=local.getString("groupId") || remote.getString("coordinatorDeviceId")!=local.getString("coordinatorDeviceId"))throw GroupSyncException("group_mismatch")
            sync.updateGroupAliases(remote)
            sync.groupFrame { it.resetGeneration(remote.getLong("generation")) }
            var batches=0
            while(batches++<100) {
                val outbox=sync.groupFrame { it.outbox() };if(outbox.isEmpty())break
                val response=call("group_push",JSONObject().put("mutations",JSONArray(outbox.map(GroupSyncContract::writeMutation))))
                sync.groupFrame { it.acceptReceipts(response.getJSONArray("receipts")) }
            }
            val progress=sync.groupFrame { it.snapshotProgress() }
            if(progress.getBoolean("required")) {
                sync.groupFrame { it.restartSnapshot() }
                val snapshot=UUID.randomUUID().toString()
                var page=call("group_snapshot",JSONObject().put("snapshotId",snapshot))
                while(true) {
                    sync.groupFrame { it.receiveSnapshot(page) }
                    if(!page.getBoolean("hasMore"))break
                    page=call("group_snapshot_rows",JSONObject().put("snapshotId",snapshot).put("after",page.getLong("next")))
                }
            }
            var through: Long?=null;batches=0
            while(batches++<100) {
                local=sync.groupStatus()
                val arguments=JSONObject().put("generation",local.getLong("generation")).put("cursor",local.getLong("cursor"))
                through?.let { arguments.put("through",it) }
                val page=try { call("group_pull",arguments) } catch(exception: LanRemoteException) {
                    if(exception.code=="reset_required")sync.groupFrame { it.restartSnapshot() }
                    throw exception
                }
                through=page.getLong("head")
                sync.groupFrame { it.receive(page) }
                if(!page.getBoolean("hasMore"))break
            }
            local=sync.groupStatus()
            call("group_ack",JSONObject().put("generation",local.getLong("generation")).put("cursor",local.getLong("cursor")))
            _status.value=sync.groupStatus().put("lastSuccess",System.currentTimeMillis()).put("lastError","")
        } catch(exception: Exception) {
            _status.value=sync.groupStatus().put("lastError",when(exception) { is GroupSyncException->exception.code;is LanRemoteException->exception.code;else->"connection_failed" })
            throw exception
        } finally { transport=null }
    }
}
