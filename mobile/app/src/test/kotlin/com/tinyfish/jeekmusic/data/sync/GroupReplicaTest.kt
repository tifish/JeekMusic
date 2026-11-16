package com.tinyfish.jeekmusic.data.sync

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.objects
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class GroupReplicaTest {
    @Test fun explicitRecoveryKeepsOriginalClockAndArchivesUntilVerifiedReceipt() {
        val rows=MemoryRows();val replica=GroupReplica(rows) { 9000L }
        val server=JSONObject().put("groupId","group-a").put("generation",1).put("coordinatorDeviceId","pc-a")
        replica.join(server,"phone-a","inc-a","peer-a")
        replica.capture(mapOf("offset:song" to GroupSeed("10",17)))
        val original=replica.outbox().single()
        replica.recover(JSONObject(server.toString()).put("generation",2),"inc-new")
        val pending=replica.outbox().single()
        assertEquals(original.version.clock,pending.version.clock);assertEquals(original.payload,pending.payload)
        assertEquals("inc-new",pending.version.deviceIncarnation)
        replica.recover(JSONObject(server.toString()).put("generation",2),"inc-new");assertEquals(pending,replica.outbox().single())
        assertEquals(1,rows.scan("recovery:group-a").size)
        replica.acceptReceipts(org.json.JSONArray().put(JSONObject().put("originSequence",1).put("receipt",1).put("fingerprint",GroupReplica.fingerprint(pending))))
        assertTrue(replica.outbox().isEmpty());assertTrue(rows.scan("recovery:group-a").isEmpty())
    }
    @Test fun sharedBusinessIngressRejectsPermissiveJsonAndKeepsCacheVersionsSeparate() {
        val fixture=SharedCases.load("sync-group-business.json")
        for(row in fixture.getJSONArray("invalid").objects()) {
            val mutation=GroupMutation("group-a",1,row.getString("key"),GroupVersion(GroupClock(1,0),"pc-a","inc-a",1),row.getString("payload"))
            assertThrows(Exception::class.java) { GroupUserData.materialize(listOf(mutation),2) }
        }
        val seeds=GroupUserData.flatten(fixture.getJSONObject("cacheState"));assertEquals(3,seeds.size)
        val mutations=seeds.entries.mapIndexed { index,entry -> GroupMutation("group-a",1,entry.key,GroupVersion(GroupClock(entry.value.editedAtMs,0),"pc-a","inc-a",index+1L),entry.value.payload) }
        val actual=GroupUserData.flatten(GroupUserData.materialize(mutations,500))
        assertEquals(seeds.mapValues { it.value.payload },actual.mapValues { it.value.payload })
        assertTrue(seeds.keys.none(GroupUserData::deletesWhenMissing))
    }
    private class MemoryRows : GroupRows {
        private val rows=mutableMapOf<Pair<String,String>,GroupRow>()
        override fun read(kind: String,key: String)=rows[kind to key]?.value
        override fun put(kind: String,key: String,value: String,cursor: Long) { rows[kind to key]=GroupRow(key,value,cursor) }
        override fun delete(kind: String,key: String) { rows.remove(kind to key) }
        override fun page(kind: String,after: Long,through: Long,limit: Int)=rows.filter { it.key.first==kind && it.value.cursor>after && it.value.cursor<=through }.values.sortedWith(compareBy({ it.cursor },{ it.key })).take(limit)
        override fun scan(kind: String,afterKey: String,limit: Int)=rows.filter { it.key.first==kind && it.key.second>afterKey }.values.sortedBy { it.key }.take(limit)
        override fun count(kind: String)=rows.count { it.key.first==kind }.toLong()
    }
    @Test fun sharedMemberTranscriptKeepsProvenanceAcrossSnapshotsAndGenerationReset() {
        val fixture=SharedCases.load("sync-group-transcript.json")
        val replica=GroupReplica(MemoryRows()) { fixture.getLong("now") }
        replica.join(fixture.getJSONObject("server"),fixture.getString("actorId"),fixture.getString("incarnation"),fixture.getString("peerId"))
        for(step in fixture.getJSONArray("steps").objects()) {
            fun act() {
                when(step.getString("action")) {
                    "capture"->replica.capture(GroupUserData.flatten(step.getJSONObject("state")))
                    "project"->replica.projected(GroupUserData.flatten(step.getJSONObject("state")),replica.pendingApply())
                    "receipts"->replica.acceptReceipts(step.getJSONArray("receipts"))
                    "snapshot"->replica.receiveSnapshot(step.getJSONObject("page"))
                    "receive"->replica.receive(step.getJSONObject("page"))
                    "reset"->replica.resetGeneration(step.getLong("generation"))
                    else->error(step.toString())
                }
            }
            if(step.has("error"))assertEquals(step.getString("error"),assertThrows(GroupSyncException::class.java,::act).code) else act()
            val expected=step.getJSONObject("expected");val info=replica.info();val message=step.getString("description")
            assertEquals(message,expected.getLong("outbox"),info.getLong("pendingUploads"))
            assertEquals(message,expected.getLong("pendingApply"),info.getLong("pendingApply"))
            assertEquals(message,expected.getLong("cursor"),info.getLong("cursor"))
            assertEquals(message,expected.getLong("generation"),info.getLong("generation"))
            expected.optJSONObject("firstOutbox")?.let { assertEquals(message,GroupSyncContract.readMutation(it),replica.outbox().first()) }
        }
        for(row in fixture.getJSONArray("fingerprints").objects())
            assertEquals(row.getString("expected"),GroupReplica.fingerprint(GroupSyncContract.readMutation(row.getJSONObject("mutation"))))
    }

    @Test fun canonicalValuesUseUtf16TupleLengthsAndExcludeLegacyTimestamps() {
        val reference="collection:music/曲目🎵.flac"
        val state=JSONObject("""{"playlists":[{"uid":"list-a","name":"<test>/曲目🎵","items":[],"updated_at":1}],"tag_overrides":{}}""")
        state.getJSONObject("tag_overrides").put(reference,JSONObject().put("MOOD",JSONObject().put("value","SPORT").put("updated_at",1)))
        val seeds=GroupUserData.flatten(state)
        assertTrue(seeds.containsKey(GroupUserData.tupleKey("tag",reference,"MOOD")))
        assertFalse(seeds.getValue("playlist:list-a").payload!!.contains("updated_at"))
        val mutations=seeds.entries.mapIndexed { index,entry -> GroupMutation("group-a",1,entry.key,GroupVersion(GroupClock(1,0),"pc-a","inc-a",index+1L),entry.value.payload) }
        val restored=GroupUserData.flatten(GroupUserData.materialize(mutations,200))
        assertEquals(seeds.mapValues { it.value.payload },restored.mapValues { it.value.payload })
    }
}
