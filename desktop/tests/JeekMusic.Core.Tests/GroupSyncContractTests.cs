using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Core.Tests;

public sealed class GroupSyncContractTests
{
    public static IEnumerable<object[]> Cases() => SharedCases.Load("sync-group-contract.json").Cases()
        .Select(c => new object[] { c.Description(), c });

    static GroupClock Clock(JsonNode node) => new(node["physicalMs"]!.GetValue<long>(), node["logicalCounter"]!.GetValue<int>());
    static JsonObject ClockJson(GroupClock clock) => new() { ["physicalMs"] = clock.PhysicalMs, ["logicalCounter"] = clock.LogicalCounter };
    static JsonArray Merge(JsonObject c, IEnumerable<GroupMutation> changes) => new(GroupSyncContract.Merge(
        c["groupId"]!.GetValue<string>(), c["generation"]!.GetValue<long>(), changes)
        .Select(change => (JsonNode)GroupSyncContract.WriteMutation(change)).ToArray());

    static JsonNode Execute(JsonObject c) => c["operation"]!.GetValue<string>() switch
    {
        "compare" => JsonValue.Create(GroupSyncContract.Compare(GroupSyncContract.ReadVersion(c["left"]!.AsObject()), GroupSyncContract.ReadVersion(c["right"]!.AsObject())))!,
        "clock" => ClockJson(GroupSyncContract.NextClock(Clock(c["local"]!), c["now"]!.GetValue<long>(), c["observed"] is { } observed ? Clock(observed) : null)),
        "version" => GroupSyncContract.WriteVersion(GroupSyncContract.ReadVersion(c["input"]!.AsObject())),
        "reference" => GroupSyncContract.WriteReference(GroupSyncContract.ReadReference(c["input"]!.AsObject())),
        "mutation" => GroupSyncContract.WriteMutation(GroupSyncContract.ReadMutation(c["input"]!.AsObject())),
        "merge" => Merge(c, c["changes"]!.AsArray().Select(n => GroupSyncContract.ReadMutation(n!.AsObject()))),
        "checkpoint" => Checkpoint(c),
        "compaction" => JsonValue.Create(GroupSyncContract.SafeCompactionThrough(c["head"]!.GetValue<long>(),
            c["acknowledgements"]!.AsArray().Select(n => n?.GetValue<long>()).ToArray()))!,
        _ => throw new InvalidOperationException(c.ToJsonString())
    };

    static JsonNode Checkpoint(JsonObject c)
    {
        GroupSyncContract.CheckCheckpoint(c["groupId"]!.GetValue<string>(), c["generation"]!.GetValue<long>(),
            c["compactedThrough"]!.GetValue<long>(), c["head"]!.GetValue<long>(), c["requestGroup"]!.GetValue<string>(),
            c["requestGeneration"]!.GetValue<long>(), c["cursor"]!.GetValue<long>());
        return JsonValue.Create("ok")!;
    }

