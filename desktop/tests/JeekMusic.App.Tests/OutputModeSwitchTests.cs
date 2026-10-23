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
    public Task ReturningToSharedRestoresSystemVolumeSeparatelyFromSessionVolume() => RunWithEndpoint(f =>
    {
        Assert.Equal(55, f.Endpoint!.Volume);
        f.Player.SetExclusive(true);
        f.Ready();
        f.Player.SetVolume(10, false);
        Assert.Equal(10, f.Endpoint.Volume);
        f.Player.SetExclusive(false);
        f.Ready();
        Assert.Equal(55, f.Endpoint.Volume);
        Assert.Equal(23, f.Player.Volume);
        Assert.Equal("23", f.Current.Properties["ao-volume"]);
        f.Endpoint.Volume = 42; // 下一轮应记住用户新调的系统主音量。
        f.Player.SetExclusive(true);
        f.Ready();
        Assert.Equal(10, f.Endpoint.Volume);
        f.Player.SetExclusive(false);
        f.Ready();
        Assert.Equal(42, f.Endpoint.Volume);
        Assert.Empty(f.Errors);
    });

    [Fact]
    public Task FailedSystemVolumeRestoreKeepsPlaybackStoppedAndRetainsTheSnapshot() => RunWithEndpoint(f =>
    {
        f.Player.SetExclusive(true);
        f.Ready();
        f.Player.SetVolume(10, false);
        f.Endpoint!.RejectRestore = true;
        f.Player.SetExclusive(false);
        Assert.True(f.Current.Disposed);
        Assert.False(f.Player.EngineReady);
        Assert.False(f.Player.IsPlaying);
        Assert.Equal(55, f.Player.SharedDeviceVolumes["render-device"]);
        f.Player.Resume();
        f.Ready();
        Assert.DoesNotContain("set pause=no", f.Current.Operations);
        Assert.DoesNotContain("set mute=no", f.Current.Operations);
        f.Endpoint.RejectRestore = false;
        f.Player.Resume();
        Assert.Equal(55, f.Endpoint.Volume);
        Assert.True(f.Player.IsPlaying);
        Assert.Empty(f.Player.SharedDeviceVolumes);
    });

    [Fact]
    public Task DeviceChangeRestoresEachPhysicalDeviceByItsOriginalId() => RunWithEndpoint(f =>
    {
        f.Player.SetExclusive(true);
        f.Ready();
        f.Player.SetVolume(10, false);
        f.Endpoint!.DeviceId = "second-device";
        f.Endpoint.Volume = 72;
        f.Player.SetDevice("wasapi/second-device");
        f.Current.Emit(Mpv.EventId.AudioReconfig);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(10, f.Endpoint.Volume);
        f.Player.SetExclusive(false);
        f.Ready();
        Assert.Equal(72, f.Endpoint.Volume);
        f.Endpoint.DeviceId = "render-device";
        Assert.Equal(55, f.Endpoint.Volume);
        Assert.Empty(f.Player.SharedDeviceVolumes);
    });

    [Fact]
    public Task ExitingExclusivePlaybackRestoresSystemVolumeAfterClosingTheDecoder() => RunWithEndpoint(f =>
    {
        f.Player.SetExclusive(true);
        f.Ready();
        f.Player.SetVolume(10, false);
        f.Player.Dispose();
        Assert.True(f.Current.Disposed);
        Assert.Equal(55, f.Endpoint!.Volume);
        Assert.Empty(f.Player.SharedDeviceVolumes);
    });

    [Fact]
    public Task RestartInExclusivePreservesTheOriginalSystemVolumeSnapshot() => RunWithEndpoint(f =>
    {
        f.Endpoint!.Volume = 10; // 上一进程的独占音量仍留在设备上。
        using var restarted = new PlayerService(f, options => new FakeEngine(options), f.Endpoint);
        restarted.RestoreVolumeSettings(true, 10, 23, 10, false,
            new Dictionary<string, double> { ["render-device"] = 55 });
        restarted.Restore([new(1, false)], 0, false, RepeatMode.Off, null, TimeSpan.Zero);
        restarted.PrepareRestored();
        restarted.SetExclusive(false);
        Assert.Equal(55, f.Endpoint.Volume);
        Assert.Empty(restarted.SharedDeviceVolumes);
        Assert.Equal(23, restarted.Volume);
    });

    [Fact]
    public Task UnableToSaveSystemVolumeBlocksExclusivePlaybackBeforeChangingTheDevice() => RunWithEndpoint(f =>
    {
        f.Endpoint!.FailCapture = true;
        f.Player.SetExclusive(true);
        f.Ready();
        Assert.Equal(55, f.Endpoint.Volume);
        Assert.DoesNotContain(f.Current.Operations, op => op.StartsWith("set ao-volume="));
        Assert.DoesNotContain("set pause=no", f.Current.Operations);
        Assert.False(f.Player.IsPlaying);
        f.Endpoint.FailCapture = false;
        f.Player.Resume();
        Assert.True(f.Player.IsPlaying);
        Assert.Equal(55, f.Player.SharedDeviceVolumes["render-device"]);
    });

    [Fact]
    public async Task SystemVolumeSnapshotSurvivesSettingsSerializationAndIsClearedAfterRestore()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        var path = Path.Combine(Path.GetTempPath(), "jeek-system-volume-" + Guid.NewGuid().ToString("N") + ".json");
        var restartPath = path + ".restart";
        try
        {
            await session.Dispatch(() =>
            {
                var settings = new SettingsStore(path);
                var endpoint = new FakeEndpointVolume();
                byte[] saved;
                using (var f = new Fixture(settings, endpoint: endpoint))
                {
                    f.Player.SetVolume(23, false);
                    f.Player.SetExclusive(true);
                    f.Ready();
                    f.Player.SetVolume(10, false);
                    Assert.Equal(55, settings.Current.SharedDeviceVolumes["render-device"]);
                    settings.WriteNow();
                    saved = File.ReadAllBytes(path);
                }
                // 用上次进程退出前的文件模拟独占状态下重启，不能把设备上现存的 10 当成共享原音量。
                endpoint.Volume = 10;
                File.WriteAllBytes(restartPath, saved);
                var reloaded = new SettingsStore(restartPath);
                using (var f = new Fixture(reloaded, endpoint: endpoint))
                {
                    Assert.Equal(10, endpoint.Volume);
                    Assert.Equal(55, f.Player.SharedDeviceVolumes["render-device"]);
                    f.Player.SetExclusive(false);
                    f.Ready();
                    Assert.Equal(55, endpoint.Volume);
                    Assert.Equal(23, f.Player.Volume);
                    Assert.Empty(reloaded.Current.SharedDeviceVolumes);
                    reloaded.WriteNow();
                    var result = JsonSerializer.Deserialize(File.ReadAllBytes(restartPath), SettingsJsonContext.Default.AppSettings)!;
                    Assert.Empty(result.SharedDeviceVolumes);
                    Assert.False(result.Exclusive);
                }
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Delay(1300);
            File.Delete(path);
            File.Delete(restartPath);
        }
    }

    [Fact]
    public Task ModesRememberTheirVolumesAndRestoreHigherSavedValuesBeforePlaying() => Run(f =>
    {
        f.Player.SetVolume(65, false);
        f.Player.SetExclusive(true);
        f.Ready();
        f.Player.SetVolume(18, false);
        for (var i = 0; i < 3; i++)
        {
            f.Player.SetExclusive(false);
            f.Current.Properties["ao-volume"] = "18";
            Assert.Equal(65, f.Player.Volume);
            Assert.Equal("yes", f.Current.Properties["mute"]);
            f.Ready();
            Assert.Equal("65", f.Current.Properties["ao-volume"]);
            Assert.True(f.Current.Operations.IndexOf("set ao-volume=65") < f.Current.Operations.IndexOf("set pause=no"));
            f.Player.SetExclusive(true);
            f.Ready();
            Assert.Equal(18, f.Player.Volume);
            Assert.Equal("18", f.Current.Properties["ao-volume"]);
            Assert.Equal(65, f.Player.SharedVolume);
            Assert.Equal(18, f.Player.ExclusiveVolume);
        }
        Assert.Empty(f.Errors);
    });

    [Fact]
    public Task RapidSwitchDoesNotReplaceSavedVolumeWithAnUninitializedBackendValue() => Run(f =>
    {
        f.Player.SetVolume(65, false);
        f.Player.SetExclusive(true);
        f.Ready();
        f.Player.SetVolume(18, false);
        f.Player.SetExclusive(false);
        f.Current.Properties["ao-volume"] = "2";
        f.Player.SetExclusive(true);
        f.Ready();
        f.Player.SetExclusive(false);
        f.Ready();
        Assert.Equal(65, f.Player.Volume);
        Assert.Equal(18, f.Player.ExclusiveVolume);
    });

    [Theory]
    [InlineData("rejected")]
    [InlineData("ignored")]
    public Task SavedHigherVolumeStillRequiresSuccessfulReadback(string failure) => Run(f =>
    {
        f.Player.SetVolume(65, false);
        f.Player.SetExclusive(true);
        f.Ready();
        f.Player.SetVolume(18, false);
        f.Player.SetExclusive(false);
        f.Current.Properties["ao-volume"] = "18";
        f.Current.RejectVolume = failure == "rejected";
        f.Current.IgnoreVolume = failure == "ignored";
        f.Ready();
        Assert.False(f.Player.IsPlaying);
        Assert.DoesNotContain("set mute=no", f.Current.Operations);
        Assert.DoesNotContain("set pause=no", f.Current.Operations);
        Assert.Equal(65, f.Player.SharedVolume);
        Assert.Single(f.Errors);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateVolumesPersistAcrossRestartAndLegacyVolumeIsSafelyInitialized(bool exclusive)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        var path = Path.Combine(Path.GetTempPath(), "jeek-volume-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // 原版本只有一个音量；不能把它误当成另一种模式已确认的音量。
            File.WriteAllText(path, $$"""{"exclusive":{{exclusive.ToString().ToLowerInvariant()}},"volume":100}""");
            await session.Dispatch(() =>
            {
                var settings = new SettingsStore(path);
                using (var f = new Fixture(settings, initialNativeVolume: "30"))
                {
                    Assert.Equal(exclusive, f.Player.Exclusive);
                    Assert.Equal(30, f.Player.Volume);
                    Assert.Null(exclusive ? f.Player.SharedVolume : f.Player.ExclusiveVolume);
                    f.Player.SetExclusive(false);
                    f.Ready();
                    f.Player.SetVolume(65, false);
                    f.Player.SetExclusive(true);
                    f.Ready();
                    f.Player.SetVolume(18, true);
                    settings.WriteNow();
                }
                var reloaded = new SettingsStore(path);
                using (var f = new Fixture(reloaded, initialNativeVolume: "5"))
                {
                    Assert.True(f.Player.Exclusive);
                    Assert.Equal(18, f.Player.Volume);
                    Assert.True(f.Player.Muted);
                    Assert.Equal(65, reloaded.Current.SharedVolume);
                    Assert.Equal(18, reloaded.Current.ExclusiveVolume);
                    f.Player.SetExclusive(false);
                    f.Ready();
                    Assert.Equal(65, f.Player.Volume);
                    Assert.Equal(65, reloaded.Current.Volume);
                    f.Player.SetVolume(42, true);
                    Assert.Equal(42, reloaded.Current.SharedVolume);
                    Assert.Equal(18, reloaded.Current.ExclusiveVolume);
                    reloaded.WriteNow();
                }
                var saved = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings)!;
                Assert.False(saved.Exclusive);
                Assert.Equal(42, saved.SharedVolume);
                Assert.Equal(18, saved.ExclusiveVolume);
            }, CancellationToken.None);
        }
        finally
        {
            // 等防抖写入结束后再清理，避免计时器重建已删掉的临时文件。
            await Task.Delay(1300);
            File.Delete(path);
        }
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
        Assert.Equal("23", engine.Properties["ao-volume"]);
        Assert.True(engine.Operations.IndexOf("set ao-volume=23") < engine.Operations.IndexOf("set mute=no"));
        Assert.True(engine.Operations.IndexOf("set ao-volume=23") < engine.Operations.IndexOf("set pause=no"));
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

    [Theory]
    [InlineData("unavailable")]
    [InlineData("rejected")]
    [InlineData("ignored")]
    [InlineData("invalid")]
    public Task FailedVolumeRestorationNeverUnmutesOrResumes(string failure) => Run(f =>
    {
        f.Player.SetExclusive(true);
        var engine = f.Current;
        if (failure == "unavailable") engine.Properties.Remove("ao-volume");
        if (failure == "rejected") engine.RejectVolume = true;
        if (failure == "ignored") engine.IgnoreVolume = true;
        if (failure == "invalid") engine.Properties["ao-volume"] = "NaN";
        f.Ready();
        Assert.False(f.Player.IsPlaying);
        Assert.Equal("yes", engine.Properties["pause"]);
        Assert.Equal("yes", engine.Properties["mute"]);
        Assert.DoesNotContain("set pause=no", engine.Operations);
        Assert.DoesNotContain("set mute=no", engine.Operations);
        Assert.Single(f.Errors);
        f.Player.Resume();
        Assert.False(f.Player.IsPlaying);
        Assert.Equal("yes", engine.Properties["mute"]);
    });

    [Fact]
    public Task DoesNotRaiseExistingDeviceVolumeToTheStoredHundredPercent() => Run(f =>
    {
        f.Player.SetVolume(100, false);
        f.Player.SetExclusive(true);
        f.Current.Properties["ao-volume"] = "30";
        f.Ready();
        Assert.Equal(30, f.Player.Volume);
        Assert.Equal("30", f.Current.Properties["ao-volume"]);
        Assert.DoesNotContain("set ao-volume=100", f.Current.Operations);
        Assert.True(f.Player.IsPlaying);
    });

    [Fact]
    public Task KeepsLowerVolumeChosenInTheWindowsMixer() => Run(f =>
    {
        f.Current.Properties["ao-volume"] = "7";
        f.Player.SetExclusive(true);
        f.Ready();
        Assert.Equal(7, f.Player.Volume);
        Assert.Equal("7", f.Current.Properties["ao-volume"]);
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
                using var player = new PlayerService(source, options => engine = new Mpv(options.Select(o => o.Item1 == "ao" ? ("ao", "null") : o)));
                var errors = new List<string>();
                player.Error += errors.Add;
                player.SetVolume(15, false);
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

    static async Task RunWithEndpoint(Action<Fixture> scenario)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(() =>
        {
            using var fixture = new Fixture(endpoint: new FakeEndpointVolume());
            scenario(fixture);
        }, CancellationToken.None);
    }

    sealed class FakeEndpointVolume : IEndpointVolume
    {
        readonly Dictionary<string, double> _volumes = new() { ["render-device"] = 55 };
        public string DeviceId = "render-device";
        public bool RejectRestore, FailCapture;
        public double Volume { get => _volumes[DeviceId]; set => _volumes[DeviceId] = value; }
        public EndpointVolumeSnapshot? Capture(string? device) => FailCapture ? null : new(DeviceId, Volume);
        public bool Restore(EndpointVolumeSnapshot snapshot)
        {
            if (RejectRestore) return false;
            _volumes[snapshot.DeviceId] = snapshot.Volume;
            return true;
        }
    }

    sealed class Fixture : ITrackSource, IDisposable
    {
        readonly string _path = Path.GetTempFileName();
        readonly Track _track;
        readonly Track _nextTrack;
        public PlayerService Player { get; }
        public List<FakeEngine> Engines { get; } = [];
        public List<string> Errors { get; } = [];
        public FakeEngine Current => Engines[^1];
        public FakeEndpointVolume? Endpoint { get; }

        public Fixture(SettingsStore? settings = null, string initialNativeVolume = "100", FakeEndpointVolume? endpoint = null,
            IExclusiveAudioFormat? formats = null)
        {
            Endpoint = endpoint;
            _track = new Track { Id = 1, TrackKey = "cue#01", Dir = "", FileName = "cue.wav", Title = "CUE track", ArtistDisplay = "Test", CueTrackNo = 1, StartMs = 60000, EndMs = 180000, DurationMs = 120000, SampleRate = 44100, Channels = 2 };
            _nextTrack = new Track { Id = 2, TrackKey = "next", Dir = "", FileName = "next.wav", Title = "Next", ArtistDisplay = "Test", DurationMs = 120000, SampleRate = 96000, BitsPerSample = 24, Channels = 2 };
            Player = new PlayerService(this, options =>
            {
                var engine = new FakeEngine(options);
                if (Engines.Count == 0) engine.Properties["ao-volume"] = initialNativeVolume;
                if (endpoint is not null && engine.Properties["audio-exclusive"] == "yes")
                {
                    engine.Properties["ao-volume"] = Mpv.Num(endpoint.Volume);
                    engine.VolumeSet = v => endpoint.Volume = v;
                }
                Engines.Add(engine);
                return engine;
            }, endpoint, formats);
            Player.Error += Errors.Add;
            if (settings is null) Player.SetVolume(23, false);
            else OutputSettingsBinding.Attach(Player, settings);
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
        public string? PathOf(Track track) => _path;
        public void Dispose() { Player.Dispose(); File.Delete(_path); }
    }

    sealed class FakeEngine : IPlaybackEngine
    {
        public Dictionary<string, string> Properties { get; } = new() { ["ao-volume"] = "100", ["ao-mute"] = "no", ["current-ao"] = "wasapi", ["playlist-count"] = "0" };
        public List<string[]> Commands { get; } = [];
        public List<string> Operations { get; } = [];
        public bool Disposed, RejectVolume, IgnoreVolume, RejectMute;
        public Action<double>? VolumeSet;
        public FakeEngine(IEnumerable<(string, string)> options)
        {
            foreach (var (key, value) in options) Properties[key] = value;
        }
        public event Action<string, string?>? PropertyChanged;
        public event Action<long>? FileStarted { add { } remove { } }
        public event Action<long>? FileLoaded;
        public event Action<long, Mpv.EndReason, string?>? FileEnded { add { } remove { } }
        public event Action<Mpv.EventId>? Notified;
        public event Action<string, string, string>? Log { add { } remove { } }
        public void Observe(string name) { }
        public void Emit(Mpv.EventId e)
        {
            if (e == Mpv.EventId.FileLoaded) FileLoaded?.Invoke(GetLong("playlist/0/id") ?? -1);
            else Notified?.Invoke(e);
        }
        public string? Get(string name) => Properties.GetValueOrDefault(name);
        public double? GetDouble(string name) => double.TryParse(Get(name), CultureInfo.InvariantCulture, out var n) ? n : null;
        public long? GetLong(string name) => long.TryParse(Get(name), out var n) ? n : null;
        public int TrySet(string name, string value)
        {
            Operations.Add($"set {name}={value}");
            if (name == "mute" && RejectMute) return -1;
            if (name == "ao-volume" && RejectVolume) return -1;
            if (name == "ao-volume" && IgnoreVolume) return 0;
            Properties[name] = value;
            if (name == "ao-volume") VolumeSet?.Invoke(double.Parse(value, CultureInfo.InvariantCulture));
            if (name == "pause") PropertyChanged?.Invoke(name, value);
            return 0;
        }
        public int TryRun(params string[] args)
        {
            Commands.Add(args);
            if (args[0] == "loadfile")
            {
                var count = args[2] == "replace" ? 1 : (int)GetLong("playlist-count")! + 1;
                Properties["playlist-count"] = count.ToString();
                Properties[$"playlist/{count - 1}/id"] = (count + 9).ToString();
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
