using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.Automation;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI;

namespace JeekMusic.App.Tests;

// Only Headless UI, injected FakeEngine/FakeEndpointVolume and an empty temp file.
// No client entry point, MCP bridge/server Start, decoder or physical device access.
[Collection("Avalonia UI")]
public sealed class TrackNavigationTests
{
    [Theory]
    [InlineData(false, false, RepeatMode.Off)]
    [InlineData(false, true, RepeatMode.One)]
    [InlineData(false, true, RepeatMode.All)]
    [InlineData(true, false, RepeatMode.One)]
    [InlineData(true, true, RepeatMode.Off)]
    [InlineData(true, true, RepeatMode.All)]
    public Task NavigationLoadsOnceAndPlaysWithoutChangingQueueOrMute(bool previous, bool paused, RepeatMode repeat) => Run(f =>
    {
        if (previous) { f.Player.Next(); f.Ready(); }
        f.Player.SetRepeat(repeat);
        f.Endpoint.Muted = true;
        f.Player.SetMuted(paused);
        if (paused) f.Player.Pause();
        Dispatcher.UIThread.RunJobs();
        var queue = f.Player.Queue.Items.ToArray();
        f.Current.Commands.Clear();
        var completed = 0;
        f.Player.TrackCompleted += () => completed++;

        if (previous) f.Player.Previous(); else f.Player.Next();

        var load = Assert.Single(f.Current.Commands, c => c[0] == "loadfile" && c[2] == "replace");
        if (previous)
        {
            Assert.Contains("start=60", load[4]);
            Assert.Contains("end=179.999999", load[4]);
        }
        Assert.Equal("yes", f.Current.Properties["pause"]);
        Assert.Equal("yes", f.Current.Properties["mute"]);
        f.Ready();
        Assert.True(f.Player.IsPlaying);
        Assert.Equal(previous ? 1 : 2, f.Player.CurrentTrack!.Id);
        Assert.Equal("no", f.Current.Properties["pause"]);
        Assert.Equal(paused ? "yes" : "no", f.Current.Properties["mute"]);
        Assert.Equal(queue, f.Player.Queue.Items);
        Assert.Equal(repeat, f.Player.Queue.Repeat);
        Assert.True(f.Player.Queue.Shuffle);
        Assert.Equal(0, completed);
        Assert.Equal(55, f.Endpoint.Volume);
        Assert.True(f.Endpoint.Muted);
        Assert.Empty(f.Endpoint.Writes);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PreviousRestartsCueAndPlaysWhileOrdinarySeekKeepsPause(bool paused) => Run(f =>
    {
        f.Player.SetMuted(true);
        if (paused) f.Player.Pause();
        Dispatcher.UIThread.RunJobs();
        f.Current.Commands.Clear();
        f.Player.Previous();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("60", Assert.Single(f.Current.Commands, c => c[0] == "seek")[1]);
        Assert.DoesNotContain(f.Current.Commands, c => c[0] == "loadfile");
        Assert.True(f.Player.IsPlaying);
        Assert.Equal(0, f.Player.Position.TotalSeconds);
        Assert.Equal(0, f.Player.Queue.CurrentIndex);
        Assert.True(f.Player.Muted);
        Assert.Equal("yes", f.Current.Properties["mute"]);
        f.Player.Pause();
        f.Player.SeekBy(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        Assert.False(f.Player.IsPlaying);
        Assert.Empty(f.Endpoint.Writes);
    });

    [Theory]
    [InlineData(3, 2)]
    [InlineData(3.001, 1)]
    public Task PreviousPreservesTheThreeSecondThreshold(double seconds, long expectedId) => Run(f =>
    {
        f.Current.Properties["time-pos"] = Mpv.Num(60 + seconds);
        f.Player.Pause(); Dispatcher.UIThread.RunJobs();
        f.Player.Previous(); f.Ready();
        Assert.True(f.Player.IsPlaying);
        Assert.Equal(expectedId, f.Player.CurrentTrack!.Id);
    });

    [Theory]
    [InlineData("next")]
    [InlineData("previous")]
    public Task EmptyQueueDoesNotCreateEngine(string action) => Run(f =>
    {
        using var empty = new PlayerService(f, _ => throw new Exception("Must not create engine"), f.Endpoint);
        if (action == "next") empty.Next(); else empty.Previous();
        Assert.Null(empty.CurrentTrack);
        Assert.False(empty.EngineReady);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ManualBoundaryWrapsAndSingleTrackRestartsEvenWithRepeatOff(bool previous) => Run(f =>
    {
        f.Player.SetRepeat(RepeatMode.Off);
        if (!previous) { f.Player.Next(); f.Ready(); }
        // Previous at the first track stays on it; Next at the last wraps.
        f.Current.Properties["time-pos"] = previous ? "60" : "0";
        f.Player.Pause();
        if (previous) f.Player.Previous(); else f.Player.Next();
        f.Ready();
        Assert.Equal(1, f.Player.CurrentTrack!.Id);
        Assert.True(f.Player.IsPlaying);
        f.Player.Restore([new(1, false)], 0, false, RepeatMode.Off, null, TimeSpan.Zero);
        f.Player.Pause();
        if (previous) f.Player.Previous(); else f.Player.Next();
        f.Ready();
        Assert.Equal(1, f.Player.CurrentTrack!.Id);
        Assert.True(f.Player.IsPlaying);
        Assert.Equal(0, f.Player.Position.TotalSeconds);
    });

    [Theory]
    [InlineData("next", "session")]
    [InlineData("previous", "session-muted")]
    [InlineData("restart", "session-unknown")]
    [InlineData("next", "hardware")]
    [InlineData("previous", "hardware-unknown")]
    [InlineData("restart", "hardware")]
    [InlineData("previous", "software-mute")]
    [InlineData("next", "endpoint")]
    [InlineData("previous", "volume")]
    [InlineData("restart", "endpoint")]
    public Task NavigationCannotBypassOutputSafety(string action, string reason) => Run(f =>
    {
        f.Player.SetExclusive(true);
        f.Ready();
        f.Player.Pause();
        Dispatcher.UIThread.RunJobs();
        if (action == "previous") f.Current.Properties["time-pos"] = "60";
        switch (reason)
        {
            case "session": f.Endpoint.SessionVolume = 20; break;
            case "session-muted": f.Endpoint.SessionMuted = true; break;
            case "session-unknown": f.Endpoint.SessionVolume = null; break;
            case "hardware": f.Endpoint.HardwareVolume = false; break;
            case "hardware-unknown": f.Endpoint.HardwareVolume = null; break;
            case "software-mute": f.Endpoint.HardwareMute = false; f.Endpoint.Muted = true; break;
            case "endpoint": f.Endpoint.FailCapture = true; break;
            case "volume": f.Endpoint.FailVolume = true; break;
        }
        f.Current.Operations.Clear();
        if (action == "next") f.Player.Next(); else f.Player.Previous();
        f.Ready();
        Assert.False(f.Player.IsPlaying);
        Assert.True(f.Player.OutputPending);
        Assert.NotNull(f.Player.OutputBlockReason);
        Assert.Equal("yes", f.Current.Properties["pause"]);
        Assert.Equal("yes", f.Current.Properties["mute"]);
        Assert.DoesNotContain("set pause=no", f.Current.Operations);
        Assert.Empty(f.Endpoint.Writes);
        if (reason is "hardware" or "software-mute")
        {
            Assert.True(f.Player.HardwareVolumeConfirmationRequired);
            // A second navigation after the warning still cannot acknowledge it.
            f.Player.Next(); f.Ready();
            f.Player.Previous(); f.Ready();
            Assert.True(f.Player.HardwareVolumeConfirmationRequired);
            Assert.False(f.Player.IsPlaying);
            f.Player.Resume();
            Dispatcher.UIThread.RunJobs();
            Assert.False(f.Player.HardwareVolumeConfirmationRequired);
            Assert.True(f.Player.IsPlaying);
        }
    });

    [Theory]
    [InlineData("seek")]
    [InlineData("unseekable")]
    [InlineData("load")]
    [InlineData("decode")]
    [InlineData("missing-id")]
    [InlineData("missing-path")]
    public Task FailedNavigationNeverResumesTheOldTrack(string failure) => Run(f =>
    {
        f.Player.Pause();
        f.Player.SetRepeat(RepeatMode.Off);
        Dispatcher.UIThread.RunJobs();
        f.Current.Operations.Clear();
        switch (failure)
        {
            case "seek": f.Current.RejectSeek = true; f.Player.Previous(); break;
            case "unseekable": f.Current.Properties["seekable"] = "no"; f.Player.Previous(); break;
            case "load": f.Current.RejectLoad = true; f.Player.Next(); f.Ready(); break;
            case "decode": f.Player.Next(); f.Current.EndCurrent(Mpv.EndReason.Error); Dispatcher.UIThread.RunJobs(); break;
            case "missing-id":
                f.Player.Queue.PlayNext([999]);
                f.Player.Next();
                f.Ready();
                Assert.Null(f.Player.CurrentTrack);
                break;
            case "missing-path":
                f.MissingPaths.Add(2);
                f.Player.Next();
                Dispatcher.UIThread.RunJobs();
                Assert.True(f.Player.IsIdle);
                break;
        }
        Assert.False(f.Player.IsPlaying);
        Assert.DoesNotContain("set pause=no", f.Current.Operations);
        Assert.Empty(f.Endpoint.Writes);
    });

    [Fact]
    public Task MissingTargetStopsPlayingStateImmediately() => Run(f =>
    {
        f.Player.Queue.PlayNext([999]);
        f.Player.Next();
        Assert.Null(f.Player.CurrentTrack);
        Assert.True(f.Player.IsIdle);
        Assert.False(f.Player.IsPlaying);
    });

    [Fact]
    public Task NaturalQueueEndStillReloadsPaused() => Run(f =>
    {
        f.Player.SetRepeat(RepeatMode.Off);
        f.Player.Next(); f.Ready();
        f.Current.EndCurrent(Mpv.EndReason.Eof);
        Dispatcher.UIThread.RunJobs();
        f.Ready();
        Assert.Equal(2, f.Player.CurrentTrack!.Id);
        Assert.False(f.Player.IsPlaying);
        Assert.Equal("yes", f.Current.Properties["pause"]);
    });

    [Fact]
    public Task RapidNavigationIgnoresAnEarlierSongsQueuedFileLoaded() => Run(f =>
    {
        f.Player.Pause(); Dispatcher.UIThread.RunJobs();
        f.Player.Next();
        f.Current.Emit(Mpv.EventId.FileLoaded);
        f.Player.Next();
        f.Current.Operations.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("set pause=no", f.Current.Operations);
        Assert.True(f.Player.OutputPending);
        f.Ready();
        Assert.True(f.Player.IsPlaying);
        Assert.Equal(1, f.Player.CurrentTrack!.Id);
    });

    [Theory]
    [InlineData("main-command")]
    [InlineData("main-key")]
    [InlineData("mini-command")]
    [InlineData("mini-key")]
    [InlineData("mcp")]
    public Task WindowAndMcpEntrypointsPlayAfterPause(string entry) => Run(f =>
    {
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        using var app = new AppServices(settings, new LibraryDb(":memory:"), _ => f.Player);
        var vm = new JeekMusic.UI.ViewModels.PlayerViewModel(f.Player, app.SleepTimer, settings) { ProgressVisible = false };
        Window? window = entry.StartsWith("main") ? new MainWindow(app) { ExitRequested = true } :
            entry.StartsWith("mini") ? new MiniPlayerWindow(vm, false) : null;
        try
        {
            window?.Show(); // Avalonia Headless; never starts the client or Shell integrations.
            foreach (var previous in new[] { false, true })
            {
                f.Player.Pause(); Dispatcher.UIThread.RunJobs();
                if (entry == "mcp") new McpTools(app).Call("playback", new() { ["action"] = previous ? "previous" : "next" });
                else if (entry.EndsWith("key"))
                {
                    var key = previous ? Key.Z : Key.X;
                    window!.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
                    window!.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
                }
                else
                {
                    var playerVm = window is MainWindow main ? main.ViewModel.Player : vm;
                    var command = previous ? playerVm.PreviousCommand : playerVm.NextCommand;
                    var button = window!.GetVisualDescendants().OfType<Button>().Single(b =>
                        Equals(b.Command, command));
                    button.Command!.Execute(null);
                }
                f.Ready();
                Assert.True(f.Player.IsPlaying);
                Assert.Equal(previous ? 1 : 2, f.Player.CurrentTrack!.Id);
            }
            Assert.Empty(f.Endpoint.Writes);
        }
        finally
        {
            if (window is MainWindow main) main.ViewModel.Player.ProgressVisible = false;
            vm.ProgressVisible = false;
            window?.Close();
        }
    });

    [Fact]
    public Task McpNavigationReportsTheGateWithoutConfirmingHardwareVolume() => Run(f =>
    {
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        using var app = new AppServices(settings, new LibraryDb(":memory:"), _ => f.Player);
        var tools = new McpTools(app);
        f.Endpoint.HardwareVolume = false;
        f.Player.SetExclusive(true); f.Ready();
        foreach (var action in new[] { "next", "previous" })
        {
            tools.Call("playback", new() { ["action"] = action });
            f.Ready();
            var output = tools.Call("get_status", new())["output"]!;
            Assert.True(output["hardwareVolumeConfirmationRequired"]!.GetValue<bool>());
            Assert.Equal("hardware-volume-confirmation", output["blockReason"]!.GetValue<string>());
            Assert.False(f.Player.IsPlaying);
            Assert.Equal("yes", f.Current.Properties["mute"]);
        }
        Assert.Empty(f.Endpoint.Writes);
    });

    [Fact]
    public void McpCatalogDescribesNavigationAndKeepsItsCompatibleActions()
    {
        var tool = McpTools.BuildCatalog().OfType<JsonObject>().Single(t => t["name"]!.GetValue<string>() == "playback");
        Assert.Contains("start playback even when paused", tool["description"]!.GetValue<string>());
        var action = tool["inputSchema"]!["properties"]!["action"]!;
        Assert.Equal(new[] { "resume", "pause", "toggle", "next", "previous", "seek" }, action["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("hardware-volume confirmation", action["description"]!.GetValue<string>());
    }

    static async Task Run(Action<OutputModeSwitchTests.Fixture> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(() =>
        {
            using var f = new OutputModeSwitchTests.Fixture();
            test(f);
        }, CancellationToken.None);
    }
}
