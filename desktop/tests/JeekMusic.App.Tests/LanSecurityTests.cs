using System.Text;
using System.Text.Json.Nodes;
using JeekMusic.Sync;
using JeekMusic.Automation;

namespace JeekMusic.App.Tests;

public sealed class LanSecurityTests
{
    [Fact]
    public void McpNestedPermissionsRequireAllBooleanFieldsAndRejectUnknownKeys()
    {
        var schema = McpTools.BuildCatalog().Single(t => t!["name"]!.GetValue<string>() == "lan_sync_control")!["inputSchema"]!.AsObject();
        var a = JsonNode.Parse("{\"action\":\"authorize\",\"deviceId\":\"test\",\"permissions\":{\"readMusic\":true,\"writeSync\":false,\"manageLearning\":false,\"downloadLearning\":false}}")!.AsObject();
        McpSession.Validate(a, schema);
        a["permissions"]!["extra"] = true; Assert.Throws<ArgumentException>(() => McpSession.Validate(a, schema));
        a["permissions"]!.AsObject().Remove("extra"); a["permissions"]!["writeSync"] = "yes";
        Assert.Throws<ArgumentException>(() => McpSession.Validate(a, schema));
        a["permissions"]!.AsObject().Remove("writeSync"); Assert.Throws<ArgumentException>(() => McpSession.Validate(a, schema));
    }
    [Fact]
    public void AdmissionSeparatesStagesBoundsDistributedKeysAndRecovers()
    {
        var b = new LanAdmission(); var now = DateTime.UtcNow;
        var peers = new List<LanAdmission.Lease>();
        for (int i = 0; i < 16; i++)
        {
            var p = Assert.IsType<LanAdmission.Lease>(b.Enter("nat" + i / 8, now.AddSeconds(i)));
            Assert.True(b.Authenticate(p, "device" + i / 2)); peers.Add(p);
        }
        for (int i = 0; i < 8; i++) peers.Add(Assert.IsType<LanAdmission.Lease>(b.Enter("attack" + i, now.AddSeconds(20 + i))));
        Assert.Equal(24, b.Active); Assert.Equal(8, b.Pending);
        Assert.Null(b.Enter("extra", now.AddSeconds(30)));
        foreach (var p in peers) b.Exit(p);
        Assert.Equal(0, b.Active);
        for (int i = 0; i < 5000; i++) { var p = b.Enter("source" + i, now.AddSeconds(31 + i / 64)); if (p is not null) b.Exit(p); }
        Assert.InRange(b.SourceCount, 0, 1024);
        var recovered = b.Enter("legitimate", now.AddMinutes(5)); Assert.NotNull(recovered); b.Exit(recovered!);
    }
    [Fact]
    public void NatSupportsEightAuthenticatedDevicesAndFourIndependentSyncSessionsPerDevice()
    {
        var b = new LanAdmission(); var now = DateTime.UtcNow;
        var peers = new List<LanAdmission.Lease>();
        for (int i = 0; i < 8; i++) { var p = b.Enter("nat", now)!; Assert.True(b.Authenticate(p, "device" + i)); peers.Add(p); }
        var ninth = b.Enter("nat", now)!; Assert.False(b.Authenticate(ninth, "extra")); b.Exit(ninth);
        foreach (var p in peers) b.Exit(p);
        var sessions = new List<LanAdmission.Lease>();
        for (int i = 0; i < 4; i++) {
            var session = b.Enter("nat", now)!;
            Assert.True(b.Authenticate(session, "same")); sessions.Add(session);
        }
        var fifth = b.Enter("other", now)!; Assert.False(b.Authenticate(fifth, "same")); b.Exit(fifth);
        b.Exit(sessions[0]);
        Assert.True(b.Authenticate(b.Enter("nat", now)!, "same"));
    }
    [Theory]
    [InlineData("{\"op\":\"auth\",\"op\":\"pair\"}")]
    [InlineData("{\"op\":[]}")]
    [InlineData("{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":1}}}}}")]
    public void AuthenticationRejectsAmbiguousFieldsAndDepth(string json) => Assert.ThrowsAny<Exception>(() => LanLimits.Parse(Encoding.UTF8.GetBytes(json), true));
    [Fact]
    public async Task FramesRejectBeforeBodyAllocationAndValidateEncodingAndCancellation()
    {
        await Assert.ThrowsAnyAsync<IOException>(() => LanSyncService.ReadFrame(new MemoryStream(new byte[] { 1, 0, 0, 0 }), maximum: 4096, authentication: true));
        Assert.ThrowsAny<Exception>(() => LanLimits.Parse(new byte[] { 123, 34, 97, 34, 58, 34, 255, 34, 125 }, true));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LanSyncService.ReadFrame(new MemoryStream(), cancelled.Token, 4096, true));
        await Assert.ThrowsAnyAsync<IOException>(() => LanSyncService.WriteFrame(new MemoryStream(), new JsonObject { ["data"] = new string('x', 8192) }, maximum: 4096));
    }
}
