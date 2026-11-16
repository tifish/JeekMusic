using System.Text.Json.Nodes;

namespace JeekMusic.Core.Sync;

public sealed class GroupSyncException(string code) : ArgumentException(code)
{
    public string Code { get; } = code;
}

public sealed record GroupClock(long PhysicalMs, int LogicalCounter);
public sealed record GroupVersion(GroupClock Clock, string OriginDeviceId, string DeviceIncarnation, long OriginSequence);
public sealed record GroupReference(string CollectionId, string Path, int? CueTrackNo = null);
/// <summary>Payload is immutable opaque text; null is a retained tombstone, not an absent register.</summary>
public sealed record GroupMutation(string GroupId, long Generation, string Key, GroupVersion Version, string? Payload);

/// <summary>
/// Shared group contract primitives. Clock/sequence persistence and receipts belong to GroupJournal/GroupReplica;
/// authenticated client services own transport and business projection. Legacy format 2 is a separate adapter.
/// </summary>
public static class GroupSyncContract
{
    public const int Revision = 1;
    public const long MaxInteger = 9_007_199_254_740_991;
    public const int MaxCounter = int.MaxValue;
    public const int MaxBatch = 4096;
    static void Require(bool condition, string code = "invalid_contract")
    {
        if (!condition) throw new GroupSyncException(code);
    }

    static void Integer(long value, long minimum = 0) => Require(value >= minimum && value <= MaxInteger);
    static void Identity(string value) => Require(value.Length is >= 1 and <= 64 &&
        (value[0] is >= 'a' and <= 'z' or >= '0' and <= '9') &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'));

    static void Text(string value, int maximum)
    {
        Require(value.Length >= 1 && value.Length <= maximum);
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            Require(c >= ' ' && c != '\u007f');
            if (char.IsHighSurrogate(c)) Require(++i < value.Length && char.IsLowSurrogate(value[i]));
            else Require(!char.IsLowSurrogate(c));
        }
    }

    public static void Validate(GroupClock clock)
    {
        Integer(clock.PhysicalMs); Require(clock.LogicalCounter >= 0);
    }

    public static void Validate(GroupVersion version)
    {
        Validate(version.Clock); Identity(version.OriginDeviceId); Identity(version.DeviceIncarnation);
        Integer(version.OriginSequence, 1);
    }

