using System.Globalization;
using Avalonia.Headless;
using Avalonia.Threading;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Playback;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class OutputModeSwitchTests
{
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

    sealed class Fixture : ITrackSource, IDisposable
    {
        readonly string _path = Path.GetTempFileName();
        readonly Track _track;
        readonly Track _nextTrack;
        public PlayerService Player { get; }
        public List<FakeEngine> Engines { get; } = [];
        public List<string> Errors { get; } = [];
        public FakeEngine Current => Engines[^1];

        public Fixture()
        {
            _track = new Track { Id = 1, TrackKey = "cue#01", Dir = "", FileName = "cue.wav", Title = "CUE track", ArtistDisplay = "Test", CueTrackNo = 1, StartMs = 60000, EndMs = 180000, DurationMs = 120000 };
            _nextTrack = new Track { Id = 2, TrackKey = "next", Dir = "", FileName = "next.wav", Title = "Next", ArtistDisplay = "Test", DurationMs = 120000 };
            Player = new PlayerService(this, options =>
            {
                var engine = new FakeEngine(options);
                Engines.Add(engine);
                return engine;
            });
            Player.Error += Errors.Add;
            Player.SetVolume(23, false);
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
