using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Core.Tests;

public sealed class SyncMergeTests
{
    [Fact]
    public void ExtendedDimensionsRoundTripWithoutExposingThemToLegacyEditors()
    {
        var fixture = Core.Tests.SharedCases.Load("sync-calmness-wire.json");
        var parsed = SyncMerge.ParseFile(fixture["file"]!.ToJsonString())!;
        var tags = parsed["tag_overrides"]!;
        Assert.Equal("QUIET", SyncMerge.Str(tags["song"]!["MOOD"]!["value"]));
        Assert.Equal("CALM", SyncMerge.Str(tags["song"]!["CALMNESS"]!["value"]));
        Assert.Equal(30, SyncMerge.Number(tags["reset"]!["CALMNESS"]!["updated_at"]));
        Assert.Null(tags["reset"]!["CALMNESS"]!["value"]);
        var wire = SyncMerge.ForWire(parsed);
        Assert.All(wire["tag_overrides"]!.AsObject(), item =>
            Assert.All(item.Value!.AsObject(), dim => Assert.Contains(dim.Key, new[] { "MOOD", "VOICE", "GENRE" })));
        Assert.True(JsonNode.DeepEquals(parsed, SyncMerge.ParseFile(wire.ToJsonString())));
        Assert.True(JsonNode.DeepEquals(wire, SyncMerge.ForWire(wire)));
        Assert.True(JsonNode.DeepEquals(parsed, SyncMerge.ParseFile(SyncMerge.ForWire(fixture["file"]!.AsObject()).ToJsonString())));
        foreach (var invalid in fixture["invalid_extensions"]!.AsArray())
        {
            var file = fixture["file"]!.DeepClone();
            file["extended_tag_overrides"] = invalid!.DeepClone();
            Assert.Null(SyncMerge.ParseFile(file.ToJsonString()));
        }
    }

    [Fact]
    public void PartialNavigationKeepsBuiltinPagesAndCanonicalArtistKeys()
    {
        var input = JsonNode.Parse("""[{"type":"PLAYLISTS","ref":""},{"type":"ARTIST","ref":" Test   Artist "},{"type":"ARTIST","ref":"test artist"},{"type":"ARTIST","ref":" "},{"type":"UNKNOWN"}]""");
        var normalized = SyncMerge.NormalizeNavigation(input);
        Assert.Equal(new[] { "PLAYLISTS", "ARTIST", "FOLDERS", "ARTISTS" }, normalized.Select(n => SyncMerge.Str(n!["type"])));
        Assert.Equal("test artist", SyncMerge.Str(normalized[1]!["ref"]));
        Assert.True(JsonNode.DeepEquals(normalized, SyncMerge.NormalizeNavigation(normalized)));
        Assert.Equal(3, SyncMerge.NormalizeNavigation(null).Count);
    }

    [Theory]
    [InlineData("\"bad\"")]
    [InlineData("{\"song\":42}")]
    [InlineData("{\"song\":{\"MOOD\":true}}")]
    public void MalformedPeerIsRejectedBeforeMerge(string overrides)
    {
        Assert.Null(SyncMerge.ParseFile("{\"format\":2,\"device\":{\"id\":\"phone\",\"name\":\"Phone\"},\"written_at\":1,\"tag_overrides\":" + overrides + "}"));
        Assert.NotNull(SyncMerge.ParseFile("""{"format":2,"device":{"id":"phone","name":"Phone"},"written_at":1,"lyrics_offsets":{"song":{"offset_ms":-500,"updated_at":1}}}"""));
    }

    [Fact]
    public void InMemoryOffsetsAndLegacyFormatMergeAfterSerialization()
    {
        var local = new JsonObject { ["lyrics_offsets"] = new JsonObject
        {
            ["song.flac"] = new JsonObject { ["offset_ms"] = 250, ["updated_at"] = 10L }
        } };
        var remote = new JsonObject { ["format"] = 2, ["device"] = new JsonObject { ["id"] = "phone", ["name"] = "Phone" },
            ["written_at"] = 20L, ["lyrics_offsets"] = new JsonObject
            { ["song.flac"] = new JsonObject { ["offset_ms"] = 500, ["updated_at"] = 20L } } };
        var first = SyncMerge.Merge(local, [remote], "pc", 20);
        Assert.Equal(500, SyncMerge.Number(first.State["lyrics_offsets"]!["song.flac"]!["offset_ms"]));
        var second = SyncMerge.Merge(first.State, [JsonNode.Parse(remote.ToJsonString())!.AsObject()], "pc", 20);
        Assert.Empty(second.Changes);
        var legacy = SyncMerge.ParseLegacy("""{"format":1,"playlists":[{"name":"Old","items":[]}]}""")!;
        Assert.Single(SyncMerge.Merge(new JsonObject(), [legacy], "pc", 20).State["playlists"]!.AsArray());
    }

    [Fact]
    public void NormalizationKeepsNewestDimensionAndReset()
    {
        var state = JsonNode.Parse("""
            {"tag_overrides":{
              "parent/song.flac":{"MOOD":{"value":"SPORT","updated_at":10},"GENRE":{"value":"CLASSICAL","updated_at":30}},
              "song.flac":{"MOOD":{"value":null,"updated_at":20}}},
             "lyrics_offsets":{"parent/song.flac":{"offset_ms":500,"updated_at":30},"song.flac":{"offset_ms":0,"updated_at":10}}}
            """)!.AsObject();
        var result = SyncMerge.NormalizeReferences(state, r => r.Split('/')[^1]);
        Assert.Single(result["tag_overrides"]!.AsObject());
        Assert.Null(result["tag_overrides"]!["song.flac"]!["MOOD"]!["value"]);
        Assert.Equal("CLASSICAL", SyncMerge.Str(result["tag_overrides"]!["song.flac"]!["GENRE"]!["value"]));
        Assert.Equal(500, SyncMerge.Number(result["lyrics_offsets"]!["song.flac"]!["offset_ms"]));
        Assert.Equal(2, state["tag_overrides"]!.AsObject().Count);
    }

    [Fact]
    public void SharedCases()
    {
        foreach (var c in Core.Tests.SharedCases.Load("sync-merge.json").Cases())
        {
            var result = SyncMerge.Merge(c["local"]!.AsObject(), SyncMerge.Objects(c["remotes"]), c.Str("self")!, SyncMerge.Number(c["now"]));
            var expected = c["expected"]!;
            var expectedState = expected["state"]!.DeepClone();
            foreach (var key in new[] { "playlists", "deleted_playlists" })
                expectedState[key] = new JsonArray(SyncMerge.Objects(expectedState[key]).OrderBy(p => SyncMerge.Str(p["uid"]), StringComparer.Ordinal).Select(p => (JsonNode)p.DeepClone()).ToArray());
            Assert.True(JsonNode.DeepEquals(expectedState, result.State), c.Description() + "\n" + result.State);
            Assert.Equal(expected["changes"].Strings(), result.Changes.Select(x => $"{x.Kind}:{x.Key}@{x.DeviceId}").Order(StringComparer.Ordinal));
            Assert.Equal(expected["replaced"].Strings(), result.Replaced.Select(x => (x.Deleted ? "deleted:" : "") + SyncMerge.Str(x.Old["uid"])).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void LegacyIdentifiersMatchMobile()
    {
        foreach (var row in Core.Tests.SharedCases.Load("sync-merge.json").Cases("legacy_uids"))
            Assert.Equal(row.Str("uid"), SyncMerge.LegacyUid(row.Str("name")!));
    }
}