    static void AssertResult(JsonObject c, Func<JsonNode> action)
    {
        if (c["error"] is { } error)
            Assert.Equal(error.GetValue<string>(), Assert.Throws<GroupSyncException>(() => action()).Code);
        else
        {
            var actual = action();
            Assert.True(JsonNode.DeepEquals(c["expected"], actual), c.Description() + "\n" + actual);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SharedContract(string description, JsonObject c)
    {
        Assert.False(string.IsNullOrEmpty(description));
        AssertResult(c, () => Execute(c));
        if (c["permutations"]?.GetValue<bool>() == true)
        {
            var changes = c["changes"]!.AsArray().Select(n => GroupSyncContract.ReadMutation(n!.AsObject())).ToArray();
            foreach (var permutation in Permutations(changes)) AssertResult(c, () => Merge(c, permutation));
        }
    }

    static IEnumerable<GroupMutation[]> Permutations(GroupMutation[] items)
    {
        if (items.Length == 0) { yield return []; yield break; }
        for (int i = 0; i < items.Length; i++)
            foreach (var rest in Permutations(items.Where((_, index) => index != i).ToArray()))
                yield return [items[i], .. rest];
    }

    [Fact]
    public void SerializationInMemoryAndAcrossWirePreservesOriginAndOpaquePayload()
    {
        var mutation = new GroupMutation("group-a", 1, "tag/曲目🎵/mood", new(new(100, 2), "pc-a", "boot-a", 3), " {\"value\":null}\n");
        var json = GroupSyncContract.WriteMutation(mutation);
        Assert.Equal(mutation, GroupSyncContract.ReadMutation(json));
        Assert.Equal(mutation, GroupSyncContract.ReadMutation(JsonNode.Parse(json.ToJsonString())!.AsObject()));
        var reference = new GroupReference("collection-a", "曲目🎵.flac#02", 2);
        Assert.Equal(reference, GroupSyncContract.ReadReference(GroupSyncContract.WriteReference(reference)));
    }

    [Fact]
    public void BoundedReductionAndAcknowledgementsRejectOversizedSets()
    {
        var mutation = new GroupMutation("group-a", 1, "key", new(new(0, 0), "pc-a", "boot-a", 1), null);
        Assert.Single(GroupSyncContract.Merge("group-a", 1, Enumerable.Repeat(mutation, GroupSyncContract.MaxBatch)));
        Assert.Equal("batch_limit", Assert.Throws<GroupSyncException>(() => GroupSyncContract.Merge("group-a", 1,
            Enumerable.Repeat(mutation, GroupSyncContract.MaxBatch + 1))).Code);
        Assert.Equal("batch_limit", Assert.Throws<GroupSyncException>(() => GroupSyncContract.SafeCompactionThrough(1,
            Enumerable.Repeat<long?>(1, GroupSyncContract.MaxBatch + 1).ToArray())).Code);
        Assert.Equal("invalid_contract", Assert.Throws<GroupSyncException>(() => GroupSyncContract.Validate(mutation with { Payload = new string('x', 1_048_577) })).Code);
    }

    [Fact]
    public void ExactJsonIntegersDoNotAcceptFloatingPointLexemesOrNumericStrings()
    {
        foreach (var value in new[] { "1.0", "1e0", "\"1\"", "true", "null", "9007199254740992" })
        {
            var json = JsonNode.Parse("{\"physicalMs\":" + value + ",\"logicalCounter\":0,\"originDeviceId\":\"pc-a\",\"deviceIncarnation\":\"boot-a\",\"originSequence\":1}")!.AsObject();
            Assert.Equal("invalid_contract", Assert.Throws<GroupSyncException>(() => GroupSyncContract.ReadVersion(json)).Code);
        }
    }

    [Fact]
    public void InvalidUnicodeAndControlsCannotBecomeLogicalReferences()
    {
        foreach (var path in new[] { "a\ud800.flac", "a\udc00.flac", "a\u0000.flac", "a\u007f.flac" })
            Assert.Equal("invalid_contract", Assert.Throws<GroupSyncException>(() => GroupSyncContract.Validate(new GroupReference("collection-a", path))).Code);
    }

    [Fact]
    public void SnapshotReplayNeedsResetAndNeverUsesOriginHighWaterAsDeliveryProof()
    {
        var version = new GroupVersion(new(1, 0), "pc-a", "boot-a", 1);
        var old = new GroupMutation("group-a", 1, "a", version, "old");
        var deletion = old with { Version = version with { Clock = new(2, 0), OriginSequence = 2 }, Payload = null };
        var state = GroupSyncContract.Merge("group-a", 1, [old, deletion]);
        Assert.Equal(state, GroupSyncContract.Merge("group-a", 1, state.Concat([old])));
        Assert.Equal("reset_required", Assert.Throws<GroupSyncException>(() =>
            GroupSyncContract.CheckCheckpoint("group-a", 2, 2, 3, "group-a", 1, 3)).Code);
        Assert.Equal("reset_required", Assert.Throws<GroupSyncException>(() => GroupSyncContract.Merge("group-a", 2, [old])).Code);
        Assert.Equal(0, GroupSyncContract.SafeCompactionThrough(3, [3, null]));
    }
}
