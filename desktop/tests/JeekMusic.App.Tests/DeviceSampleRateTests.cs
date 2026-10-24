using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using JeekMusic.Automation;
using JeekMusic.Library;
using JeekMusic.Platform;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI;
using Xunit.Abstractions;


namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class DeviceSampleRateTests(ITestOutputHelper output)
{
    sealed class Formats : IDeviceAudioFormat, IExclusiveAudioFormat
    {
        public int MixRate = 48000;
        public bool RejectWrite, SharedAvailable = true;
        public List<(string, int)> Writes { get; } = [];
        public Action? BeforeWrite;
        public DeviceAudioCapabilities Capture(string? device, int? sourceRate, int channels) =>
            new(device?.Replace("wasapi/", "") ?? "render-device", MixRate, 2,
                SharedAvailable ? [44100, 48000, 96000] : [], [44100, 48000, 96000], SharedAvailable,
                SharedAvailable ? null : "IPolicyConfig unavailable");
        public bool SetSharedRate(string device, int rate, out string? diagnostic)
        {
            BeforeWrite?.Invoke();
            Writes.Add((device, rate));
            diagnostic = RejectWrite ? "SetDeviceFormat: 0x80004001" : null;
            if (!RejectWrite) MixRate = rate;
            return !RejectWrite;
        }
        public int? ReadMixRate(string device) => MixRate;
        public string? Select(string? device, int sampleRate, int channels, int? sourceBits) => "s32";
    }

    static async Task Run(Action<OutputModeSwitchTests.Fixture, Formats> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(() =>
        {
            var formats = new Formats();
            using var f = new OutputModeSwitchTests.Fixture(formats: formats, deviceFormats: formats);
            test(f, formats);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SharedManualClosesStreamBeforeSystemWriteAndPreservesCuePositionAndPause(bool paused) => Run((f, format) =>
    {
        if (paused) { f.Player.Pause(); Dispatcher.UIThread.RunJobs(); }
        var old = f.Current;
        format.BeforeWrite = () => Assert.True(old.Disposed);
        Assert.True(f.Player.SetSampleRate("manual", 96000));
        Assert.Equal(("render-device", 96000), Assert.Single(format.Writes));
        Assert.Equal(12.345, f.Player.Position.TotalSeconds, 3);
        Assert.DoesNotContain(f.Current.Commands.Where(c => c[0] == "loadfile"), c => c[4].Contains("audio-samplerate"));
        Assert.Equal(96000, f.Player.ActualSampleRate);
        Assert.False(f.Player.SampleRateApplied); // initialized but not acknowledged by file-loaded
        f.Ready();
        Assert.True(f.Player.SampleRateApplied);
        Assert.Equal(!paused, f.Player.IsPlaying);
        Assert.Equal(12.345, f.Player.Position.TotalSeconds, 3);
        Assert.Empty(f.Endpoint.Writes);
    });

    [Fact]
    public Task SharedAutoDoesNotPrefetchOrChangeMixForNextSongUntilEof() => Run((f, format) =>
    {
        Assert.True(f.Player.SetSampleRate("auto"));
        f.Ready();
        Assert.Equal(44100, format.MixRate);
        Assert.Single(f.Current.Commands, c => c[0] == "loadfile");
        var completed = 0;
        f.Player.TrackCompleted += () => completed++;
        f.Current.EndCurrent(Mpv.EndReason.Eof);
        Dispatcher.UIThread.RunJobs();
        f.Ready();
        Assert.Equal(2, f.Player.CurrentTrack!.Id);
        Assert.Equal(new[] { 44100, 96000 }, format.Writes.Select(w => w.Item2));
        Assert.Single(f.Current.Commands, c => c[0] == "loadfile");
        Assert.Equal(1, completed);
        Assert.True(f.Player.IsPlaying);
        Assert.True(f.Player.SampleRateApplied);
    });

    [Fact]
    public Task FailedSharedWriteKeepsActualRateUsablePlaybackAndRollsBackManualSelection() => Run((f, format) =>
    {
        format.RejectWrite = true;
        Assert.False(f.Player.SetSampleRate("manual", 96000));
        f.Ready();
        Assert.Equal("system", f.Player.SampleRateMode);
        Assert.Equal(48000, f.Player.ActualSampleRate);
        Assert.True(f.Player.IsPlaying);
        Assert.Contains("0x80004001", f.Player.SampleRateDiagnostic);
        Assert.False(f.Player.SampleRateApplied);
        Assert.Single(format.Writes);
        Assert.NotEmpty(f.Errors);
    });

    [Fact]
    public Task UnsupportedRatesAndPolicyUnavailableNeverWriteOrReplaceEngine() => Run((f, format) =>
    {
        var old = f.Current;
        Assert.False(f.Player.SetSampleRate("manual", 12345));
        format.SharedAvailable = false;
        Assert.False(f.Player.SetSampleRate("auto"));
        Assert.Equal("system", f.Player.SampleRateMode);
        Assert.Same(old, f.Current);
        Assert.Empty(format.Writes);
        Assert.Contains("IPolicyConfig", f.Player.SampleRateDiagnostic);
    });

    [Fact]
    public Task AutoFailureKeepsPolicyAndCanRecoverOnTheNextTrack() => Run((f, format) =>
    {
        format.RejectWrite = true;
        Assert.False(f.Player.SetSampleRate("auto"));
        f.Ready();
        Assert.Equal("auto", f.Player.SampleRateMode);
        Assert.Equal(48000, f.Player.ActualSampleRate);
        Assert.False(f.Player.SampleRateApplied);
        Assert.True(f.Player.IsPlaying);
        format.RejectWrite = false;
        f.Player.Next(); f.Ready();
        Assert.Equal(96000, f.Player.ActualSampleRate);
        Assert.True(f.Player.SampleRateApplied);
        Assert.Null(f.Player.SampleRateDiagnostic);
    });

    [Fact]
    public Task ExclusiveManualUsesFileOptionsForBothTracksWithoutWritingSystemMix() => Run((f, format) =>
    {
        f.Player.SetExclusive(true); f.Ready();
        Assert.True(f.Player.SetSampleRate("manual", 48000));
        f.Current.Properties["audio-out-params/samplerate"] = "48000";
        f.Ready();
        Assert.All(f.Current.Commands.Where(c => c[0] == "loadfile"), c => Assert.Contains("audio-samplerate=48000", c[4]));
        Assert.Empty(format.Writes);
        Assert.Equal(48000, f.Player.ActualSampleRate);
        Assert.True(f.Player.SampleRateApplied);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExclusiveRateMismatchOrInitializationFailureRestoresPreviousChoiceAndCue(bool initializationFails) => Run((f, format) =>
    {
        f.Player.SetExclusive(true); f.Ready();
        Assert.True(f.Player.SetSampleRate("manual", 96000));
        if (initializationFails) { f.Current.EndCurrent(Mpv.EndReason.Error); Dispatcher.UIThread.RunJobs(); }
        else { f.Current.Properties["audio-out-params/samplerate"] = "48000"; f.Ready(); }
        Assert.Equal("system", f.Player.SampleRateMode);
        Assert.Equal(12.345, f.Player.Position.TotalSeconds, 3);
        f.Ready();
        Assert.True(f.Player.IsPlaying);
        Assert.NotNull(f.Player.SampleRateDiagnostic);
        Assert.Empty(format.Writes);
    });

    [Fact]
    public Task ExternalSystemChangeReportsRealMixAndDoesNotFightTheSystem() => Run((f, format) =>
    {
        Assert.True(f.Player.SetSampleRate("manual", 44100)); f.Ready();
        format.MixRate = 48000;
        f.Player.RefreshOutput(forceRateRefresh: true);
        Assert.Equal(48000, f.Player.ActualSampleRate);
        Assert.False(f.Player.SampleRateApplied);
        Assert.Single(format.Writes);
        Assert.Contains("externally", f.Player.SampleRateDiagnostic);
        var state = McpTools.SampleRateState(f.Player);
        Assert.Equal(48000, state["actualRate"]!.GetValue<int>());
        Assert.Equal(44100, state["requestedRate"]!.GetValue<int>());
        Assert.False(state["applied"]!.GetValue<bool>());
    });

    [Fact]
    public Task DeviceChangeReappliesManualRateToTheNewBoundEndpoint() => Run((f, format) =>
    {
        Assert.True(f.Player.SetSampleRate("manual", 96000)); f.Ready();
        format.MixRate = 48000;
        f.Endpoint.Volumes["second-device"] = 70;
        f.Player.SetDevice("wasapi/second-device"); f.Ready();
        Assert.Equal(("second-device", 96000), format.Writes[^1]);
        Assert.Equal(12.345, f.Player.Position.TotalSeconds, 3);
        Assert.True(f.Player.IsPlaying);
        Assert.Empty(f.Endpoint.Writes);
    });

    [Fact]
    public Task McpAndSettingsUseBusinessStateAndRejectInconsistentArguments() => Run((f, format) =>
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var settings = new SettingsStore(path);
        OutputSettingsBinding.Attach(f.Player, settings);
        using var app = new AppServices(settings, new LibraryDb(":memory:"), _ => f.Player);
        var mcp = new McpTools(app);
        Assert.Throws<ArgumentException>(() => mcp.Call("set_output", new() { ["sampleRateMode"] = "auto", ["sampleRate"] = 44100 }));
        Assert.Throws<ArgumentException>(() => mcp.Call("set_output", new() { ["sampleRateMode"] = "manual" }));
        var state = mcp.Call("set_output", new() { ["sampleRate"] = 96000 });
        f.Ready();
        Assert.Equal(96000, state["sampleRate"]!["actualRate"]!.GetValue<int>());
        Assert.Equal("manual", settings.Current.SampleRateMode);
        Assert.Equal(96000, settings.Current.ManualSampleRate);
        Assert.Throws<InvalidOperationException>(() => mcp.Call("set_output", new() { ["sampleRate"] = 12345 }));
        Assert.Equal(96000, settings.Current.ManualSampleRate);
        settings.WriteNow();
        Assert.Equal("manual", new SettingsStore(path).Current.SampleRateMode);
        File.Delete(path);
    });

    [Fact]
    public Task PlaybackMenuUsesProbedRatesAndSelectionReflectsAppliedOutput() => Run((f, format) =>
    {
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        using var app = new AppServices(settings, new LibraryDb(":memory:"), _ => f.Player);
        var window = new MainWindow(app) { ExitRequested = true };
        window.ViewModel.Player.ProgressVisible = false;
        try
        {
            var menu = window.CreateOutputMenu();
            var groups = menu.Items.OfType<MenuItem>().Where(i => i.Items.Count > 0).ToArray();
            Assert.Equal(2, groups.Length);
            Assert.Equal(5, groups[0].Items.Count); // system, auto, three real driver-probed rates
            var manual = groups[0].Items.OfType<MenuItem>().Single(i => Equals(i.Header, "96 kHz"));
            manual.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            f.Ready();
            Assert.True(f.Player.SampleRateApplied);
            Assert.Contains(window.CreateOutputMenu().Items.OfType<MenuItem>().First(i => i.Items.Count > 0).Items.OfType<MenuItem>(),
                i => Equals(i.Header, "96 kHz") && i.IsChecked);
            format.MixRate = 48000;
            f.Player.RefreshOutput(forceRateRefresh: true);
            Assert.DoesNotContain(window.CreateOutputMenu().Items.OfType<MenuItem>().First(i => i.Items.Count > 0).Items.OfType<MenuItem>(),
                i => Equals(i.Header, "96 kHz") && i.IsChecked);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LegacyAndInvalidSampleRatePreferencesKeepThePreviousSystemBehavior()
    {
        Assert.Equal("system", new AppSettings().SampleRateMode);
        using var player = new PlayerService(new EmptySource(), _ => throw new Exception("No engine expected"));
        player.RestoreSampleRateSettings("unknown", -1);
        Assert.Equal("system", player.SampleRateMode);
        player.RestoreSampleRateSettings("manual", null);
        Assert.Equal("system", player.SampleRateMode);
    }

    sealed class EmptySource : ITrackSource
    {
        public Track? ById(long id) => null;
        public Track? ByKey(string key) => null;
        public string? PathOf(Track track) => null;
    }

    [RealAudioFact]
    public void RealWindowsEndpointSharedFormatWriteIsReadBackAndOriginalRateIsRestored()
    {
        var service = new DeviceAudioFormat();
        var caps = service.Capture(null, 44100, 2);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(caps));
        Assert.NotNull(caps.DeviceId);
        Assert.NotNull(caps.MixRate);
        Assert.True(caps.CanSetShared, caps.Diagnostic);
        var original = service.CaptureSystemFormat(caps.DeviceId!);
        var candidates = caps.SharedRates.Where(r => r != caps.MixRate).ToArray();
        Assert.NotEmpty(candidates);
        var target = candidates.Contains(44100) ? 44100 : candidates[0];
        try
        {
            var success = service.SetSharedRate(caps.DeviceId!, target, out var diagnostic);
            output.WriteLine($"Set {target} Hz: success={success}; diagnostic={diagnostic}; readback={service.ReadMixRate(caps.DeviceId!)}");
            Assert.True(success, diagnostic);
            Assert.Equal(target, service.ReadMixRate(caps.DeviceId!));
        }
        finally
        {
            service.RestoreSystemFormat(caps.DeviceId!, original);
            output.WriteLine($"Restore original endpoint/mix format: readback={service.ReadMixRate(caps.DeviceId!)} Hz");
            Assert.Equal(caps.MixRate, service.ReadMixRate(caps.DeviceId!));
            Assert.Equal(original.Endpoint, service.CaptureSystemFormat(caps.DeviceId!).Endpoint);
            Assert.Equal(original.Mix, service.CaptureSystemFormat(caps.DeviceId!).Mix);
        }
    }
}

public sealed class RealAudioFactAttribute : FactAttribute
{
    public RealAudioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("JEEKMUSIC_TEST_REAL_AUDIO") != "1")
            Skip = "Opt-in Windows endpoint format mutation: set JEEKMUSIC_TEST_REAL_AUDIO=1.";
    }
}

