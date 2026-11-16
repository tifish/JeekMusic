package com.tinyfish.jeekmusic.data.sync

import org.json.JSONObject

class GroupSyncException(val code: String) : IllegalArgumentException(code)
data class GroupClock(val physicalMs: Long, val logicalCounter: Int)
data class GroupVersion(val clock: GroupClock, val originDeviceId: String, val deviceIncarnation: String, val originSequence: Long)
data class GroupReference(val collectionId: String, val path: String, val cueTrackNo: Int? = null)
/** Immutable opaque payload text; null is a retained tombstone, not an absent register. */
data class GroupMutation(val groupId: String, val generation: Long, val key: String, val version: GroupVersion, val payload: String?)

/** P0 pure primitives only: no legacy format 2, network, persistence or capabilities integration. */
object GroupSyncContract {
    const val REVISION = 1
    const val MAX_INTEGER = 9_007_199_254_740_991L
    const val MAX_COUNTER = Int.MAX_VALUE
    const val MAX_BATCH = 4096

    private fun check(condition: Boolean, code: String = "invalid_contract") {
        if (!condition) throw GroupSyncException(code)
    }
    private fun integer(value: Long, minimum: Long = 0) = check(value in minimum..MAX_INTEGER)
    private fun identity(value: String) = check(value.length in 1..64 && (value.first() in 'a'..'z' || value.first() in '0'..'9') &&
        value.all { it in 'a'..'z' || it in '0'..'9' || it == '-' || it == '_' })

    private fun unicode(value: String) {
        var i = 0
        while (i < value.length) {
            val c = value[i++]
            if (c.isHighSurrogate()) check(i < value.length && value[i++].isLowSurrogate())
            else check(!c.isLowSurrogate())
        }
    }
    private fun text(value: String, maximum: Int) {
        check(value.length in 1..maximum && value.all { it >= ' ' && it != '\u007f' })
        unicode(value)
    }

    fun validate(clock: GroupClock) { integer(clock.physicalMs); check(clock.logicalCounter >= 0) }
    fun validate(version: GroupVersion) {
        validate(version.clock); identity(version.originDeviceId); identity(version.deviceIncarnation)
        integer(version.originSequence, 1)
    }
    fun validate(reference: GroupReference) {
        identity(reference.collectionId); text(reference.path, 1024)
        check(reference.path.none { it in "\\:<>\"|?*" })
        check(reference.path.split('/').all { it.isNotEmpty() && it != "." && it != ".." && !it.endsWith('.') && !it.endsWith(' ') })
        check(reference.cueTrackNo == null || reference.cueTrackNo in 1..99)
    }
    fun validate(mutation: GroupMutation) {
        identity(mutation.groupId); integer(mutation.generation, 1); text(mutation.key, 4096); validate(mutation.version)
        mutation.payload?.let { check(it.length <= 1_048_576); unicode(it) }
    }

    fun compare(left: GroupVersion, right: GroupVersion): Int {
        validate(left); validate(right)
        val result = compareValuesBy(left, right, { it.clock.physicalMs }, { it.clock.logicalCounter },
            { it.originDeviceId }, { it.deviceIncarnation }, { it.originSequence })
        return result.compareTo(0)
    }

    /** Persist the result before acknowledging reception or emitting a local change. */
    fun nextClock(local: GroupClock, nowMs: Long, observed: GroupClock? = null): GroupClock {
        validate(local); integer(nowMs); observed?.let(::validate)
        val physical = maxOf(nowMs, local.physicalMs, observed?.physicalMs ?: 0)
        val counter = when {
            observed != null && physical == local.physicalMs && physical == observed.physicalMs -> maxOf(local.logicalCounter, observed.logicalCounter).toLong() + 1
            physical == local.physicalMs -> local.logicalCounter.toLong() + 1
            observed != null && physical == observed.physicalMs -> observed.logicalCounter.toLong() + 1
            else -> 0L
        }
        if (counter > MAX_COUNTER) {
            check(physical < MAX_INTEGER, "clock_exhausted")
            return GroupClock(physical + 1, 0)
        }
        return GroupClock(physical, counter.toInt())
    }

    private fun shape(value: JSONObject, required: Set<String>, optional: Set<String> = emptySet()) {
        val keys = value.keys().asSequence().toSet()
        check(keys.containsAll(required) && (keys - required - optional).isEmpty())
    }
    private fun readInteger(value: Any?, minimum: Long = 0): Long {
        check(value is Int || value is Long)
        return (value as Number).toLong().also { integer(it, minimum) }
    }
    private fun readText(value: Any?): String { check(value is String); return value as String }

