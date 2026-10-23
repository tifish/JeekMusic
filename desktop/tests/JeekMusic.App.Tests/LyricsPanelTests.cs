using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.Automation;
using JeekMusic.Core.Lyrics;
using JeekMusic.Library;
using JeekMusic.Lyrics;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI;
using JeekMusic.UI.ViewModels;
using System.Text.Json.Nodes;
using Track = JeekMusic.Library.Track;

namespace JeekMusic.App.Tests;

// These tests create only Avalonia Headless windows. Both engine and endpoint are
// injected fakes; no AppServices default constructor, mpv or real audio is used.
[Collection("Avalonia UI")]
public sealed class LyricsPanelTests
{
    static string Lrc(int count = 24) => string.Join('\n', Enumerable.Range(0, count).Select(i =>
        $"[{i * 4 / 60:00}:{i * 4 % 60:00}]Line {i} " + (i == 8 ? string.Concat(Enumerable.Repeat("long wrapped lyric words ", 12)) : "context") +
        $"\n[{i * 4 / 60:00}:{i * 4 % 60:00}]翻译 {i}"));
    static FoundLyrics Found(string text, int offset = 0) => new(LrcParser.Parse(text), "synthetic", null, offset);

    [Fact]
    public Task PlaybackLinesCenterAndProgrammaticScrollNeverSuspendsFollowing() => Run(() =>
    {
        using var h = new Harness();
        h.Seek(0); h.AssertCentered(0);
        h.Seek(32); h.AssertCentered(8);
        var row = h.Rows[8];
        Assert.Equal(2, row.Children.Count);
        Assert.True(row.Children[0].Bounds.Height > 50); // Wrapped original + translation move as one row.
        Assert.Equal(FontWeight.SemiBold, ((TextBlock)row.Children[1]).FontWeight);
        // Natural playback position updates, rather than a seek event.
        h.Audio.Current.Properties["time-pos"] = "100"; // CUE begins at 60 seconds.
        h.Vm.ProgressVisible = true;
        h.Pump(); h.AssertCentered(10);
        h.Audio.Current.Properties["time-pos"] = "101";
        h.Vm.ProgressVisible = true;
        h.Pump(); h.AssertCentered(10);
        h.Seek(92); h.AssertCentered(23);
        Assert.False(h.Bool("manualBrowsing"));
        Assert.False(h.Bool("locatorVisible"));
    });

    [Fact]
    public Task ManualWheelWaitsFromLastInteractionAndReturnsWhilePausedOnSameLine() => Run(() =>
    {
        using var h = new Harness();
        h.Audio.Player.Pause();
        h.Seek(32);
        h.Wheel();
        Assert.True(h.Bool("manualBrowsing"));
        Assert.True(h.Bool("locatorVisible"));
        var offset = h.Scroll.Offset.Y;
        h.Clock.Advance(2.9);
        h.Panel.ResumeFollowIfDue(); h.Pump();
        Assert.Equal(offset, h.Scroll.Offset.Y);
        h.Wheel();
        h.Clock.Advance(2.9);
        h.Panel.ResumeFollowIfDue(); h.Pump();
        Assert.True(h.Bool("manualBrowsing"));
        h.Clock.Advance(.11);
        h.Panel.ResumeFollowIfDue(); h.Pump();
        h.AssertCentered(8);
        Assert.False(h.Bool("manualBrowsing"));
        Assert.False(h.Audio.Player.IsPlaying);
    });

    [Fact]
    public Task HeldPointerDoesNotLoseBrowsingAndReleaseRestartsDeadline() => Run(() =>
    {
        using var h = new Harness();
        h.Seek(40);
        var point = h.Scroll.TranslatePoint(new Point(30, 30), h.Window)!.Value;
        h.Window.MouseDown(point, MouseButton.Left);
        h.Pump();
        Assert.True(h.Bool("pointerHeld"));
        h.Clock.Advance(4);
        h.Panel.ResumeFollowIfDue();
        Assert.True(h.Bool("manualBrowsing"));
        h.Window.MouseMove(point + new Vector(0, 10));
        h.Window.MouseUp(point, MouseButton.Left);
        h.Pump();
        Assert.False(h.Bool("pointerHeld"));
        h.Clock.Advance(2.99); h.Panel.ResumeFollowIfDue();
        Assert.True(h.Bool("manualBrowsing"));
        h.Clock.Advance(.02); h.Panel.ResumeFollowIfDue(); h.Pump();
        h.AssertCentered(10);
        Assert.False(h.Bool("manualBrowsing"));
    });

