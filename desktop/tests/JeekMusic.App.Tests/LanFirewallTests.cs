using System.Text.Json.Nodes;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

public sealed class LanFirewallTests
{
    sealed class PendingQuery : ILanFirewallBackend
    {
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<JsonObject> Query(LanFirewallRequest request) { Entered.TrySetResult(); return new TaskCompletionSource<JsonObject>().Task; }
        public Task<int> Apply(LanFirewallRequest request) => throw new InvalidOperationException();
        public Task<JsonObject> Remove(LanFirewallRequest request) => Task.FromResult(new JsonObject { ["satisfied"] = true, ["state"] = "not_required" });
    }

    [Fact]
    public async Task StopCancelsStaleReadWithoutWaitingForItsTimeout()
    {
        var backend = new PendingQuery();
        var firewall = new LanFirewall(backend, new string('a', 64), @"C:\app\JeekMusic.exe", () => { });
        var start = firewall.Set("0.0.0.0", 40000);
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await firewall.Set("0.0.0.0", 0).WaitAsync(TimeSpan.FromSeconds(2));
        await start.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("not_required", firewall.Status()["state"]!.GetValue<string>());
    }

    sealed class Fake : ILanFirewallBackend
    {
        internal List<LanFirewallRequest> Applied = [];
        internal LanFirewallRequest? Current;
        internal int Exit;
        public Task<JsonObject> Query(LanFirewallRequest request)
        {
            bool satisfied = Current == request || !request.Needed && Current is null;
            return Task.FromResult(new JsonObject { ["satisfied"] = satisfied, ["state"] = satisfied ? request.Needed ? "configured" : "not_required" : "repair_required" });
        }
        public Task<int> Apply(LanFirewallRequest request) { Applied.Add(request); if (Exit == 0) Current = request.Needed ? request : null; return Task.FromResult(Exit); }
    }
    [Fact]
    public async Task ExactPortBindAndProgramLifecycleIsIdempotentAndLoopbackRemovesOldRule()
    {
        var backend = new Fake(); var firewall = new LanFirewall(backend, new string('a', 64), @"C:\new\JeekMusic.exe", () => { });
        await firewall.Set("127.0.0.1", 40000); Assert.Empty(backend.Applied);
        await firewall.Set("192.168.1.4", 40000); Assert.Equal("configured", firewall.Status()["state"]!.GetValue<string>());
        await firewall.Set("192.168.1.4", 40000); Assert.Single(backend.Applied);
        await firewall.Set("192.168.1.4", 40001); Assert.Equal(40001, backend.Current!.Port);
        await firewall.Set("0.0.0.0", 40001); Assert.Equal("0.0.0.0", backend.Current!.Bind);
        await firewall.Set("127.0.0.1", 40001); Assert.Null(backend.Current);
        await firewall.Set("0.0.0.0", 40001); await firewall.Set("0.0.0.0", 0); Assert.Null(backend.Current);
    }
    [Theory]
    [InlineData(1223, "uac_cancelled")]
    [InlineData(5, "access_denied")]
    [InlineData(1, "configuration_failed")]
    public async Task DeniedOrFailedChangesNeverReportConfigured(int exit, string state)
    {
        var backend = new Fake { Exit = exit }; var firewall = new LanFirewall(backend, new string('a', 64), @"C:\app\JeekMusic.exe", () => { });
        await firewall.Set("0.0.0.0", 40000); Assert.Equal(state, firewall.Status()["state"]!.GetValue<string>());
        Assert.Equal("not_verified", firewall.Status()["reachability"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("bad", "127.0.0.1", 123)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1.2.3.4;evil", 123)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "0.0.0.0", 65536)]
    public void HelperRejectsUnstructuredOrOutOfRangeInput(string owner, string bind, int port) => Assert.Throws<ArgumentException>(() => LanFirewallRequest.Validate(owner, bind, port));
}
