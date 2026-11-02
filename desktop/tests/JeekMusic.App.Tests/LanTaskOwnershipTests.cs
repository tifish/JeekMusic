using System.Reflection;
using Avalonia.Headless;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class LanTaskOwnershipTests
{
    [Fact]
    public void DefaultDirectoryUsesTheSameInstanceRegardlessOfExplicitEnvironment()
    {
        const string path = @"C:\Users\fixture\AppData\Local\JeekMusic";
        Assert.Equal("", JeekMusic.Platform.SingleInstance.DataSuffix(path, path));
        Assert.Equal("", JeekMusic.Platform.SingleInstance.DataSuffix(path.ToLowerInvariant()+"/", path));
        Assert.NotEqual("", JeekMusic.Platform.SingleInstance.DataSuffix(path+"-isolated",path));
        Assert.Equal(JeekMusic.Platform.SingleInstance.DataSuffix(path+"-isolated",path),JeekMusic.Platform.SingleInstance.DataSuffix(path+"-isolated/",path));
    }
    [Fact]
    public async Task CheckpointPersistsWindowBeforeQueueAndSettings()
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(async()=> {
            using var app = new AppServices(new SettingsStore(Path.Combine(Path.GetTempPath(), "jeek-checkpoint-" + Guid.NewGuid(), "settings.json")), new LibraryDb(":memory:"),
                source => new PlayerService(source, o => new OutputModeSwitchTests.FakeEngine(o), new OutputModeSwitchTests.FakeEndpointVolume()));
            bool captured=false; app.CheckpointWindow=()=>captured=true;
            var result=await new JeekMusic.Automation.McpTools(app).CallAsync("checkpoint_client",new());
            Assert.True(captured); Assert.True(result["saved"]!.GetValue<bool>()); return 0;
        },CancellationToken.None);
    }
    [Fact]
    public async Task RevocationCancelsOnlyTheOwningRemoteWorkerToken()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(async () => {
            using var app = new AppServices(new SettingsStore(Path.Combine(Path.GetTempPath(), "jeek-owned-" + Guid.NewGuid(), "settings.json")), new LibraryDb(":memory:"),
                source => new PlayerService(source, o => new OutputModeSwitchTests.FakeEngine(o), new OutputModeSwitchTests.FakeEndpointVolume()));
            var training = app.Preferences.Training;
            void Set(string key, object? value) => training.GetType().GetField(key, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(training, value);
            // Controlled worker fixture waits on the very token consumed by the production worker.
            // It does not download model weights or claim to exercise CPU/model accuracy.
            foreach (var owner in new string?[] { null, "lan:device-one" })
            {
                using var cancellation = new CancellationTokenSource();
                Set("cancel", cancellation); Set("remoteOwner", owner); Set("learning", true);
                var worker = Task.Run(async () => { try { await Task.Delay(Timeout.Infinite, cancellation.Token); } catch (OperationCanceledException) { } });
                Set("jobTask", worker);
                app.LanSync.Revoke("device-two"); Assert.False(cancellation.IsCancellationRequested);
                app.LanSync.Revoke("device-one"); Assert.Equal(owner is not null, cancellation.IsCancellationRequested);
                if (owner is null) { Assert.False(worker.IsCompleted); cancellation.Cancel(); }
                await worker.WaitAsync(TimeSpan.FromSeconds(2));
                Set("cancel", null); Set("remoteOwner", null); Set("learning", false); Set("jobTask", null);
            }
            return 0;
        }, CancellationToken.None);
    }
}
