using System.Globalization;
using Avalonia.Headless;
using Avalonia.Threading;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Playback;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class StartupPlaybackTests
{
    sealed class Source(string path) : ITrackSource
    {
        internal Track Track = new() { Id = 1, TrackKey = "startup-cue", Dir = "", FileName = "startup.wav", Title = "Startup fixture", ArtistDisplay = "Fixture", CueTrackNo = 1, StartMs = 60000, EndMs = 180000, DurationMs = 120000, SampleRate = 44100, Channels = 2 };
        public Track? ById(long id) => id == 1 ? Track : null;
        public Track? ByKey(string key) => key == Track.TrackKey ? Track : null;
        public string? PathOf(Track track) => path;
    }
    sealed class Endpoint : IEndpointVolume
    {
        internal readonly OutputModeSwitchTests.FakeEndpointVolume Inner = new();
        internal int Reads;
        public string? LastError => Inner.LastError;
        public EndpointVolumeSnapshot? Capture(string? device) { Reads++; return Inner.Capture(device); }
        public bool SetVolume(string id, double value) => Inner.SetVolume(id, value);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("auto", false)]
    [InlineData("wasapi/second-device", false)]
    [InlineData("wasapi/second-device", true)]
    public async Task RestoredQueueNeedsNoEngineOrEndpointUntilPlayAndPreservesCueSeek(string? savedDevice, bool unreadable)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(() =>
        {
            var path = Path.GetTempFileName();
            try
            {
                var source = new Source(path);
                var endpoint = new Endpoint(); endpoint.Inner.FailCapture = unreadable;
                endpoint.Inner.Volumes["second-device"] = 35;
                var engines = new List<OutputModeSwitchTests.FakeEngine>();
                using var player = new PlayerService(source, options => { var engine = new OutputModeSwitchTests.FakeEngine(options); engines.Add(engine); return engine; }, endpoint);
                player.RestoreDevice(savedDevice);
                player.Restore([new QueueEntry(1, true)], 0, true, RepeatMode.All, null, TimeSpan.FromSeconds(12.345));
                var queue = player.Queue.Items.ToArray();
                Assert.Equal(savedDevice == "auto" ? null : savedDevice, player.Device);
                Assert.Null(player.ActualDeviceId); Assert.Null(player.EndpointVolumeValue);
                Assert.Empty(engines); Assert.Equal(0, endpoint.Reads);
                Assert.True(player.EndpointReadPending);
                Assert.True(player.CanSeek); Assert.True(player.PausedOutputReleased);
                player.SeekBy(TimeSpan.FromSeconds(5));
                Assert.Equal(17.345, player.Position.TotalSeconds, 3);
                Assert.Empty(engines); Assert.Equal(0, endpoint.Reads);
                player.Resume();
                Assert.True(endpoint.Reads > 0);
                Assert.False(player.EndpointReadPending);
                Assert.Equal(queue, player.Queue.Items); Assert.True(player.Queue.Shuffle); Assert.Equal(RepeatMode.All, player.Queue.Repeat);
                if (unreadable) { Assert.Empty(engines); Assert.False(player.IsPlaying); Assert.NotNull(player.OutputBlockReason); }
                else
                {
                    var engine = Assert.Single(engines);
                    var load = Assert.Single(engine.Commands, command => command[0] == "loadfile");
                    var start = load[4].Split(',').Single(option => option.StartsWith("start="))[6..];
                    Assert.Equal(77.345, double.Parse(start, CultureInfo.InvariantCulture), 6);
                    Assert.Equal(savedDevice == "wasapi/second-device" ? "second-device" : endpoint.Inner.DeviceId, player.ActualDeviceId);
                    engine.Emit(Mpv.EventId.FileLoaded); engine.Emit(Mpv.EventId.AudioReconfig); Dispatcher.UIThread.RunJobs();
                    Assert.True(player.IsPlaying);
                    Assert.Equal(17.345, player.Position.TotalSeconds, 3);
                }
                Assert.Empty(endpoint.Inner.Writes);
            }
            finally { File.Delete(path); }
        }, CancellationToken.None);
    }
}
