using System.Globalization;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Threading;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class OutputModeSwitchTests
{
    [Fact]
    public async Task ExclusiveLoadsAndPreloadsEachTrackAtItsOwnNativeRateAndClearsTheOptionsInSharedMode()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(() =>
        {
            var selector = new FakeExclusiveFormat();
            using var f = new Fixture(formats: selector);
            Assert.Empty(selector.Queries);
            f.Player.SetExclusive(true);
            var loads = f.Current.Commands.Where(c => c[0] == "loadfile").ToArray();
            Assert.Contains("audio-format=s16", loads[0][4]);
            Assert.Contains("audio-samplerate=44100", loads[0][4]);
            var start = loads[0][4].Split(',').Single(o => o.StartsWith("start="))[6..];
            Assert.Equal(72.345, double.Parse(start, CultureInfo.InvariantCulture), 6);
            Assert.Contains("end=179.999999", loads[0][4]);
            Assert.Contains("audio-format=s32", loads[1][4]);
            Assert.Contains("audio-samplerate=96000", loads[1][4]);
            Assert.Equal(new[] { 44100, 96000 }, selector.Queries);
            f.Ready();
            f.Player.SetExclusive(false);
            Assert.All(f.Current.Commands.Where(c => c[0] == "loadfile"), c => Assert.DoesNotContain("audio-format", c[4]));
            Assert.All(f.Current.Commands.Where(c => c[0] == "loadfile"), c => Assert.DoesNotContain("audio-samplerate", c[4]));
            Assert.Equal(2, selector.Queries.Count);
            f.Ready();
            Assert.Empty(f.Errors);
        }, CancellationToken.None);
    }

    sealed class FakeExclusiveFormat : IExclusiveAudioFormat
    {
        public List<int> Queries { get; } = [];
        public string? Select(string? device, int sampleRate, int channels, int? sourceBits)
        {
            Queries.Add(sampleRate);
            Assert.Equal(2, channels);
            return sampleRate == 44100 ? "s16" : "s32";
        }
    }

    [Fact]
    public Task ViewModelExternalRefreshAndMuteDoNotWriteEndpointVolume() => Run(f =>
    {
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        var vm = new JeekMusic.UI.ViewModels.PlayerViewModel(f.Player, new SleepTimer(f.Player), settings);
        f.Endpoint.Volume = 19;
        f.Player.RefreshOutput();
        Assert.Equal(19, vm.Volume);
        vm.Muted = true;
        Assert.True(f.Player.Muted);
        Assert.Empty(f.Endpoint.Writes);
        vm.Volume = 15;
        Assert.Equal(15, f.Endpoint.Volume);
        Assert.Single(f.Endpoint.Writes);
    });

    [Fact]
    public Task McpReportsUnknownReadingsSeparatelyFromUnsupportedHardware() => Run(f =>
    {
        f.Endpoint.HardwareVolume = false;
        f.Player.SetExclusive(true);
        f.Ready();
        var output = JeekMusic.Automation.McpTools.VolumeState(f.Player);
        Assert.Equal("endpoint-master", output["semantics"]!.GetValue<string>());
        Assert.Equal("player", output["muteScope"]!.GetValue<string>());
        Assert.False(output["volumeAvailable"]!.GetValue<bool>());
        Assert.False(output["hardwareVolume"]!.GetValue<bool>());
        Assert.Equal(55, output["endpointVolume"]!.GetValue<double>());
        Assert.True(output["hardwareVolumeConfirmationRequired"]!.GetValue<bool>());
        f.Endpoint.FailCapture = true;
        f.Player.RefreshOutput();
        output = JeekMusic.Automation.McpTools.VolumeState(f.Player);
        Assert.Null(output["endpointVolume"]);
        Assert.Null(output["hardwareVolume"]);
        Assert.NotNull(output["diagnostic"]);
    });

    [Fact]
    public Task IdleEngineStillAllowsEndpointControlsAndInitializesNoSavedVolume() => Run(f =>
    {
        using var player = new PlayerService(f, options => new FakeEngine(options), f.Endpoint);
        player.EnsureEngine();
        Assert.False(player.OutputPending);
        Assert.True(player.VolumeAvailable);
        Assert.True(player.SetVolume(13, false));
        Assert.Equal(13, f.Endpoint.Volume);
    });

    [Fact]
    public Task ModesAndExitNeverRestoreHistoricalVolume() => Run(f =>
    {
        Assert.Equal(55, f.Player.Volume);
        f.Player.SetVolume(18, false);
        f.Player.SetExclusive(true);
        f.Ready();
        Assert.Equal(18, f.Player.Volume);
        f.Endpoint.Volume = 31;
        f.Player.RefreshOutput();
        Assert.Equal(31, f.Player.Volume);
        f.Player.SetExclusive(false);
        f.Ready();
        Assert.Equal(31, f.Player.Volume);
        f.Player.Dispose();
        Assert.Equal(31, f.Endpoint.Volume);
        Assert.Single(f.Endpoint.Writes);
        Assert.All(f.Engines, e => Assert.DoesNotContain(e.Operations, op => op.StartsWith("set ao-")));
    });

    [Fact]
    public Task ExplicitAndDefaultDeviceChangesPinTheNewDeviceWithoutWritingVolume() => Run(f =>
    {
        f.Endpoint.Volumes["second-device"] = 72;
        f.Player.SetDevice("wasapi/second-device");
        f.Ready();
        Assert.Equal("wasapi/second-device", f.Current.Properties["audio-device"]);
        Assert.Equal(72, f.Player.Volume);
        f.Player.SetDevice(null);
        f.Ready();
        Assert.Equal(55, f.Player.Volume);
        f.Endpoint.DeviceId = "second-device";
        f.Player.RefreshOutput();
        Assert.True(f.Current.Properties["mute"] == "yes");
        f.Ready();
        Assert.Equal("second-device", f.Player.ActualDeviceId);
        Assert.Equal(72, f.Player.Volume);
        Assert.Empty(f.Endpoint.Writes);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExternalEndpointChangesNeverFeedBackAndMuteIsLocal(bool exclusive) => Run(f =>
    {
        f.Player.SetExclusive(exclusive);
        f.Ready();
        f.Endpoint.Volume = 12;
        f.Endpoint.Muted = true;
        f.Player.RefreshOutput();
        Assert.Equal(12, f.Player.Volume);
        Assert.True(f.Player.EndpointMuted);
        Assert.False(f.Player.Muted);
        f.Player.SetMuted(true);
        Assert.Equal("yes", f.Current.Properties["mute"]);
        f.Player.SetMuted(false);
        Assert.True(f.Endpoint.Muted);
        Assert.Empty(f.Endpoint.Writes);
        Assert.DoesNotContain(f.Current.Operations, op => op.StartsWith("set ao-"));
    });

    [Theory]
    [InlineData(10.0, false)]
    [InlineData(100.0, true)]
    [InlineData(null, false)]
    public Task MixerAttenuationMuteAndReadFailureBlockExclusiveWithoutOverridingUser(double? volume, bool muted) => Run(f =>
    {
        f.Endpoint.SessionVolume = volume;
        f.Endpoint.SessionMuted = muted;
        f.Player.SetExclusive(true);
        Assert.False(f.Player.Exclusive);
        Assert.Single(f.Engines);
        Assert.Equal("session-attenuated-or-unreadable", f.Player.OutputBlockReason);
        Assert.Equal(volume, f.Endpoint.SessionVolume);
        Assert.Equal(muted, f.Endpoint.SessionMuted);
        f.Endpoint.SessionVolume = 100;
        f.Endpoint.SessionMuted = false;
        f.Player.SetExclusive(true);
        f.Ready();
        Assert.True(f.Player.IsPlaying);
    });

    [Fact]
    public Task ExclusiveStartupAlsoChecksPersistedMixerAndCannotBypassItWithResume() => Run(f =>
    {
        FakeEngine? engine = null;
        using var player = new PlayerService(f, options => engine = new FakeEngine(options), f.Endpoint);
        player.RestoreVolumeSettings(true, false);
        f.Endpoint.SessionVolume = 8;
        player.Restore([new(1, false)], 0, false, RepeatMode.Off, null, TimeSpan.Zero);
        player.PrepareRestored();
        engine!.Emit(Mpv.EventId.FileLoaded);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("session-attenuated-or-unreadable", player.OutputBlockReason);
        player.Resume();
        Assert.False(player.IsPlaying);
        Assert.Empty(f.Endpoint.Writes);
    });

    [Fact]
    public Task UnsupportedHardwareIsDistinctFromReadFailureAndCanBeExplicitlyResumed() => Run(f =>
    {
        f.Endpoint.HardwareVolume = false;
        f.Player.SetExclusive(true);
        f.Ready();
        Assert.False(f.Player.VolumeAvailable);
        Assert.False(f.Player.IsPlaying);
        Assert.Equal("hardware-volume-confirmation", f.Player.OutputBlockReason);
        Assert.False(f.Player.SetVolume(90, false));
        Assert.Equal(55, f.Player.Volume);
        f.Player.Resume();
        Assert.True(f.Player.IsPlaying);
        Assert.Equal("100", f.Current.Properties["volume"]);
        Assert.Empty(f.Endpoint.Writes);
        // Rebuilding for another device revokes the physical-knob acknowledgement.
        f.Endpoint.Volumes["second-device"] = 20;
        f.Player.SetDevice("wasapi/second-device");
        f.Ready();
        Assert.False(f.Player.IsPlaying);
        Assert.True(f.Player.HardwareVolumeConfirmationRequired);
    });

    [Fact]
    public Task SoftwareEndpointMuteIsNotSilentlyBypassedInExclusive() => Run(f =>
    {
        f.Endpoint.HardwareMute = false;
        f.Endpoint.Muted = true;
        f.Player.SetExclusive(true);
        f.Ready();
        Assert.False(f.Player.IsPlaying);
        Assert.True(f.Player.HardwareVolumeConfirmationRequired);
        Assert.True(f.Endpoint.Muted);
        f.Player.Resume();
        Assert.True(f.Player.IsPlaying);
        Assert.True(f.Endpoint.Muted);
    });

    [Theory]
    [InlineData("endpoint")]
    [InlineData("volume")]
    [InlineData("capability")]
    public Task ReadFailureKeepsPausedUntilSuccessfulRetry(string failure) => Run(f =>
    {
        f.Player.SetExclusive(true);
        f.Endpoint.FailCapture = failure == "endpoint";
        f.Endpoint.FailVolume = failure == "volume";
        if (failure == "capability") f.Endpoint.HardwareVolume = null;
        f.Ready();
        Assert.False(f.Player.IsPlaying);
        Assert.DoesNotContain("set mute=no", f.Current.Operations);
        Assert.DoesNotContain("set pause=no", f.Current.Operations);
        f.Player.Resume();
        Assert.False(f.Player.IsPlaying);
        f.Endpoint.FailCapture = f.Endpoint.FailVolume = false;
        f.Endpoint.HardwareVolume = true;
        f.Player.Resume();
        Assert.True(f.Player.IsPlaying);
        Assert.Empty(f.Endpoint.Writes);
    });

    [Fact]
    public Task FailedEndpointWriteDoesNotPretendSuccessAndKeepsPlaybackSafe() => Run(f =>
    {
        f.Endpoint.RejectWrite = true;
        Assert.False(f.Player.SetVolume(90, false));
        Assert.Equal(55, f.Player.Volume);
        Assert.False(f.Player.IsPlaying);
        Assert.Equal("endpoint-write-failed", f.Player.OutputBlockReason);
        f.Player.Resume();
        Assert.True(f.Player.IsPlaying); // read-only retry can use the actual unchanged volume
    });

    [Fact]
    public Task EndpointLostImmediatelyAfterWriteCannotReturnSuccess() => Run(f =>
    {
        f.Endpoint.FailAfterWrite = true;
        Assert.False(f.Player.SetVolume(20, false));
        Assert.False(f.Player.IsPlaying);
        Assert.Null(f.Player.EndpointVolumeValue);
        Assert.False(f.Player.VolumeAvailable);
    });

    [Fact]
    public Task DeviceLossPausesAndReconnectionDoesNotAutoplay() => Run(f =>
    {
        f.Player.SetDevice("wasapi/render-device");
        f.Ready();
        f.Endpoint.FailCapture = true;
        f.Player.RefreshOutput();
        Assert.False(f.Player.IsPlaying);
        Assert.False(f.Player.VolumeAvailable);
        f.Endpoint.FailCapture = false;
        f.Player.RefreshOutput();
        Assert.False(f.Player.IsPlaying);
        f.Player.Resume();
        Assert.True(f.Player.IsPlaying);
    });

    [Fact]
    public async Task LegacySettingsDiscardAllVolumeFieldsWithoutAnyEndpointWrite()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        var path = Path.Combine(Path.GetTempPath(), "jeek-volume-migration-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"exclusive":true,"volume":100,"sharedVolume":95,"exclusiveVolume":80,"sharedDeviceVolumes":{"render-device":100},"muted":true}""");
            await session.Dispatch(() =>
            {
                var settings = new SettingsStore(path);
                using (var f = new Fixture(settings))
                {
                    Assert.Equal(55, f.Player.Volume);
                    Assert.True(f.Player.Muted);
                    f.Player.SetExclusive(false);
                    f.Ready();
                    Assert.Empty(f.Endpoint.Writes);
                    Assert.Equal(55, f.Endpoint.Volume);
                }
                settings.WriteNow();
                var json = File.ReadAllText(path);
                foreach (var key in new[] { "volume", "sharedVolume", "exclusiveVolume", "sharedDeviceVolumes" })
                    Assert.DoesNotContain("\"" + key + "\"", json);
            }, CancellationToken.None);
        }
        finally { await Task.Delay(1300); File.Delete(path); }
    }

    [Fact]
    public Task RecreatesDecoderAtCuePositionAndRestoresVolumeBeforePlaying() => Run(f =>
    {
        var before = f.Player.Queue.Items.ToArray();
        var completed = 0;
        f.Player.TrackCompleted += () => completed++;
        f.Player.SetFadeVolume(45);
        f.Player.SetExclusive(true);
        var engine = f.Current;
        Assert.True(f.Engines[0].Disposed);
        Assert.Equal("yes", engine.Properties["audio-exclusive"]);
        Assert.Equal("45", engine.Properties["volume"]);
        Assert.Equal("yes", engine.Properties["pause"]);
        Assert.Equal("yes", engine.Properties["mute"]);
        Assert.Equal(12.345, f.Player.Position.TotalSeconds, 3);
        var load = Assert.Single(engine.Commands, c => c[0] == "loadfile" && c[2] == "replace");
        var options = load[4].Split(',').Select(o => o.Split('=')).ToDictionary(o => o[0], o => double.Parse(o[1], CultureInfo.InvariantCulture));
        Assert.Equal(72.345, options["start"], 6);
        Assert.Equal(179.999999, options["end"], 6);
        engine.Emit(Mpv.EventId.AudioReconfig);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("set pause=no", engine.Operations);
        engine.Emit(Mpv.EventId.FileLoaded);
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Player.IsPlaying);
        Assert.Equal(55, f.Player.Volume);
        Assert.Empty(f.Endpoint.Writes);
        Assert.Equal(before, f.Player.Queue.Items);
        Assert.Equal(0, f.Player.Queue.CurrentIndex);
        Assert.True(f.Player.Queue.Shuffle);
        Assert.Equal(RepeatMode.All, f.Player.Queue.Repeat);
        Assert.Equal(0, completed);
        Assert.Empty(f.Errors);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PausedAndMutedStatesSurviveTheReload(bool muted) => Run(f =>
    {
        f.Player.SetVolume(23, muted);
        f.Player.Pause();
        Dispatcher.UIThread.RunJobs();
        f.Player.SetExclusive(true);
        f.Ready();
        Assert.False(f.Player.IsPlaying);
        Assert.Equal("yes", f.Current.Properties["pause"]);
        Assert.Equal(muted ? "yes" : "no", f.Current.Properties["mute"]);
        Assert.Equal(muted, f.Player.Muted);
        Assert.Equal(12.345, f.Player.Position.TotalSeconds, 3);
    });

    [Fact]
    public Task RapidSwitchIgnoresQueuedEventsFromBothDiscardedEngines() => Run(f =>
    {
        f.Engines[0].Emit(Mpv.EventId.FileLoaded);
        f.Player.SetExclusive(true);
        var middle = f.Current;
        middle.Emit(Mpv.EventId.FileLoaded);
        middle.Emit(Mpv.EventId.AudioReconfig);
        f.Player.SetExclusive(false);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("set pause=no", f.Current.Operations);
        Assert.Equal("yes", f.Current.Properties["mute"]);
        Assert.Equal(12.345, f.Player.Position.TotalSeconds, 3);
        f.Ready();
        Assert.False(f.Player.Exclusive);
        Assert.True(f.Player.IsPlaying);
        Assert.Equal(1, f.Player.CurrentTrack!.Id);
        Assert.True(middle.Disposed);
    });

    [Fact]
    public Task ResumeDuringReloadCannotBypassTheVolumeGateAndPauseIsRespected() => Run(f =>
    {
        f.Player.SetExclusive(true);
        f.Player.Resume();
        Assert.DoesNotContain("set pause=no", f.Current.Operations);
        Assert.Equal("yes", f.Current.Properties["mute"]);
        f.Player.Pause();
        f.Ready();
        Assert.False(f.Player.IsPlaying);
        Assert.Equal("yes", f.Current.Properties["pause"]);
    });

    [Fact]
    public Task ABackendThatCannotBeMutedIsClosedBeforeSwitching() => Run(f =>
    {
        f.Current.RejectMute = true;
        f.Player.SetExclusive(true);
        Assert.True(f.Current.Disposed);
        Assert.Single(f.Engines);
        Assert.False(f.Player.IsPlaying);
        Assert.False(f.Player.EngineReady);
        Assert.Single(f.Errors);
    });

    [Fact]
    public async Task RealDecoderReloadsSilentAudioAtTheSavedPositionWithoutUsingAudioHardware()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "jeek-output-mode-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                using (var writer = new BinaryWriter(File.Create(path)))
                {
                    const int dataLength = 16000 * 2 * 30;
                    writer.Write("RIFF"u8); writer.Write(36 + dataLength); writer.Write("WAVEfmt "u8);
                    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000);
                    writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
                    writer.Write("data"u8); writer.Write(dataLength); writer.Write(new byte[dataLength]);
                }
                var source = new SilentTrackSource(path);
                Mpv? engine = null;
                using var player = new PlayerService(source, options => engine = new Mpv(options.Select(o => o.Item1 == "ao" ? ("ao", "null") : o)), new FakeEndpointVolume());
                var errors = new List<string>();
                player.Error += errors.Add;

                player.Restore([new(1, false)], 0, false, RepeatMode.Off, null, TimeSpan.FromSeconds(6.75));
                player.PrepareRestored();
                WaitFor(() => engine!.GetDouble("time-pos") is > 6.7);
                errors.Clear();
                player.SetExclusive(true);
                WaitFor(() => errors.Count > 0);
                Assert.Equal(6.75, player.Position.TotalSeconds, 1);
                Assert.Equal(6.75, engine!.GetDouble("time-pos")!.Value, 1);
                Assert.Equal("null", engine.Get("current-ao"));
                Assert.Equal("yes", engine.Get("pause"));
                Assert.Equal("yes", engine.Get("mute"));
                Assert.False(player.IsPlaying);
            }
            finally { File.Delete(path); }
        }, CancellationToken.None);
    }

    static void WaitFor(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (ready()) return;
            Thread.Sleep(10);
        }
        Assert.Fail("Timed out waiting for the audio decoder.");
    }

    sealed class SilentTrackSource(string path) : ITrackSource
    {
        readonly Track _track = new() { Id = 1, TrackKey = "silent", Dir = "", FileName = "silent.wav", Title = "Silent", ArtistDisplay = "Test", DurationMs = 30000 };
        public Track? ById(long id) => id == 1 ? _track : null;
        public Track? ByKey(string key) => key == _track.TrackKey ? _track : null;
        public string? PathOf(Track track) => path;
    }

    static async Task Run(Action<Fixture> scenario)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(() =>
        {
            using var fixture = new Fixture();
            scenario(fixture);
        }, CancellationToken.None);
    }

    internal sealed class FakeEndpointVolume : IEndpointVolume
    {
        public readonly Dictionary<string, double> Volumes = new() { ["render-device"] = 55 };
        public readonly List<(string, double)> Writes = [];
        public string DeviceId = "render-device";
        public bool RejectWrite, FailCapture, FailVolume, FailAfterWrite, Muted;
        public bool? HardwareVolume = true, HardwareMute = true;
        public double? SessionVolume = 100;
        public bool? SessionMuted = false;
        public string? LastError => FailCapture || FailVolume ? "test read failure" : RejectWrite ? "test write failure" : null;
        public double Volume { get => Volumes[DeviceId]; set => Volumes[DeviceId] = value; }
        public EndpointVolumeSnapshot? Capture(string? device)
        {
            var id = device is null or "auto" ? DeviceId : device.Replace("wasapi/", "");
            return FailCapture || !Volumes.TryGetValue(id, out var volume) ? null :
                new(id, FailVolume ? null : volume, Muted, HardwareVolume, HardwareMute, SessionVolume, SessionMuted, LastError);
        }
        public bool SetVolume(string deviceId, double volume)
        {
            if (RejectWrite) return false;
            Writes.Add((deviceId, volume));
            Volumes[deviceId] = volume;
            if (FailAfterWrite) FailCapture = true;
            return true;
        }
    }

    internal sealed class Fixture : ITrackSource, IDisposable
    {
        readonly string _path = Path.GetTempFileName();
        readonly Track _track;
        readonly Track _nextTrack;
        public PlayerService Player { get; }
        public List<FakeEngine> Engines { get; } = [];
        public List<string> Errors { get; } = [];
        public FakeEngine Current => Engines[^1];
        public FakeEndpointVolume Endpoint { get; }
        public HashSet<long> MissingPaths { get; } = [];

        public Fixture(SettingsStore? settings = null, string initialNativeVolume = "100", FakeEndpointVolume? endpoint = null,
            IExclusiveAudioFormat? formats = null, IDeviceAudioFormat? deviceFormats = null,
            int? initialBits = null, int nextRate = 96000, Action<FakeEngine>? configureEngine = null)
        {
            Endpoint = endpoint ?? new FakeEndpointVolume();
            _track = new Track { Id = 1, TrackKey = "cue#01", Dir = "", FileName = "cue.wav", Title = "CUE track", ArtistDisplay = "Test", CueTrackNo = 1, StartMs = 60000, EndMs = 180000, DurationMs = 120000, SampleRate = 44100, BitsPerSample = initialBits, Channels = 2 };
            _nextTrack = new Track { Id = 2, TrackKey = "next", Dir = "", FileName = "next.wav", Title = "Next", ArtistDisplay = "Test", DurationMs = 120000, SampleRate = nextRate, BitsPerSample = 24, Channels = 2 };
            Player = new PlayerService(this, options =>
            {
                var engine = new FakeEngine(options);
                configureEngine?.Invoke(engine);
                if (Engines.Count == 0) engine.Properties["ao-volume"] = initialNativeVolume;
                Engines.Add(engine);
                return engine;
            }, Endpoint, formats, deviceFormats);
            Player.Error += Errors.Add;
            if (settings is not null) OutputSettingsBinding.Attach(Player, settings);
            Player.Restore([new(1, false), new(2, true)], 0, true, RepeatMode.All, null, TimeSpan.FromSeconds(12.345));
            Player.PrepareRestored();
            Current.Emit(Mpv.EventId.FileLoaded);
            Current.Emit(Mpv.EventId.AudioReconfig);
            Dispatcher.UIThread.RunJobs();
            Player.Resume();
            Dispatcher.UIThread.RunJobs();
        }

        public void Ready()
        {
            Current.Emit(Mpv.EventId.FileLoaded);
            Current.Emit(Mpv.EventId.AudioReconfig);
            Dispatcher.UIThread.RunJobs();
        }
        public Track? ById(long id) => id == 1 ? _track : id == 2 ? _nextTrack : null;
        public Track? ByKey(string trackKey) => trackKey == _track.TrackKey ? _track : null;
        public string? PathOf(Track track) => MissingPaths.Contains(track.Id) ? null : _path;
        public void Dispose() { Player.Dispose(); File.Delete(_path); }
    }

    internal sealed class FakeEngine : IPlaybackEngine
    {
        public Dictionary<string, string> Properties { get; } = new() { ["seekable"] = "yes", ["seeking"] = "no", ["ao-volume"] = "100", ["ao-mute"] = "no", ["current-ao"] = "wasapi", ["playlist-count"] = "0" };
        public List<string[]> Commands { get; } = [];
        public List<string> Operations { get; } = [];
        public bool Disposed, RejectMute, RejectSeek, RejectLoad;
        long _entryId = 9;
        public FakeEngine(IEnumerable<(string, string)> options)
        {
            foreach (var (key, value) in options) Properties[key] = value;
        }
        public event Action<string, string?>? PropertyChanged;
        public void EmitProperty(string name, string? value) => PropertyChanged?.Invoke(name, value);
        public event Action<long>? FileStarted;
        public void StartEntry(int index) => FileStarted?.Invoke(GetLong($"playlist/{index}/id") ?? -1);
        public void StartEntryId(long id) => FileStarted?.Invoke(id);
        public event Action<long>? FileLoaded;
        public event Action<long, Mpv.EndReason, string?>? FileEnded;
        public event Action<Mpv.EventId>? Notified;
        public event Action<string, string, string>? Log { add { } remove { } }
        public void Observe(string name) { }
        public void Emit(Mpv.EventId e)
        {
            if (e == Mpv.EventId.FileLoaded) FileLoaded?.Invoke(GetLong("playlist/0/id") ?? -1);
            else Notified?.Invoke(e);
        }
        public void EndCurrent(Mpv.EndReason reason) => FileEnded?.Invoke(GetLong("playlist/0/id") ?? -1, reason, "test decoder failure");
        public string? Get(string name) => Properties.GetValueOrDefault(name);
        public double? GetDouble(string name) => double.TryParse(Get(name), CultureInfo.InvariantCulture, out var n) ? n : null;
        public long? GetLong(string name) => long.TryParse(Get(name), out var n) ? n : null;
        public int TrySet(string name, string value)
        {
            Operations.Add($"set {name}={value}");
            if (name == "mute" && RejectMute) return -1;
            Properties[name] = value;
            if (name == "pause") PropertyChanged?.Invoke(name, value);
            return 0;
        }
        public int TryRun(params string[] args)
        {
            Commands.Add(args);
            if (args[0] == "seek") return RejectSeek ? -1 : 0;
            if (args[0] == "playlist-remove")
            {
                var index = int.Parse(args[1], CultureInfo.InvariantCulture);
                var count = (int)(GetLong("playlist-count") ?? 0);
                for (var i = index; i < count - 1; i++) Properties[$"playlist/{i}/id"] = Properties[$"playlist/{i + 1}/id"];
                Properties.Remove($"playlist/{count - 1}/id");
                Properties["playlist-count"] = Math.Max(0, count - 1).ToString();
            }
            if (args[0] == "loadfile")
            {
                if (RejectLoad) return -1;
                var count = args[2] == "replace" ? 1 : (int)GetLong("playlist-count")! + 1;
                Properties["playlist-count"] = count.ToString();
                Properties[$"playlist/{count - 1}/id"] = (++_entryId).ToString();
                if (args[2] == "replace")
                {
                    var start = args[4].Split(',').FirstOrDefault(s => s.StartsWith("start="))?[6..];
                    Properties["time-pos"] = start ?? "0";
                }
            }
            return 0;
        }
        public void Dispose() { Operations.Add("dispose"); Disposed = true; }
    }
}
