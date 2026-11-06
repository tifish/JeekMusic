using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Threading;
using JeekMusic.Automation;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class LanLifecycleTests
{
    sealed class DelayedFirewall : ILanFirewallBackend
    {
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Allowed = true;
        internal bool Removed;
        internal TaskCompletionSource? RemovalRelease;
        internal bool Closed;
        public async Task<JsonObject> Query(LanFirewallRequest request)
        {
            Entered.TrySetResult();
            await Release.Task;
            return new() { ["satisfied"] = Allowed, ["state"] = Allowed ? "configured" : "repair_required" };
        }
        public Task<int> Apply(LanFirewallRequest request) => Task.FromResult(5);
        public async Task<JsonObject> Remove(LanFirewallRequest request)
        {
            Removed = true;
            if (RemovalRelease is not null) await RemovalRelease.Task;
            return new JsonObject { ["satisfied"] = true, ["state"] = "not_required" };
        }
        public Task CloseAsync() { Closed = true; return Task.CompletedTask; }
    }

    [Fact]
    public async Task ClientExitYieldsDispatcherUntilVerifiedCleanupAndHelperCloseFinish()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "jeek-exit-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            var backend = new DelayedFirewall { RemovalRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            backend.Release.TrySetResult();
            var firewall = new LanFirewall(backend, new string('a', 64), @"C:\fixture\JeekMusic.exe", () => { });
            var app = new AppServices(new SettingsStore(Path.Combine(root, "settings.json")), new LibraryDb(":memory:"),
                source => new PlayerService(source, o => new OutputModeSwitchTests.FakeEngine(o), new OutputModeSwitchTests.FakeEndpointVolume()),
                owner => new LanSyncService(owner, firewall, Path.Combine(root, "lan.json"), false));
            using var rsa = RSA.Create(2048);
            var cert = new CertificateRequest("CN=Async exit fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            typeof(LanSyncService).GetField("_certificate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app.LanSync, cert);
            app.LanSync.Configure("0.0.0.0", 39843);
            await app.LanSync.StartAsync(0);
            var stop = app.DisposeAsync().AsTask();
            try
            {
                Assert.False(stop.IsCompleted);
                Assert.False(backend.Closed);
                bool dispatcherRan = false;
                await Dispatcher.UIThread.InvokeAsync(() => dispatcherRan = true, DispatcherPriority.Background);
                Assert.True(dispatcherRan);
            }
            finally { backend.RemovalRelease.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert.True(backend.Removed);
            Assert.True(backend.Closed);
            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("stop")]
    [InlineData("dispose")]
    [InlineData("deny")]
    [InlineData("restart")]
    public async Task FirewallWaitKeepsDispatcherAndDiagnosticsAvailableAndHonorsCancellation(string action)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "jeek-lifecycle-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            using var app = new AppServices(new SettingsStore(Path.Combine(root, "settings.json")), new LibraryDb(":memory:"),
                source => new PlayerService(source, o => new OutputModeSwitchTests.FakeEngine(o), new OutputModeSwitchTests.FakeEndpointVolume()));
            var backend = new DelayedFirewall { Allowed = action != "deny" };
            var firewall = new LanFirewall(backend, new string('a', 64), @"C:\fixture\JeekMusic.exe", () => { });
            using var service = new LanSyncService(app, firewall, Path.Combine(root, "lan.json"), false);
            using var rsa = RSA.Create(2048);
            var cert = new CertificateRequest("CN=Lifecycle fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            typeof(LanSyncService).GetField("_certificate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, cert);
            service.Configure("0.0.0.0", 39843);
            var start = service.StartAsync(0);
            await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(start.IsCompleted);
            Assert.True(service.Diagnostics()["starting"]!.GetValue<bool>());
            Assert.Equal(0, service.Port); // No listener before exact-rule verification.
            var diagnostics = await new McpTools(app).CallAsync("get_diagnostics", new());
            Assert.Equal("milliseconds", diagnostics["lifecycle"]!["units"]!.GetValue<string>());
            if (action == "stop") service.Stop();
            if (action == "dispose") _ = service.StopForExit();
            Task? restart = null;
            if (action == "restart") { service.Stop(); restart = service.StartAsync(0); }
            backend.Release.TrySetResult();
            if (action == "deny") await Assert.ThrowsAsync<IOException>(() => start);
            else await start.WaitAsync(TimeSpan.FromSeconds(5));
            if (restart is not null) await restart.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(service.Starting);
            if (action is "complete" or "restart") Assert.True(service.Port > 0);
            else Assert.Equal(0, service.Port);
            await service.StopForExit().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(backend.Removed);
            return 0;
        }, CancellationToken.None);
    }
}