    public static void Validate(GroupReference reference)
    {
        Identity(reference.CollectionId); Text(reference.Path, 1024);
        Require(!reference.Path.Any(c => c is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*'));
        Require(reference.Path.Split('/').All(p => p.Length > 0 && p is not ("." or "..") && !p.EndsWith('.') && !p.EndsWith(' ')));
        Require(reference.CueTrackNo is null or >= 1 and <= 99);
    }

    public static int Compare(GroupVersion left, GroupVersion right)
    {
        Validate(left); Validate(right);
        int result = left.Clock.PhysicalMs.CompareTo(right.Clock.PhysicalMs);
        if (result == 0) result = left.Clock.LogicalCounter.CompareTo(right.Clock.LogicalCounter);
        if (result == 0) result = string.CompareOrdinal(left.OriginDeviceId, right.OriginDeviceId);
        if (result == 0) result = string.CompareOrdinal(left.DeviceIncarnation, right.DeviceIncarnation);
        if (result == 0) result = left.OriginSequence.CompareTo(right.OriginSequence);
        return Math.Sign(result);
    }

    /// <summary>Advance after a local event or receive. Persist the returned clock before acknowledging or emitting.</summary>
    public static GroupClock NextClock(GroupClock local, long nowMs, GroupClock? observed = null)
    {
        Validate(local); Integer(nowMs); if (observed is not null) Validate(observed);
        var physical = Math.Max(nowMs, Math.Max(local.PhysicalMs, observed?.PhysicalMs ?? 0));
        long counter = observed is not null && physical == local.PhysicalMs && physical == observed.PhysicalMs
            ? Math.Max(local.LogicalCounter, observed.LogicalCounter) + 1L
            : physical == local.PhysicalMs ? local.LogicalCounter + 1L
            : observed is not null && physical == observed.PhysicalMs ? observed.LogicalCounter + 1L : 0;
        if (counter > MaxCounter)
        {
            Require(physical < MaxInteger, "clock_exhausted");
            return new(physical + 1, 0);
        }
        return new(physical, (int)counter);
    }

    static void Shape(JsonObject value, string[] required, params string[] optional)
    {
        Require(required.All(value.ContainsKey) && value.All(p => required.Contains(p.Key) || optional.Contains(p.Key)));
    }
    static long ReadInteger(JsonNode? value, long minimum = 0)
    {
        long result;
        if (value is JsonValue v && v.TryGetValue<long>(out var wide)) result = wide;
        else if (value is JsonValue small && small.TryGetValue<int>(out var narrow)) result = narrow;
        else throw new GroupSyncException("invalid_contract");
        Integer(result, minimum); return result;
    }
    static string ReadText(JsonNode? value)
    {
        Require(value is JsonValue v && v.TryGetValue<string>(out _)); return value!.GetValue<string>();
    }

    public static GroupVersion ReadVersion(JsonObject value)
    {
        Shape(value, ["physicalMs", "logicalCounter", "originDeviceId", "deviceIncarnation", "originSequence"]);
        var counter = ReadInteger(value["logicalCounter"]); Require(counter <= MaxCounter);
        var version = new GroupVersion(new(ReadInteger(value["physicalMs"]), (int)counter),
            ReadText(value["originDeviceId"]), ReadText(value["deviceIncarnation"]), ReadInteger(value["originSequence"], 1));
        Validate(version); return version;
    }

    public static JsonObject WriteVersion(GroupVersion version)
    {
        Validate(version);
        return new() { ["physicalMs"] = version.Clock.PhysicalMs, ["logicalCounter"] = version.Clock.LogicalCounter,
            ["originDeviceId"] = version.OriginDeviceId, ["deviceIncarnation"] = version.DeviceIncarnation,
            ["originSequence"] = version.OriginSequence };
    }

    public static GroupReference ReadReference(JsonObject value)
    {
        Shape(value, ["collectionId", "path"], "cueTrackNo");
        long? track = value.ContainsKey("cueTrackNo") ? ReadInteger(value["cueTrackNo"], 1) : null;
        Require(track is null or <= 99);
        var reference = new GroupReference(ReadText(value["collectionId"]), ReadText(value["path"]), (int?)track);
        Validate(reference); return reference;
    }

    public static JsonObject WriteReference(GroupReference reference)
    {
        Validate(reference);
        var result = new JsonObject { ["collectionId"] = reference.CollectionId, ["path"] = reference.Path };
        if (reference.CueTrackNo is { } track) result["cueTrackNo"] = track;
        return result;
    }

    public static GroupMutation ReadMutation(JsonObject value)
    {
        Shape(value, ["groupId", "generation", "key", "version", "payload"]);
        Require(value["version"] is JsonObject);
        var mutation = new GroupMutation(ReadText(value["groupId"]), ReadInteger(value["generation"], 1),
            ReadText(value["key"]), ReadVersion(value["version"]!.AsObject()),
            value["payload"] is null ? null : ReadText(value["payload"]));
        Validate(mutation); return mutation;
    }

    public static JsonObject WriteMutation(GroupMutation mutation)
    {
        Validate(mutation);
        return new() { ["groupId"] = mutation.GroupId, ["generation"] = mutation.Generation, ["key"] = mutation.Key,
            ["version"] = WriteVersion(mutation.Version), ["payload"] = mutation.Payload };
    }

    public static void Validate(GroupMutation mutation)
    {
        Identity(mutation.GroupId); Integer(mutation.Generation, 1); Text(mutation.Key, 4096); Validate(mutation.Version);
        // Payload is an opaque UTF-16 string; preserve exact bytes after JSON decoding, including whitespace.
        if (mutation.Payload is { } payload)
        {
            Require(payload.Length <= 1_048_576);
            // Payload may contain control characters (e.g. serialized text); validate Unicode separately.
            for (int i = 0; i < payload.Length; i++)
                if (char.IsHighSurrogate(payload[i])) Require(++i < payload.Length && char.IsLowSurrogate(payload[i]));
                else Require(!char.IsLowSurrogate(payload[i]));
        }
    }

    /// <summary>
    /// Reduce a bounded supplied set. Retains tombstones indefinitely. This is not a durable replay ledger:
    /// the future service must also check origins that were superseded in earlier batches.
    /// </summary>
    public static IReadOnlyList<GroupMutation> Merge(string groupId, long generation, IEnumerable<GroupMutation> changes)
    {
        Identity(groupId); Integer(generation, 1);
        var origins = new Dictionary<(string, string, long), GroupMutation>();
        var state = new SortedDictionary<string, GroupMutation>(StringComparer.Ordinal);
        int count = 0;
        foreach (var change in changes)
        {
            Require(++count <= MaxBatch, "batch_limit"); Validate(change);
            Require(change.GroupId == groupId, "group_mismatch");
            Require(change.Generation == generation, "reset_required");
            var origin = (change.Version.OriginDeviceId, change.Version.DeviceIncarnation, change.Version.OriginSequence);
            if (origins.TryGetValue(origin, out var old)) Require(old == change, "origin_reused");
            else origins.Add(origin, change);
            if (!state.TryGetValue(change.Key, out var current) || Compare(change.Version, current.Version) > 0)
                state[change.Key] = change;
        }
        return state.Values.ToArray();
    }

    /// <summary>Validate one coordinator receive-stream cursor, never an origin sequence or file timestamp.</summary>
    public static void CheckCheckpoint(string expectedGroup, long generation, long compactedThrough, long head,
        string requestGroup, long requestGeneration, long cursor)
    {
        Identity(expectedGroup); Identity(requestGroup); Integer(generation, 1); Integer(requestGeneration, 1);
        Integer(compactedThrough); Integer(head); Integer(cursor); Require(compactedThrough <= head);
        Require(expectedGroup == requestGroup, "group_mismatch");
        Require(generation == requestGeneration || requestGeneration < generation, "invalid_checkpoint");
        Require(generation == requestGeneration && cursor >= compactedThrough, "reset_required");
        Require(cursor <= head, "invalid_checkpoint");
    }

    /// <summary>Caller supplies EVERY active member, including offline members. Null means never acknowledged.</summary>
    public static long SafeCompactionThrough(long head, IReadOnlyList<long?> memberAcknowledgements)
    {
        Integer(head); Require(memberAcknowledgements.Count <= MaxBatch, "batch_limit");
        long result = head;
        foreach (var ack in memberAcknowledgements)
        {
            if (ack is { } value) { Integer(value); Require(value <= head, "invalid_checkpoint"); }
            result = Math.Min(result, ack ?? 0);
        }
        return memberAcknowledgements.Count == 0 ? 0 : result;
    }
}
