package com.tinyfish.jeekmusic.data.sync

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.objects
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class GroupSyncContractTest {
    private fun clock(value: JSONObject) = GroupClock(value.getLong("physicalMs"), value.getInt("logicalCounter"))
    private fun clockJson(value: GroupClock) = JSONObject().put("physicalMs", value.physicalMs).put("logicalCounter", value.logicalCounter)
    private fun merge(c: JSONObject, changes: List<GroupMutation>): JSONArray = JSONArray(
        GroupSyncContract.merge(c.getString("groupId"), c.getLong("generation"), changes).map(GroupSyncContract::writeMutation))

    private fun execute(c: JSONObject): Any = when (c.getString("operation")) {
        "compare" -> GroupSyncContract.compare(GroupSyncContract.readVersion(c.getJSONObject("left")), GroupSyncContract.readVersion(c.getJSONObject("right")))
        "clock" -> clockJson(GroupSyncContract.nextClock(clock(c.getJSONObject("local")), c.getLong("now"), c.optJSONObject("observed")?.let(::clock)))
        "version" -> GroupSyncContract.writeVersion(GroupSyncContract.readVersion(c.getJSONObject("input")))
        "reference" -> GroupSyncContract.writeReference(GroupSyncContract.readReference(c.getJSONObject("input")))
        "mutation" -> GroupSyncContract.writeMutation(GroupSyncContract.readMutation(c.getJSONObject("input")))
        "merge" -> merge(c, c.getJSONArray("changes").objects().map(GroupSyncContract::readMutation))
        "checkpoint" -> {
            GroupSyncContract.checkCheckpoint(c.getString("groupId"), c.getLong("generation"), c.getLong("compactedThrough"),
                c.getLong("head"), c.getString("requestGroup"), c.getLong("requestGeneration"), c.getLong("cursor"))
            "ok"
        }
        "compaction" -> {
            val acks = c.getJSONArray("acknowledgements")
            GroupSyncContract.safeCompactionThrough(c.getLong("head"), List(acks.length()) { if (acks.isNull(it)) null else acks.getLong(it) })
        }
        else -> error(c.toString())
    }

    private fun assertResult(c: JSONObject, action: () -> Any) {
        val description = c.getString("description")
        if (c.has("error")) {
            val error = assertThrows(description, GroupSyncException::class.java) { action() }
            assertEquals(description, c.getString("error"), error.code)
        } else {
            val actual = action()
            val expected = c.get("expected")
            assertEquals(description, normalized(expected), normalized(actual))
        }
    }

    private fun normalized(value: Any?): Any? = when (value) {
        is JSONObject -> value.keys().asSequence().associateWith { normalized(value.get(it)) }
        is JSONArray -> List(value.length()) { normalized(value.get(it)) }
        is Number -> value.toLong()
        JSONObject.NULL -> null
        else -> value
    }

    @Test fun sharedContract() {
        for (c in SharedCases.load("sync-group-contract.json").cases()) {
            assertResult(c) { execute(c) }
            if (c.optBoolean("permutations")) {
                val changes = c.getJSONArray("changes").objects().map(GroupSyncContract::readMutation)
                for (permutation in permutations(changes)) assertResult(c) { merge(c, permutation) }
            }
        }
    }

    private fun permutations(items: List<GroupMutation>): List<List<GroupMutation>> =
        if (items.isEmpty()) listOf(emptyList()) else items.indices.flatMap { index ->
            permutations(items.filterIndexed { i, _ -> i != index }).map { listOf(items[index]) + it }
        }

    @Test fun serializationPreservesOriginAndOpaquePayload() {
        val mutation = GroupMutation("group-a", 1, "tag/曲目🎵/mood", GroupVersion(GroupClock(100, 2), "pc-a", "boot-a", 3), " {\"value\":null}\n")
        val json = GroupSyncContract.writeMutation(mutation)
        assertEquals(mutation, GroupSyncContract.readMutation(json))
        assertEquals(mutation, GroupSyncContract.readMutation(JSONObject(json.toString())))
        val reference = GroupReference("collection-a", "曲目🎵.flac#02", 2)
        assertEquals(reference, GroupSyncContract.readReference(GroupSyncContract.writeReference(reference)))
    }

    @Test fun boundedReductionAndAcknowledgementsRejectOversizedSets() {
        val mutation = GroupMutation("group-a", 1, "key", GroupVersion(GroupClock(0, 0), "pc-a", "boot-a", 1), null)
        assertEquals(1, GroupSyncContract.merge("group-a", 1, List(GroupSyncContract.MAX_BATCH) { mutation }).size)
        assertEquals("batch_limit", assertThrows(GroupSyncException::class.java) {
            GroupSyncContract.merge("group-a", 1, List(GroupSyncContract.MAX_BATCH + 1) { mutation })
        }.code)
        assertEquals("batch_limit", assertThrows(GroupSyncException::class.java) {
            GroupSyncContract.safeCompactionThrough(1, List(GroupSyncContract.MAX_BATCH + 1) { 1L })
        }.code)
        assertEquals("invalid_contract", assertThrows(GroupSyncException::class.java) {
            GroupSyncContract.validate(mutation.copy(payload = "x".repeat(1_048_577)))
        }.code)
    }

    @Test fun exactJsonIntegersRejectFloatingPointLexemesAndNumericStrings() {
        for (value in listOf("1.0", "1e0", "\"1\"", "true", "null", "9007199254740992")) {
            val json = JSONObject("""{"physicalMs":$value,"logicalCounter":0,"originDeviceId":"pc-a","deviceIncarnation":"boot-a","originSequence":1}""")
            assertEquals("invalid_contract", assertThrows(GroupSyncException::class.java) { GroupSyncContract.readVersion(json) }.code)
        }
    }

    @Test fun invalidUnicodeAndControlsCannotBecomeLogicalReferences() {
        for (path in listOf("a\ud800.flac", "a\udc00.flac", "a\u0000.flac", "a\u007f.flac"))
            assertEquals("invalid_contract", assertThrows(GroupSyncException::class.java) {
                GroupSyncContract.validate(GroupReference("collection-a", path))
            }.code)
    }

    @Test fun snapshotReplayNeedsResetAndNeverUsesOriginHighWaterAsDeliveryProof() {
        val version = GroupVersion(GroupClock(1, 0), "pc-a", "boot-a", 1)
        val old = GroupMutation("group-a", 1, "a", version, "old")
        val deletion = old.copy(version = version.copy(clock = GroupClock(2, 0), originSequence = 2), payload = null)
        val state = GroupSyncContract.merge("group-a", 1, listOf(old, deletion))
        assertEquals(state, GroupSyncContract.merge("group-a", 1, state + old))
        assertEquals("reset_required", assertThrows(GroupSyncException::class.java) {
            GroupSyncContract.checkCheckpoint("group-a", 2, 2, 3, "group-a", 1, 3)
        }.code)
        assertEquals("reset_required", assertThrows(GroupSyncException::class.java) { GroupSyncContract.merge("group-a", 2, listOf(old)) }.code)
        assertEquals(0, GroupSyncContract.safeCompactionThrough(3, listOf(3, null)))
    }
}