    fun readVersion(value: JSONObject): GroupVersion {
        shape(value, setOf("physicalMs", "logicalCounter", "originDeviceId", "deviceIncarnation", "originSequence"))
        val counter = readInteger(value.opt("logicalCounter")); check(counter <= MAX_COUNTER)
        return GroupVersion(GroupClock(readInteger(value.opt("physicalMs")), counter.toInt()),
            readText(value.opt("originDeviceId")), readText(value.opt("deviceIncarnation")), readInteger(value.opt("originSequence"), 1)).also(::validate)
    }
    fun writeVersion(version: GroupVersion): JSONObject {
        validate(version)
        return JSONObject().put("physicalMs", version.clock.physicalMs).put("logicalCounter", version.clock.logicalCounter)
            .put("originDeviceId", version.originDeviceId).put("deviceIncarnation", version.deviceIncarnation).put("originSequence", version.originSequence)
    }
    fun readReference(value: JSONObject): GroupReference {
        shape(value, setOf("collectionId", "path"), setOf("cueTrackNo"))
        val track = if (value.has("cueTrackNo")) readInteger(value.opt("cueTrackNo"), 1) else null
        check(track == null || track <= 99)
        return GroupReference(readText(value.opt("collectionId")), readText(value.opt("path")), track?.toInt()).also(::validate)
    }
    fun writeReference(reference: GroupReference): JSONObject {
        validate(reference)
        return JSONObject().put("collectionId", reference.collectionId).put("path", reference.path).also {
            reference.cueTrackNo?.let { track -> it.put("cueTrackNo", track) }
        }
    }
    fun readMutation(value: JSONObject): GroupMutation {
        shape(value, setOf("groupId", "generation", "key", "version", "payload"))
        check(value.opt("version") is JSONObject)
        return GroupMutation(readText(value.opt("groupId")), readInteger(value.opt("generation"), 1), readText(value.opt("key")),
            readVersion(value.getJSONObject("version")), if (value.isNull("payload")) null else readText(value.opt("payload"))).also(::validate)
    }
    fun writeMutation(mutation: GroupMutation): JSONObject {
        validate(mutation)
        return JSONObject().put("groupId", mutation.groupId).put("generation", mutation.generation).put("key", mutation.key)
            .put("version", writeVersion(mutation.version)).put("payload", mutation.payload ?: JSONObject.NULL)
    }

    /** Bounded supplied-set reduction, NOT a durable replay ledger. Retains tombstones without a time limit. */
    fun merge(groupId: String, generation: Long, changes: Iterable<GroupMutation>): List<GroupMutation> {
        identity(groupId); integer(generation, 1)
        val origins = mutableMapOf<Triple<String, String, Long>, GroupMutation>()
        val state = sortedMapOf<String, GroupMutation>()
        var count = 0
        for (change in changes) {
            check(++count <= MAX_BATCH, "batch_limit"); validate(change)
            check(change.groupId == groupId, "group_mismatch")
            check(change.generation == generation, "reset_required")
            val origin = Triple(change.version.originDeviceId, change.version.deviceIncarnation, change.version.originSequence)
            val old = origins[origin]
            if (old != null) check(old == change, "origin_reused") else origins[origin] = change
            val current = state[change.key]
            if (current == null || compare(change.version, current.version) > 0) state[change.key] = change
        }
        return state.values.toList()
    }

    /** Cursor is from ONE coordinator receive stream, not an origin sequence or a file timestamp. */
    fun checkCheckpoint(expectedGroup: String, generation: Long, compactedThrough: Long, head: Long,
        requestGroup: String, requestGeneration: Long, cursor: Long) {
        identity(expectedGroup); identity(requestGroup); integer(generation, 1); integer(requestGeneration, 1)
        integer(compactedThrough); integer(head); integer(cursor); check(compactedThrough <= head)
        check(expectedGroup == requestGroup, "group_mismatch")
        check(requestGeneration <= generation, "invalid_checkpoint")
        check(requestGeneration == generation && cursor >= compactedThrough, "reset_required")
        check(cursor <= head, "invalid_checkpoint")
    }

    /** Include EVERY active member, including offline members; null means never acknowledged. */
    fun safeCompactionThrough(head: Long, memberAcknowledgements: List<Long?>): Long {
        integer(head); check(memberAcknowledgements.size <= MAX_BATCH, "batch_limit")
        var result = head
        for (ack in memberAcknowledgements) {
            if (ack != null) { integer(ack); check(ack <= head, "invalid_checkpoint") }
            result = minOf(result, ack ?: 0)
        }
        return if (memberAcknowledgements.isEmpty()) 0 else result
    }
}