    [Fact]
    public Task ExternalSeekIncludingSameLineCancelsBrowsingAndObsoleteQueuedTargets() => Run(() =>
    {
        using var h = new Harness();
        h.Seek(32); h.Wheel();
        h.Seek(33); h.AssertCentered(8);
        h.Wheel();
        h.Audio.Player.SeekBy(TimeSpan.FromSeconds(5));
        h.Pump(); h.AssertCentered(9);
        h.Audio.Player.Seek(TimeSpan.FromSeconds(4));
        h.Audio.Player.Seek(TimeSpan.FromSeconds(80));
        h.Audio.Player.Seek(TimeSpan.FromSeconds(16));
        h.Pump(); h.AssertCentered(4);
        Assert.False(h.Bool("manualBrowsing"));
    });

    [Fact]
    public Task DecoderRestartAcknowledgementOrResumeDoesNotCancelNewerManualBrowsing() => Run(() =>
    {
        using var h = new Harness(); h.Seek(40); h.Wheel();
        h.Audio.Current.Emit(Mpv.EventId.PlaybackRestart); h.Pump();
        Assert.True(h.Bool("manualBrowsing"));
        h.Audio.Player.Pause(); h.Audio.Player.Resume();
        h.Audio.Current.Emit(Mpv.EventId.PlaybackRestart); h.Pump();
        Assert.True(h.Bool("manualBrowsing"));
        h.Seek(41); h.AssertCentered(10);
        Assert.False(h.Bool("manualBrowsing"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TouchAndScrollbarDragSuspendUntilThreeSecondsAfterRelease(bool touch) => Run(() =>
    {
        using var h = new Harness();
        h.Seek(40);
        // Inertia uses Avalonia's wall clock, independently of the simulated deadline.
        h.Scroll.IsScrollInertiaEnabled = false;
        var thumb = h.Scroll.GetVisualDescendants().OfType<ScrollBar>()
            .Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical)
            .GetVisualDescendants().OfType<Thumb>().Single();
        var source = touch ? (Control)h.Scroll : thumb;
        var point = source.TranslatePoint(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2), h.Window)!.Value;
        var oldOffset = h.Scroll.Offset.Y;
        using var contact = touch ? h.Window.TouchBegin(point) : null;
        if (!touch) h.Window.MouseDown(point, MouseButton.Left);
        var moved = point + new Vector(0, touch ? -60 : 30);
        if (touch) h.Window.TouchMove(contact!, moved); else h.Window.MouseMove(moved);
        h.Pump();
        Assert.True(h.Bool("manualBrowsing"));
        Assert.NotEqual(oldOffset, h.Scroll.Offset.Y);
        h.Clock.Advance(4); h.Panel.ResumeFollowIfDue();
        Assert.True(h.Bool("manualBrowsing"));
        if (touch) h.Window.TouchEnd(contact!, moved); else h.Window.MouseUp(moved, MouseButton.Left);
        h.Pump();
        Assert.False(h.Bool("pointerHeld"));
        h.Clock.Advance(2.9); h.Panel.ResumeFollowIfDue();
        Assert.True(h.Bool("manualBrowsing"));
        h.Clock.Advance(.2); h.Panel.ResumeFollowIfDue(); h.Pump();
        Assert.False(h.Bool("manualBrowsing")); h.AssertCentered(10);
    });

    [Fact]
    public Task LayoutScrollDoesNotExtendManualDeadlineAndPendingFollowYieldsToInput() => Run(() =>
    {
        using var h = new Harness();
        h.Audio.Player.Seek(TimeSpan.FromSeconds(32)); // Follow is queued but has not run.
        h.Wheel();
        Assert.True(h.Bool("manualBrowsing"));
        h.Clock.Advance(2.9);
        h.Window.Width = 700; h.Window.Height = 850; h.Pump();
        Assert.True(h.Bool("manualBrowsing"));
        h.Clock.Advance(.11); h.Panel.ResumeFollowIfDue(); h.Pump();
        h.AssertCentered(8);
    });

    [Fact]
    public Task CancelledScrollbarCaptureDoesNotLeaveFollowingPermanentlySuspended() => Run(() =>
    {
        using var h = new Harness(); h.Seek(40);
        var thumb = h.Scroll.GetVisualDescendants().OfType<ScrollBar>()
            .Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical)
            .GetVisualDescendants().OfType<Thumb>().Single();
        IPointer? pointer = null;
        thumb.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, RoutingStrategies.Tunnel);
        var point = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), h.Window)!.Value;
        h.Window.MouseDown(point, MouseButton.Left); h.Pump();
        Assert.True(h.Bool("pointerHeld"));
        pointer!.Capture(null); h.Pump();
        Assert.False(h.Bool("pointerHeld"));
        h.Clock.Advance(3.1); h.Panel.ResumeFollowIfDue(); h.Pump(); h.AssertCentered(10);
        h.Window.MouseUp(point, MouseButton.Left);
    });

    [Fact]
    public Task LayoutScalingRecentersTheSameBilingualRow() => Run(() =>
    {
        using var h = new Harness(); h.Seek(32);
        h.Host.Child = null;
        var scaled = new LayoutTransformControl { Child = h.Panel };
        h.Host.Child = scaled;
        foreach (var factor in new[] { 1.0, 1.5, 2.0 })
        {
            h.Window.Width = 720; h.Window.Height = 1100;
            scaled.LayoutTransform = new ScaleTransform(factor, factor);
            h.Pump(); h.AssertCentered(8);
        }
    });

    [Fact]
    public async Task RealLyricsServiceCacheAndOffsetReloadUseTheSameAsyncLayoutPath()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(async () =>
        {
            using var h = new Harness();
            var track = h.Audio.Player.CurrentTrack!;
            h.Db.CacheLyrics(track.TrackKey, Lrc(), true, "synthetic cache");
            var panel = new LyricsPanel(h.Vm, h.Lyrics);
            h.Host.Child = panel;
            async Task Loaded(int offset)
            {
                for (var i = 0; i < 100 && (panel.Diagnostics()["loaded"]!.GetValue<bool>() == false ||
                     panel.Diagnostics()["offsetMs"]?.GetValue<int>() != offset); i++) await Task.Delay(10);
                h.Pump();
                Assert.True(panel.Diagnostics()["loaded"]!.GetValue<bool>());
                Assert.Equal(offset, panel.Diagnostics()["offsetMs"]!.GetValue<int>());
            }
            await Loaded(0);
            h.Lyrics.SetOffset(track, 500);
            await Loaded(500);
            h.Audio.Player.Seek(TimeSpan.FromSeconds(32.5)); h.Pump();
            Assert.Equal(8, panel.Diagnostics()["currentIndex"]!.GetValue<int>());
            h.Host.Child = h.Panel;
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DispatcherTimerActivelyRecentersPausedSameLineWithoutPositionEvents()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(async () =>
        {
            using var h = new Harness(systemClock: true);
            h.Audio.Player.Pause(); h.Seek(40); h.Wheel();
            Assert.True(h.Bool("manualBrowsing"));
            await Task.Delay(3300);
            h.Pump();
            Assert.False(h.Bool("manualBrowsing")); h.AssertCentered(10);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public Task BeforeFirstTimestampReturnsToBeginningWithoutHighlightAndLongLyricsStayIdleWithinLine() => Run(() =>
    {
        using var h = new Harness(Found("[00:01]intro\n" + Lrc(1500).Replace("[00:00]", "[00:02]")));
        h.Seek(100); h.Seek(0);
        Assert.Equal(-1, h.Int("currentIndex"));
        var first = h.Rows[0];
        var center = first.TranslatePoint(new Point(0, first.Bounds.Height / 2), h.Scroll)!.Value.Y;
        Assert.InRange(Math.Abs(center - h.Scroll.Viewport.Height / 2), 0, 1.5);
        Assert.All(h.Rows, row => Assert.Equal(FontWeight.Normal, ((TextBlock)row.Children[0]).FontWeight));
        h.Seek(40);
        var rowCount = h.Rows.Length;
        var changes = 0;
        h.Scroll.ScrollChanged += (_, _) => changes++;
        for (var i = 0; i < 100; i++) { h.Vm.ProgressVisible = true; h.Pump(); }
        Assert.Equal(rowCount, h.Rows.Length);
        Assert.Equal(0, changes);
        Assert.False(h.Bool("manualBrowsing"));
    });

    [Fact]
    public Task HiddenAncestorStopsFollowingAndReopeningUsesCurrentPositionAndLayout() => Run(() =>
    {
        using var h = new Harness();
        h.Seek(20); h.Wheel();
        h.Host.IsVisible = false; h.Pump();
        Assert.False(h.Bool("manualBrowsing"));
        var offset = h.Scroll.Offset;
        h.Audio.Player.Seek(TimeSpan.FromSeconds(80)); h.Pump();
        Assert.Equal(offset, h.Scroll.Offset);
        Assert.False(h.Bool("following"));
        h.Host.IsVisible = true; h.Pump(); h.AssertCentered(20);
        foreach (var full in new[] { true, false })
        {
            h.Seek(80);
            h.Panel.FullPage = full;
            h.Window.Width = full ? 1000 : 320;
            h.Window.Height = full ? 850 : 620;
            h.Pump(); h.AssertCentered(20);
            h.Panel.FontSize = full ? 22 : 14;
            h.Seek(32); h.AssertCentered(8);
            h.Seek(0); h.AssertCentered(0);
            h.Seek(92); h.AssertCentered(23);
        }
    });

    [Fact]
    public Task PlainLyricsStayManuallyBrowsableAndEmptySearchAvailabilityIsPreserved() => Run(() =>
    {
        using var h = new Harness(Found(string.Join('\n', Enumerable.Repeat("Plain text without time tags", 80))));
        h.Wheel();
        var offset = h.Scroll.Offset;
        h.Seek(80);
        Assert.Equal(offset, h.Scroll.Offset);
        Assert.Equal(-1, h.Int("currentIndex"));
        Assert.False(h.Bool("following"));
        Assert.False(h.Bool("locatorVisible"));
        Assert.All(h.Rows, row => Assert.Equal(FontWeight.Normal, ((TextBlock)row.Children[0]).FontWeight));
        h.NextResult = null;
        h.Panel.OnlineSearchAvailable = false;
        h.Lyrics.Invalidate(h.Audio.Player.CurrentTrack!); h.Pump();
        Assert.False(h.Search.IsVisible);
        h.Panel.OnlineSearchAvailable = true;
        h.Lyrics.Invalidate(h.Audio.Player.CurrentTrack!); h.Pump();
        Assert.True(h.Search.IsVisible);
    });

    [Fact]
    public Task SwitchingTimedAndPlainLyricsWithIdenticalRowSizesUpdatesCenteringSpace() => Run(() =>
    {
        using var h = new Harness(Found("[00:50]same\n[00:55]rows"));
        h.NextResult = Found("same\nrows");
        h.Lyrics.Invalidate(h.Audio.Player.CurrentTrack!); h.Pump();
        var content = (Visual)h.Scroll.Content!;
        Assert.Equal(0, h.Rows[0].TranslatePoint(default, content)!.Value.Y);
        h.NextResult = Found("[00:50]same\n[00:55]rows");
        h.Lyrics.Invalidate(h.Audio.Player.CurrentTrack!); h.Pump();
        Assert.InRange(h.Rows[0].TranslatePoint(default, content)!.Value.Y, h.Scroll.Viewport.Height / 2 - .5, h.Scroll.Viewport.Height / 2 + .5);
        Assert.Equal(-1, h.Int("currentIndex"));
    });

    [Fact]
    public Task OldAsyncSongAndSameSongReloadCannotReplaceNewestResult() => Run(() =>
    {
        var pending = new List<TaskCompletionSource<FoundLyrics?>>();
        using var h = new Harness(loader: _ =>
        {
            var t = new TaskCompletionSource<FoundLyrics?>(); pending.Add(t); return t.Task;
        });
        h.Lyrics.Invalidate(h.Audio.Player.CurrentTrack!); // Two overlapping loads for the same track.
        pending[1].SetResult(Found("[00:00]new\n[00:04]new second")); h.Pump();
        Assert.Equal("new", ((TextBlock)h.Rows[0].Children[0]).Text);
        pending[0].SetResult(Found("[00:00]obsolete")); h.Pump();
        Assert.Equal(2, h.Rows.Length);
        h.Lyrics.Invalidate(h.Audio.Player.CurrentTrack!); // Old song remains in flight.
        h.Audio.Player.Next(); h.Audio.Ready(); h.Pump();
        Assert.True(pending.Count >= 4);
        pending[^1].SetResult(Found("[00:00]next song")); h.Pump();
        foreach (var old in pending.Skip(2).SkipLast(1)) old.SetResult(Found("[00:00]previous song"));
        h.Pump();
        Assert.Equal("next song", ((TextBlock)h.Rows[0].Children[0]).Text);
        Assert.Equal("next", h.Panel.Diagnostics()["trackKey"]!.GetValue<string>());
    });

    [Fact]
    public Task LocatorButtonSeeksWithOffsetAndMcpReportsSameControlWithoutNewTools() => Run(() =>
    {
        using var h = new Harness(Found(Lrc(), 500));
        using var app = new AppServices(h.Settings, new LibraryDb(":memory:"), _ => h.Audio.Player);
        var tools = new McpTools(app);
        Assert.False(tools.Call("get_diagnostics", new())["lyricsPanel"]!["created"]!.GetValue<bool>());
        app.LyricsPanel = h.Panel;
        h.Seek(32.5); h.Wheel();
        var target = h.Panel.Diagnostics()["centerLineTimeMs"]!.GetValue<long>();
        var play = h.Panel.GetVisualDescendants().OfType<Button>().Single(b => Equals(ToolTip.GetTip(b), L.T("跳转到此句并恢复跟随")));
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); h.Pump();
        Assert.Equal((target + 500) / 1000.0, h.Audio.Player.Position.TotalSeconds, 3);
        Assert.False(h.Bool("manualBrowsing"));
        h.Wheel();
        tools.Call("playback", new JsonObject { ["action"] = "seek", ["seconds"] = 20.5 }); h.Pump();
        h.AssertCentered(5);
        var diagnostic = tools.Call("get_diagnostics", new())["lyricsPanel"]!;
        Assert.True(diagnostic["following"]!.GetValue<bool>());
        Assert.Equal(5, diagnostic["currentIndex"]!.GetValue<int>());
        Assert.Equal(500, diagnostic["offsetMs"]!.GetValue<int>());
        Assert.Equal(21, tools.Catalog.Count);
        var schema = tools.Catalog.Single(t => t!["name"]!.GetValue<string>() == "get_diagnostics")!["inputSchema"]!;
        Assert.Empty(schema["properties"]!.AsObject());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    });

    static async Task Run(Action test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(test, CancellationToken.None);
    }

    sealed class Clock : TimeProvider
    {
        long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    sealed class Harness : IDisposable
    {
        public readonly OutputModeSwitchTests.Fixture Audio = new();
        public readonly Clock Clock = new();
        readonly LibraryDb _db = new(":memory:");
        public LibraryDb Db => _db;
        readonly LibraryService _library;
        readonly string _settingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        public SettingsStore Settings { get; }
        public readonly PlayerViewModel Vm;
        public readonly LyricsService Lyrics;
        public readonly LyricsPanel Panel;
        public readonly Border Host;
        public readonly Window Window;
        public FoundLyrics? NextResult;
        public ScrollViewer Scroll => Panel.GetVisualDescendants().OfType<ScrollViewer>().Single();
        public StackPanel[] Rows => Panel.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Tag is LyricLine).ToArray();
        public Button Search => Panel.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, L.T("搜索歌词")));
        public Harness(FoundLyrics? found = null, Func<Track, Task<FoundLyrics?>>? loader = null, bool systemClock = false)
        {
            Settings = new SettingsStore(_settingsPath);
            _library = new LibraryService(_db, Settings);
            Lyrics = new LyricsService(_library);
            Vm = new PlayerViewModel(Audio.Player, new SleepTimer(Audio.Player), Settings) { ProgressVisible = false };
            NextResult = found ?? Found(Lrc());
            Panel = new LyricsPanel(Vm, Lyrics, loader ?? (_ => Task.FromResult<FoundLyrics?>(NextResult)), systemClock ? null : Clock);
            Host = new Border { Child = Panel };
            Window = new Window { Content = Host, Width = 360, Height = 700 };
            Window.Show(); Pump();
        }
        public bool Bool(string key) => Panel.Diagnostics()[key]!.GetValue<bool>();
        public int Int(string key) => Panel.Diagnostics()[key]!.GetValue<int>();
        public void Seek(double seconds)
        {
            Audio.Player.Seek(TimeSpan.FromSeconds(seconds));
            // Acknowledge the fake decoder's completed seek, so subsequent natural
            // position updates are not masked by PlayerService's pending target.
            Audio.Current.Properties["time-pos"] = (seconds + (Audio.Player.CurrentTrack!.StartMs ?? 0) / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = Audio.Player.Position;
            Pump();
        }
        public void Wheel()
        {
            Window.MouseWheel(Scroll.TranslatePoint(new Point(40, 40), Window)!.Value, new Vector(0, -3));
            Pump();
        }
        public void Pump()
        {
            for (var i = 0; i < 5; i++) { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        }
        public void AssertCentered(int index)
        {
            Assert.Equal(index, Int("currentIndex"));
            var row = Rows[index];
            var center = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), Scroll)!.Value.Y;
            Assert.InRange(Math.Abs(center - Scroll.Viewport.Height / 2), 0, 1.5);
            Assert.True(Bool("following"));
        }
        public void Dispose()
        {
            Vm.ProgressVisible = false;
            Window.Close(); Audio.Dispose(); _library.Dispose(); _db.Dispose();
            File.Delete(_settingsPath);
        }
    }
}
