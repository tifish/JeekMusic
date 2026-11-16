package com.tinyfish.jeekmusic.data.sync

import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest

data class GroupRow(val key: String,val value: String,val cursor: Long)
interface GroupRows {
    fun read(kind: String,key: String): String?
    fun put(kind: String,key: String,value: String,cursor: Long=0)
    fun delete(kind: String,key: String)
    fun page(kind: String,after: Long,through: Long,limit: Int=200): List<GroupRow>
    fun scan(kind: String,afterKey: String="",limit: Int=200): List<GroupRow>
    fun count(kind: String): Long
}

/** Android is a member, never a coordinator. Caller owns the shared Room business transaction. */
class GroupReplica(private val rows: GroupRows,private val now: ()->Long) {
    private fun check(value: Boolean,code: String) { if(!value)throw GroupSyncException(code) }
    private fun read(kind: String,key: String)=rows.read(kind,key)?.let(::JSONObject)
    private fun put(kind: String,key: String,value: JSONObject,cursor: Long=0)=rows.put(kind,key,value.toString(),cursor)
    private fun number(value: JSONObject,key: String)=value.optLong(key)
    private fun wireNumber(value: JSONObject,key: String,minimum: Long=0): Long {
        val raw=value.opt(key);check(raw is Int || raw is Long,"invalid_contract")
        return (raw as Number).toLong().also { check(it in minimum..GroupSyncContract.MAX_INTEGER,"invalid_contract") }
    }
    private fun pageHeader(page: JSONObject,snapshot: Boolean) {
        wireNumber(page,"generation",1);wireNumber(page,"head");wireNumber(page,"next")
        check(page.opt("groupId") is String && page.opt("hasMore") is Boolean && (!snapshot || page.opt("snapshotId") is String),"invalid_contract")
    }
    private fun kind(meta: JSONObject,name: String)="$name:${meta.getString("groupId") }"
    private fun sequence(value: Long)=value.toString().padStart(16,'0')
    private fun meta()=read("meta","active") ?: throw GroupSyncException("group_not_joined")
    private fun save(meta: JSONObject)=put("meta","active",meta)
    private fun clock(meta: JSONObject)=GroupClock(meta.optLong("clockMs"),meta.optInt("clockCounter"))
    private fun setClock(meta: JSONObject,value: GroupClock) { meta.put("clockMs",value.physicalMs).put("clockCounter",value.logicalCounter) }
    val active get()=rows.read("meta","active")!=null
    fun info(): JSONObject {
        val meta=meta()
        return JSONObject().put("groupId",meta.getString("groupId")).put("generation",meta.getLong("generation")).put("role","member")
            .put("coordinatorDeviceId",meta.getString("coordinatorDeviceId")).put("actorId",meta.getString("actorId"))
            .put("incarnation",meta.getString("incarnation")).put("peerId",meta.getString("peerId")).put("cursor",meta.optLong("cursor"))
            .put("head",meta.optLong("head")).put("clockAheadMs",maxOf(0,meta.optLong("clockMs")-now())).put("pendingUploads",rows.count(kind(meta,"outbox")))
            .put("pendingApply",rows.count(kind(meta,"apply"))).put("registers",rows.count(kind(meta,"register"))).put("userDataFormat",3)
    }
    fun join(server: JSONObject,actorId: String,incarnation: String,peerId: String) {
        val groupId=server.getString("groupId");val generation=server.getLong("generation")
        wireNumber(server,"generation",1)
        GroupSyncContract.validate(GroupReference(groupId,"identity"));GroupSyncContract.validate(GroupVersion(GroupClock(0,0),actorId,incarnation,1))
        GroupSyncContract.validate(GroupVersion(GroupClock(0,0),server.getString("coordinatorDeviceId"),incarnation,1))
        check(generation in 1..GroupSyncContract.MAX_INTEGER,"invalid_contract")
        if(active) {
            val old=meta()
            check(old.getString("groupId")==groupId && old.getString("incarnation")==incarnation && old.getString("actorId")==actorId && old.getString("peerId")==peerId && old.getString("coordinatorDeviceId")==server.getString("coordinatorDeviceId"),"active_group_conflict")
            resetGeneration(generation);return
        }
        save(JSONObject().put("groupId",groupId).put("generation",generation).put("role","member").put("actorId",actorId)
            .put("coordinatorDeviceId",server.getString("coordinatorDeviceId")).put("incarnation",incarnation).put("peerId",peerId)
            .put("nextOrigin",0).put("clockMs",0).put("clockCounter",0).put("cursor",0).put("head",0)
            .put("registerCount",0).put("seeded",false).put("needsSnapshot",true))
    }
    fun resetGeneration(generation: Long) {
        val meta=meta();check(generation in meta.getLong("generation")..GroupSyncContract.MAX_INTEGER,"invalid_checkpoint")
        if(generation==meta.getLong("generation"))return
        meta.put("generation",generation).put("cursor",0).put("needsSnapshot",true)
        meta.remove("snapshotId");meta.remove("snapshotHead");meta.remove("snapshotAfter");save(meta)
    }
    private fun validateBusiness(mutation: GroupMutation) {
        GroupSyncContract.validate(mutation)
        mutation.payload?.let { check(it.toByteArray(Charsets.UTF_8).size<=524_288,"payload_limit") }
        try { GroupUserData.materialize(listOf(mutation),1) } catch(_:org.json.JSONException) { throw GroupSyncException("invalid_user_data") }
    }
    fun capture(current: Map<String,GroupSeed>) {
        val meta=meta();check(current.size<=100_000,"register_limit")
        val candidates=current.toSortedMap();var after=""
        while(true) {
            val page=rows.scan(kind(meta,"projection"),after)
            for(row in page)if(row.key !in current && GroupUserData.deletesWhenMissing(row.key))candidates[row.key]=GroupSeed(null)
            if(page.isEmpty())break;after=page.last().key
        }
        for((key,seed) in candidates) {
            val projected=read(kind(meta,"projection"),key)
            if(projected!=null && (if(projected.isNull("payload"))null else projected.getString("payload"))==seed.payload)continue
            val sequence=meta.getLong("nextOrigin")+1;check(sequence<=GroupSyncContract.MAX_INTEGER,"origin_exhausted")
            val versionClock=if(!meta.optBoolean("seeded")) {
                check(seed.editedAtMs in 0..GroupSyncContract.MAX_INTEGER,"invalid_contract")
                GroupClock(seed.editedAtMs,0).also { if(it.physicalMs>clock(meta).physicalMs)setClock(meta,it) }
            } else GroupSyncContract.nextClock(clock(meta),now()).also { setClock(meta,it) }
            val mutation=GroupMutation(meta.getString("groupId"),meta.getLong("generation"),key,
                GroupVersion(versionClock,meta.getString("actorId"),meta.getString("incarnation"),sequence),seed.payload)
            validateBusiness(mutation);meta.put("nextOrigin",sequence)
            put(kind(meta,"outbox"),sequence(sequence),GroupSyncContract.writeMutation(mutation),sequence)
            register(meta,mutation,0)
            put(kind(meta,"projection"),key,JSONObject().put("payload",seed.payload ?: JSONObject.NULL))
            if(registerValue(meta,key).payload==seed.payload)rows.delete(kind(meta,"apply"),key)
        }
        meta.put("seeded",true);save(meta)
    }
    private fun registerValue(meta: JSONObject,key: String)=GroupSyncContract.readMutation(read(kind(meta,"register"),key)!!).copy(generation=meta.getLong("generation"))
    private fun register(meta: JSONObject,mutation: GroupMutation,receipt: Long) {
        val old=read(kind(meta,"register"),mutation.key)
        if(old!=null) {
            val previous=GroupSyncContract.readMutation(old)
            if(previous.version.originDeviceId==mutation.version.originDeviceId && previous.version.deviceIncarnation==mutation.version.deviceIncarnation && previous.version.originSequence==mutation.version.originSequence)
                check(fingerprint(previous)==fingerprint(mutation),"origin_reused")
            if(GroupSyncContract.compare(mutation.version,previous.version)<=0)return
        } else { val count=meta.optLong("registerCount");check(count<100_000,"register_limit");meta.put("registerCount",count+1) }
        put(kind(meta,"register"),mutation.key,GroupSyncContract.writeMutation(mutation),receipt)
        put(kind(meta,"apply"),mutation.key,GroupSyncContract.writeMutation(mutation),receipt)
    }
    fun outbox(): List<GroupMutation> {
        val meta=meta()
        return rows.page(kind(meta,"outbox"),0,GroupSyncContract.MAX_INTEGER).map { GroupSyncContract.readMutation(JSONObject(it.value)).copy(generation=meta.getLong("generation")) }
    }
    fun acceptReceipts(receipts: JSONArray) {
        val meta=meta();check(receipts.length()<=200,"batch_limit")
        repeat(receipts.length()) { index ->
            val receipt=receipts.getJSONObject(index);val sequence=wireNumber(receipt,"originSequence",1);wireNumber(receipt,"receipt",1)
            val pending=read(kind(meta,"outbox"),sequence(sequence));check(pending!=null,"unknown_receipt")
            check(fingerprint(GroupSyncContract.readMutation(pending!!))==receipt.getString("fingerprint"),"receipt_mismatch")
            rows.delete(kind(meta,"outbox"),sequence(sequence))
        }
        if(rows.count(kind(meta,"outbox"))==0L)deleteKind(kind(meta,"recovery"))
    }
    fun receive(page: JSONObject) {
        pageHeader(page,false)
        val meta=meta();check(page.getString("groupId")==meta.getString("groupId"),"group_mismatch")
        check(page.getLong("generation")==meta.getLong("generation"),"reset_required")
        var next=meta.getLong("cursor");val items=page.getJSONArray("rows");check(items.length()<=200,"batch_limit")
        repeat(items.length()) { index ->
            val item=items.getJSONObject(index);val receipt=wireNumber(item,"receipt",1)
            check(receipt==next+1 && receipt<=page.getLong("head"),"invalid_checkpoint")
            val mutation=GroupSyncContract.readMutation(item.getJSONObject("mutation"));validateBusiness(mutation)
            check(mutation.groupId==meta.getString("groupId") && mutation.generation==meta.getLong("generation"),"reset_required")
            register(meta,mutation,receipt);setClock(meta,GroupSyncContract.nextClock(clock(meta),now(),mutation.version.clock));next=receipt
        }
        check(next==page.getLong("next"),"invalid_checkpoint")
        meta.put("cursor",next).put("head",page.getLong("head"));save(meta)
    }
    fun receiveSnapshot(page: JSONObject) {
        pageHeader(page,true)
        val meta=meta();check(page.getString("groupId")==meta.getString("groupId"),"group_mismatch")
        check(page.getLong("generation")==meta.getLong("generation"),"reset_required")
        val id=page.getString("snapshotId");check(id.isNotEmpty(),"invalid_contract")
        val existing=meta.optString("snapshotId")
        if(existing.isNotEmpty())check(existing==id && meta.getLong("snapshotHead")==page.getLong("head"),"snapshot_changed")
        var after=if(existing.isNotEmpty())meta.getLong("snapshotAfter") else 0L
        val items=page.getJSONArray("rows");check(items.length()<=200,"batch_limit")
        repeat(items.length()) { index ->
            val item=items.getJSONObject(index);val receipt=wireNumber(item,"receipt",1)
            check(receipt>after && receipt<=page.getLong("head"),"invalid_checkpoint")
            val mutation=GroupSyncContract.readMutation(item.getJSONObject("mutation"));validateBusiness(mutation)
            check(mutation.groupId==meta.getString("groupId") && mutation.generation==meta.getLong("generation"),"reset_required")
            register(meta,mutation,receipt);setClock(meta,GroupSyncContract.nextClock(clock(meta),now(),mutation.version.clock));after=receipt
        }
        check(after==page.getLong("next"),"invalid_checkpoint")
        meta.put("snapshotId",id).put("snapshotHead",page.getLong("head")).put("snapshotAfter",after)
        if(!page.getBoolean("hasMore")) {
            meta.put("cursor",page.getLong("head")).put("head",page.getLong("head")).put("needsSnapshot",false)
            meta.remove("snapshotId");meta.remove("snapshotHead");meta.remove("snapshotAfter")
        }
        save(meta)
    }
    fun snapshotProgress(): JSONObject { val meta=meta();return JSONObject().put("required",meta.optBoolean("needsSnapshot")).put("snapshotId",meta.optString("snapshotId")).put("after",meta.optLong("snapshotAfter")) }
    fun restartSnapshot() { val meta=meta();meta.put("needsSnapshot",true);meta.remove("snapshotId");meta.remove("snapshotHead");meta.remove("snapshotAfter");save(meta) }
    fun pendingApply(): List<GroupMutation> { val meta=meta();return rows.scan(kind(meta,"apply")).map { GroupSyncContract.readMutation(JSONObject(it.value)).copy(generation=meta.getLong("generation")) } }
    fun reapplyCaches() {
        val meta=meta();var after=""
        while(true) {
            val page=rows.scan(kind(meta,"register"),after)
            for(row in page)if(GroupUserData.isCache(row.key))rows.put(kind(meta,"apply"),row.key,row.value,row.cursor)
            if(page.isEmpty())break;after=page.last().key
        }
    }
    fun projected(actual: Map<String,GroupSeed>,applied: List<GroupMutation>) {
        val meta=meta()
        for(mutation in applied) {
            check(fingerprint(registerValue(meta,mutation.key))==fingerprint(mutation),"projection_changed")
            put(kind(meta,"projection"),mutation.key,JSONObject().put("payload",actual[mutation.key]?.payload ?: JSONObject.NULL))
            rows.delete(kind(meta,"apply"),mutation.key)
        }
    }
    private fun deleteKind(kind: String) { while(true) { val page=rows.scan(kind);if(page.isEmpty())break;for(row in page)rows.delete(kind,row.key) } }
    fun recover(server: JSONObject,incarnation: String) {
        val meta=meta()
        check(server.getString("groupId")==meta.getString("groupId") && server.getString("coordinatorDeviceId")==meta.getString("coordinatorDeviceId"),"group_mismatch")
        GroupSyncContract.validate(GroupVersion(GroupClock(0,0),meta.getString("actorId"),incarnation,1))
        val generation=wireNumber(server,"generation",1);check(generation>=meta.getLong("generation"),"invalid_checkpoint")
        if(meta.getString("incarnation")==incarnation) { resetGeneration(generation);return }
        var after=""
        while(true) {
            val page=rows.scan(kind(meta,"outbox"),after)
            for(row in page) { rows.put(kind(meta,"rebase-stage"),row.key,row.value,row.cursor);rows.put(kind(meta,"recovery"),meta.getString("incarnation")+":"+row.key,row.value,row.cursor) }
            if(page.isEmpty())break;after=page.last().key
        }
        deleteKind(kind(meta,"outbox"))
        meta.put("incarnation",incarnation).put("generation",generation).put("nextOrigin",0).put("cursor",0).put("needsSnapshot",true)
        meta.remove("snapshotId");meta.remove("snapshotHead");meta.remove("snapshotAfter");after=""
        while(true) {
            val page=rows.scan(kind(meta,"rebase-stage"),after)
            for(row in page) {
                val old=GroupSyncContract.readMutation(JSONObject(row.value));val next=meta.getLong("nextOrigin")+1
                val rebased=old.copy(generation=generation,version=old.version.copy(deviceIncarnation=incarnation,originSequence=next))
                meta.put("nextOrigin",next);put(kind(meta,"outbox"),sequence(next),GroupSyncContract.writeMutation(rebased),next)
            }
            if(page.isEmpty())break;after=page.last().key
        }
        deleteKind(kind(meta,"rebase-stage"));save(meta)
    }
    companion object {
        fun fingerprint(mutation: GroupMutation): String {
            GroupSyncContract.validate(mutation)
            val digest=MessageDigest.getInstance("SHA-256")
            for(field in listOf(mutation.groupId,mutation.key,mutation.version.clock.physicalMs.toString(),mutation.version.clock.logicalCounter.toString(),
                mutation.version.originDeviceId,mutation.version.deviceIncarnation,mutation.version.originSequence.toString(),mutation.payload)) {
                val bytes=field?.toByteArray(Charsets.UTF_8)
                digest.update(((bytes?.size ?: -1).toString()+":").toByteArray(Charsets.US_ASCII));if(bytes!=null)digest.update(bytes)
            }
            return digest.digest().joinToString("") { "%02x".format(it) }
        }
    }
}
