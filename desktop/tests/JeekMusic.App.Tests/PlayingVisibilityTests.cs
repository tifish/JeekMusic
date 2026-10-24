using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.Automation;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI;
using JeekMusic.UI.ViewModels;
using Track = JeekMusic.Library.Track;

namespace JeekMusic.App.Tests;

// Real headless DataGrid/Flyout and virtualized rows, in-memory library, empty files,
// injected fake engine/endpoint. Never Shell, client, pipe, decoder or physical audio.
[Collection("Avalonia UI")]
public sealed class PlayingVisibilityTests
{
    [Fact]
    public Task OffscreenPlayResumeAndNavigationRevealWithoutChangingSelectionOrFocus() => Run(() =>
    {
        using var h = new Harness();
        var selection = new[] { h.Rows[1], h.Rows[3] };
        foreach (var row in selection) h.Table.SelectedItems.Add(row);
        h.Pump(); h.Table.Focus();
        var focus = h.Window.FocusManager!.GetFocusedElement();
        h.Window.PlayFrom(h.Track(220)); h.Ready(); h.AssertVisible(220);
        Assert.Equal(selection, h.Table.SelectedItems.Cast<SongRow>());
        Assert.Same(focus, h.Window.FocusManager.GetFocusedElement());
        Assert.InRange(h.Realized.Count, 1, 40); // Not merely a nonvirtualized mock list.
        h.Player.Pause(); h.Pump(); h.ScrollTo(0);
        h.Player.Resume(); h.Ready(); h.AssertVisible(220);
        h.ScrollTo(0); h.Player.Next(); h.Ready(); h.AssertVisible(221);
        h.ScrollTo(0); h.Player.Previous(); h.Ready(); h.AssertVisible(220);
        h.Engine.Properties["time-pos"] = "20";
        h.ScrollTo(0); h.Player.Previous(); h.Pump(); h.AssertVisible(220);
        Assert.Equal(selection, h.Table.SelectedItems.Cast<SongRow>());
    });

    [Fact]
    public Task FullyVisibleDoesNotMoveAndProgressOrOutputRebuildNeverTakesBackManualBrowsing() => Run(() =>
    {
        using var h = new Harness();
        h.Window.PlayFrom(h.Track(2)); h.Ready(); h.AssertVisible(2);
        Assert.Equal("already-visible", h.Diagnostic["result"]!.GetValue<string>());
        var version = h.Version;
        var before = h.TopRowY(2);
        h.Player.Resume(); h.Pump();
        Assert.Equal(before, h.TopRowY(2));
        Assert.Equal(version + 1, h.Version);
        h.ScrollTo(220); var top = h.FirstVisible(); var after = h.Version;
        for (var i = 0; i < 8; i++)
        {
            h.Engine.Properties["time-pos"] = (30 + i).ToString();
            h.Engine.Emit(Mpv.EventId.PlaybackRestart);
            h.Player.SetRepeat(i % 2 == 0 ? RepeatMode.All : RepeatMode.Off);
            h.Player.RefreshOutput(); h.Pump();
        }
        Assert.Equal(after, h.Version); Assert.Equal(top, h.FirstVisible());
        h.Player.SetExclusive(true); h.Ready(); Assert.True(h.Player.IsPlaying);
        Assert.Equal(after, h.Version); Assert.Equal(top, h.FirstVisible());
        Assert.False(h.Diagnostic["currentFullyVisible"]!.GetValue<bool>());
    });

    [Fact]
    public Task AlreadyVisiblePreservesBothScrollOffsetsAndSelection() => Run(() =>
    {
        using var h = new Harness();
        foreach (var column in h.Table.Columns) column.Width = new DataGridLength(200);
        h.Pump();
        h.Viewport.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(), new Vector(120, 0))); h.Pump();
        var horizontal = h.Table.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
        var vertical = h.Table.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.True(horizontal.Value > 0);
        h.Table.SelectedItems.Add(h.Rows[1]); h.Table.SelectedItems.Add(h.Rows[2]); h.Pump();
        var before = (horizontal.Value, vertical.Value, h.TopRowY(3));
        h.Window.PlayFrom(h.Track(3)); h.Ready(); h.AssertVisible(3);
        Assert.Equal(before, (horizontal.Value, vertical.Value, h.TopRowY(3)));
        Assert.Equal(new[] { h.Rows[1], h.Rows[2] }, h.Table.SelectedItems.Cast<SongRow>());
        Assert.True(h.Diagnostic["initiallyFullyVisible"]!.GetValue<bool>());
        Assert.Equal("keep", h.Diagnostic["alignment"]!.GetValue<string>());
    });

    [Theory]
    [InlineData(220, 0)]
    [InlineData(30, 240)]
    public Task OffscreenRequestsCenterInBothDirectionsAfterRealization(int target, int start) => Run(() =>
    {
        using var h = new Harness();
        h.ScrollTo(start);
        Assert.DoesNotContain(h.Realized, r => r.DataContext is SongRow row && row.Track?.Id == h.Track(target).Id);
        h.Window.PlayFrom(h.Track(target)); h.Ready(); h.AssertCentered(target);
        Assert.Equal("centered", h.Diagnostic["result"]!.GetValue<string>());
        Assert.False(h.Diagnostic["initiallyFullyVisible"]!.GetValue<bool>());
        Assert.InRange(h.Realized.Count, 1, 40);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(259)]
    public Task FirstAndLastRowsClampAtTheRealScrollBoundary(int target) => Run(() =>
    {
        using var h = new Harness(); h.ScrollTo(130);
        h.Window.PlayFrom(h.Track(target)); h.Ready(); h.AssertVisible(target);
        var bar = h.Table.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.InRange(Math.Abs(bar.Value - (target == 0 ? bar.Minimum : bar.Maximum)), 0, .75);
        var row = h.Realized.Single(r => ReferenceEquals(r.DataContext, h.Rows[target]));
        var y = row.TranslatePoint(default, h.Viewport)!.Value.Y;
        Assert.InRange(Math.Abs(target == 0 ? y : y + row.Bounds.Height - h.Viewport.Bounds.Height), 0, .75);
        Assert.Equal("clamped", h.Diagnostic["result"]!.GetValue<string>());
    });

    [Fact]
    public Task PartlyClippedCurrentRowBecomesFullyVisibleAfterWheelScrolling() => Run(() =>
    {
        using var h = new Harness();
        h.ScrollTo(230);
        var row = h.Realized.Single(r => ReferenceEquals(r.DataContext, h.Rows[230]));
        h.Window.MouseWheel(h.Viewport.TranslatePoint(new Point(100, 80), h.Window)!.Value, new Vector(0, .2));
        h.Pump();
        Assert.True(h.TopRowY(230) + row.Bounds.Height > h.Viewport.Bounds.Height + .5,
            $"Expected a clipped row, y={h.TopRowY(230)}, height={row.Bounds.Height}, viewport={h.Viewport.Bounds.Height}");
        h.Window.PlayFrom(h.Track(230)); h.Ready(); h.AssertCentered(230);
        Assert.Equal("centered", h.Diagnostic["result"]!.GetValue<string>());
    });

    [Fact]
    public Task NaturalFileStartedAndSingleTrackRepeatRevealOnceWithoutAUserNextCommand() => Run(() =>
    {
        using var h = new Harness();
        h.Player.Play(null, [h.Track(1).Id, h.Track(240).Id], 0); h.Ready();
        h.Engine.StartEntry(0); h.Pump(); var version = h.Version;
        h.Engine.StartEntry(1); h.Pump(); h.AssertVisible(240);
        Assert.Equal(version + 1, h.Version);
        Assert.Equal(h.Track(240).Id, h.Player.CurrentTrack!.Id);
        h.ScrollTo(0); h.Player.SetRepeat(RepeatMode.One);
        h.Engine.StartEntry(1); h.Pump(); h.AssertVisible(240);
        Assert.Equal(version + 2, h.Version);
    });

    [Theory]
    [InlineData("mcp")]
    [InlineData("menu")]
    [InlineData("double")]
    public Task ExistingPlaybackEntrypointsShareOneReveal(string entry) => Run(() =>
    {
        using var h = new Harness();
        h.Table.SelectedItem = h.Rows[230]; h.Pump(); h.ScrollTo(0);
        if (entry == "mcp")
            h.Tools.Call("play_tracks", new() { ["trackIds"] = new JsonArray(h.Track(230).Id) });
        else if (entry == "double")
        {
            h.ScrollTo(230);
            var row = h.Realized.Single(r => ReferenceEquals(r.DataContext, h.Rows[230]));
            var point = row.TranslatePoint(new Point(100, row.Bounds.Height / 2), h.Window)!.Value;
            for (var i = 0; i < 2; i++) { h.Window.MouseDown(point, MouseButton.Left); h.Window.MouseUp(point, MouseButton.Left); }
            h.ScrollTo(0);
        }
        else
        {
            ContextMenu? opened = null;
            using var subscription = ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((menu, _) => opened = menu);
            h.Table.RaiseEvent(new ContextRequestedEventArgs());
            var menu = Assert.IsType<ContextMenu>(opened);
            ((MenuItem)menu.Items[0]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
        }
        h.Ready(); h.AssertVisible(230);
        Assert.Equal(1, h.Version);
    });

    [Fact]
    public Task LatestPlaybackWinsAndUserInputCancelsAQueuedReveal() => Run(() =>
    {
        using var h = new Harness();
        h.Window.PlayFrom(h.Track(240)); h.Engine.Emit(Mpv.EventId.FileLoaded);
        h.Window.PlayFrom(h.Track(180)); h.Engine.Emit(Mpv.EventId.FileLoaded);
        h.Ready(); h.AssertVisible(180);
        Assert.Equal(h.Track(180).Id, h.Diagnostic["requestedTrackId"]!.GetValue<long>());
        h.ScrollTo(0);
        h.App.SongVisibility!.Request(h.Track(180).Id); // A UI callback is already posted for the old track.
        h.Window.PlayFrom(h.Track(240)); h.Ready(); h.AssertCentered(240);
        Assert.Equal(h.Track(240).Id, h.Diagnostic["requestedTrackId"]!.GetValue<long>());
        h.App.SongVisibility!.Request(h.Track(240).Id);
        h.Table.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageDown });
        h.Pump();
        Assert.False(h.Diagnostic["positionPending"]!.GetValue<bool>());
        Assert.Equal("user-input", h.Diagnostic["result"]!.GetValue<string>());
    });

    [Fact]
    public Task VisibleRequestDuringAsyncRowReplacementWaitsForReliableLayoutAndKeepsPosition() => Run(() =>
    {
        using var h = new Harness();
        h.Window.PlayFrom(h.Track(3)); h.Ready();
        var y = h.TopRowY(3); var old = h.Rows;
        h.Vm.Recompute(); Assert.True(h.Vm.ComputingRows);
        h.Player.Resume();
        h.Wait(() => !h.Vm.ComputingRows && !ReferenceEquals(old, h.Rows)); h.AssertVisible(3);
        Assert.Equal(y, h.TopRowY(3));
        Assert.Equal("already-visible", h.Diagnostic["result"]!.GetValue<string>());
        Assert.True(h.Diagnostic["initiallyFullyVisible"]!.GetValue<bool>());
    });

    [Fact]
    public Task AsyncReplacementWaitsForRowsAndNavigationInvalidatesAnOlderRequest() => Run(() =>
    {
        using var h = new Harness();
        h.Window.PlayFrom(h.Track(230)); h.Ready();
        h.ScrollTo(0);
        var old = h.Rows;
        h.Vm.Recompute();
        Assert.True(h.Vm.ComputingRows); Assert.Same(old, h.Rows);
        h.Player.Resume(); // An accepted play intent while replacement rows are still pending.
        h.Wait(() => !h.Vm.ComputingRows && !ReferenceEquals(old, h.Rows));
        h.AssertVisible(230);
        h.App.SongVisibility!.Request(h.Track(230).Id);
        h.Vm.Navigate(new NavTarget.Search("song 000"));
        h.Wait(() => !h.Vm.ComputingRows); h.Pump();
        Assert.Single(h.Rows);
        Assert.Equal("view-changed", h.Diagnostic["result"]!.GetValue<string>());
        Assert.False(h.Diagnostic["positionPending"]!.GetValue<bool>());
        var view = h.Vm.Current;
        h.Player.Resume(); h.Pump();
        Assert.Equal("not-in-current-view", h.Diagnostic["result"]!.GetValue<string>());
        Assert.Same(view, h.Vm.Current); Assert.Single(h.Rows);
    });

    [Fact]
    public Task FilterSortAndFolderPreferencesArePreservedAndMissingSongDoesNotNavigate() => Run(() =>
    {
        using var h = new Harness();
        var tagged = h.Track(230);
        h.App.Db.SetTags([(tagged.TrackKey, "MOOD", "SPORT", "MANUAL")]);
        h.App.Library.RebuildSnapshot(); h.Wait(() => h.App.Library.Snapshot.Tags.ContainsKey(tagged.TrackKey));
        h.Vm.ToggleTag("SPORT"); h.Wait(() => !h.Vm.ComputingRows && h.Rows.Count == 1);
        var current = h.Vm.Current; var source = h.Vm.CurrentSource;
        var sort = h.Vm.Sort; var direction = h.Vm.SortDescending;
        var ui = new UiState(); h.Window.SaveLayout(ui);
        var nodes = h.Vm.Nav.Select(n => (n.Key, n.IsExpanded)).ToArray();
        h.Tools.Call("play_tracks", new() { ["trackIds"] = new JsonArray(h.Track(1).Id) });
        h.Ready();
        Assert.Equal("not-in-current-view", h.Diagnostic["result"]!.GetValue<string>());
        Assert.Same(current, h.Vm.Current); Assert.Equal(source, h.Vm.CurrentSource);
        Assert.Equal(sort, h.Vm.Sort); Assert.Equal(direction, h.Vm.SortDescending);
        Assert.Contains("SPORT", h.Vm.TagFilter); Assert.Single(h.Rows);
        Assert.Equal(nodes, h.Vm.Nav.Select(n => (n.Key, n.IsExpanded)));
        var after = new UiState(); h.Window.SaveLayout(after);
        Assert.Equal(ui.CollapsedFolders, after.CollapsedFolders);
    });

    [Fact]
    public Task OpenQueueRevealsOnPlaybackAndClearsStaleRowsAndCallbacksOnClearOrClose() => Run(() =>
    {
        using var h = new Harness();
        h.Window.PlayFrom(h.Track(1)); h.Ready();
        var anchor = h.Window.FindControl<Button>("QueueButton")!;
        var flyout = QueueFlyout.Show(h.Window, h.App, anchor); h.Pump();
        Assert.True(h.QueueDiagnostic["currentFullyVisible"]!.GetValue<bool>());
        h.Tools.Call("edit_queue", new() { ["action"] = "jump", ["index"] = 230 });
        h.Ready();
        Assert.True(h.QueueDiagnostic["currentFullyVisible"]!.GetValue<bool>());
        Assert.InRange(Math.Abs(h.QueueDiagnostic["centerErrorY"]!.GetValue<double>()), 0, .75);
        var scroll = ((Control)flyout.Content!).GetVisualDescendants().OfType<ScrollViewer>().Single();
        scroll.Offset = default; h.Pump(); var version = h.QueueDiagnostic["requestVersion"]!.GetValue<long>();
        h.Player.SetRepeat(RepeatMode.All); h.Pump();
        Assert.Equal(version, h.QueueDiagnostic["requestVersion"]!.GetValue<long>());
        Assert.Equal(0, scroll.Offset.Y);
        h.Player.Resume(); h.Pump();
        Assert.True(h.QueueDiagnostic["currentFullyVisible"]!.GetValue<bool>());
        h.Tools.Call("edit_queue", new() { ["action"] = "jump", ["index"] = 259 }); h.Ready();
        Assert.True(h.QueueDiagnostic["currentFullyVisible"]!.GetValue<bool>());
        Assert.Equal("clamped", h.QueueDiagnostic["result"]!.GetValue<string>());
        Assert.InRange(Math.Abs(scroll.Offset.Y - (scroll.Extent.Height - scroll.Viewport.Height)), 0, .75);
        h.App.QueueVisibility!.Request(h.Player.CurrentTrack!.Id);
        h.Player.Queue.Clear(); h.Pump();
        Assert.False(h.QueueDiagnostic["containsCurrent"]!.GetValue<bool>());
        Assert.False(h.QueueDiagnostic["positionPending"]!.GetValue<bool>());
        h.App.QueueVisibility!.Request(h.Track(230).Id);
        flyout.Hide(); h.Pump(); Assert.Null(h.App.QueueVisibility);
        Assert.False(h.QueueDiagnostic["created"]!.GetValue<bool>());
    });

    [Fact]
    public Task DiagnosticsNeverCreateWindowsAndClosedCallbacksDoNotRetainControls() => Run(() =>
    {
        using var h = new Harness();
        var before = h.Version;
        for (var i = 0; i < 5; i++)
        {
            Assert.False(h.QueueDiagnostic["created"]!.GetValue<bool>());
            Assert.Equal(before, h.Version);
            Assert.False(h.Player.EngineReady);
        }
        Assert.Equal(21, h.Tools.Catalog.Count);
        foreach (var name in new[] { "play_tracks", "playback", "edit_queue" })
            Assert.Contains("reveal", h.Tools.Catalog.Single(t => t!["name"]!.GetValue<string>() == name)!["description"]!.GetValue<string>());
        var schema = h.Tools.Catalog.Single(t => t!["name"]!.GetValue<string>() == "get_diagnostics")!["inputSchema"]!;
        Assert.Empty(schema["properties"]!.AsObject()); Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        h.Window.PlayFrom(h.Track(230)); h.Ready();
        h.App.SongVisibility!.Request(h.Player.CurrentTrack!.Id);
        h.Window.Close(); h.Pump();
        Assert.False(h.Diagnostic["created"]!.GetValue<bool>());
    });

    static async Task Run(Action test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(test, CancellationToken.None);
    }

    internal sealed class Harness : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "jeek-visibility-" + Guid.NewGuid());
        readonly OutputModeSwitchTests.FakeEndpointVolume _endpoint = new();
        readonly List<OutputModeSwitchTests.FakeEngine> _engines = [];
        readonly Track[] _tracks;
        public AppServices App { get; }
        public MainWindow Window { get; }
        public MainViewModel Vm => Window.ViewModel;
        public PlayerService Player => App.Player;
        public OutputModeSwitchTests.FakeEngine Engine => _engines[^1];
        public DataGrid Table => Window.FindControl<DataGrid>("Table")!;
        public DataGridRowsPresenter Viewport => Table.GetVisualDescendants().OfType<DataGridRowsPresenter>().Single();
        public IReadOnlyList<SongRow> Rows => Vm.Rows;
        public IReadOnlyList<DataGridRow> Realized => Table.GetVisualDescendants().OfType<DataGridRow>().ToArray();
        public McpTools Tools { get; }
        public JsonNode Diagnostic => Tools.Call("get_diagnostics", new())["songVisibility"]!;
        public JsonNode QueueDiagnostic => Tools.Call("get_diagnostics", new())["queueVisibility"]!;
        public long Version => Diagnostic["requestVersion"]!.GetValue<long>();
        public Harness()
        {
            Directory.CreateDirectory(_root);
            var settings = new SettingsStore(Path.Combine(_root, "settings.json"));
            settings.Current.SongSort = SongSort.FileName; settings.Current.SortDescending = false;
            App = new AppServices(settings, new LibraryDb(":memory:"), source => new PlayerService(source,
                options => { var e = new OutputModeSwitchTests.FakeEngine(options); _engines.Add(e); return e; }, _endpoint));
            Tools = new McpTools(App);
            Assert.False(Diagnostic["created"]!.GetValue<bool>());
            var folder = App.Db.AddFolder("fake-volume", "\\visibility\\", _root + "\\", "Visibility");
            using (var tx = App.Db.Begin())
            {
                for (var i = 0; i < 260; i++)
                {
                    var file = $"song {i:D3}.flac";
                    File.WriteAllBytes(Path.Combine(_root, file), []);
                    App.Db.ReplaceFile(tx, folder, "", file, [new Track { FolderId = folder, TrackKey = file, Dir = "", FileName = file,
                        Title = $"song {i:D3}", Artists = ["Fake artist"], ArtistDisplay = "Fake artist", DurationMs = 120000 }]);
                }
                tx.Commit();
            }
            Window = new MainWindow(App) { ExitRequested = true, Width = 1100, Height = 600 };
            Window.Styles.Add(new StyleInclude(new Uri("avares://JeekMusic/")) { Source = new Uri("avares://JeekMusic/UI/Styles.axaml") });
            Vm.InitialTarget = new NavTarget.Folder(folder, "");
            App.Library.RebuildSnapshot(); Window.Show();
            Wait(() => Rows.Count == 260 && !Vm.ComputingRows);
            _tracks = App.Library.Snapshot.Tracks.OrderBy(t => t.FileName, StringComparer.Ordinal).ToArray();
            ScrollTo(0);
        }
        public Track Track(int index) => _tracks[index];
        public void Pump() { for (var i = 0; i < 8; i++) { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
        public void Ready() { Engine.Emit(Mpv.EventId.FileLoaded); Engine.Emit(Mpv.EventId.AudioReconfig); Pump(); }
        public void Wait(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            do { Pump(); if (condition()) return; Thread.Yield(); } while (DateTime.UtcNow < until);
            Assert.Fail("Timed out waiting for headless rows/layout");
        }
        public void ScrollTo(int index) { Table.ScrollIntoView(Rows[index], null); Pump(); }
        public double TopRowY(int index) => Realized.Single(r => r.DataContext is SongRow row && row.Track?.Id == Track(index).Id).TranslatePoint(default, Viewport)!.Value.Y;
        public long FirstVisible() => Realized.Where(r => r.DataContext is SongRow && r.TranslatePoint(default, Viewport) is { } p &&
            p.Y + r.Bounds.Height > .5 && p.Y < Viewport.Bounds.Height - .5).OrderBy(r => r.TranslatePoint(default, Viewport)!.Value.Y)
            .Select(r => ((SongRow)r.DataContext!).Track!.Id).First();
        public void AssertVisible(int index)
        {
            Pump();
            var row = Assert.Single(Realized, r => r.DataContext is SongRow song && song.Track?.Id == Track(index).Id);
            var y = row.TranslatePoint(default, Viewport)!.Value.Y;
            Assert.True(y >= -.5 && y + row.Bounds.Height <= Viewport.Bounds.Height + .5,
                $"song {index}: y={y}, height={row.Bounds.Height}, viewport={Viewport.Bounds.Height}, {Diagnostic}");
            Assert.True(Diagnostic["currentFullyVisible"]!.GetValue<bool>());
            Assert.False(Diagnostic["positionPending"]!.GetValue<bool>());
        }
        public void AssertCentered(int index)
        {
            AssertVisible(index);
            Assert.InRange(Math.Abs(Diagnostic["centerErrorY"]!.GetValue<double>()), 0, .75);
            Assert.Equal("center", Diagnostic["alignment"]!.GetValue<string>());
        }
        public void Dispose()
        {
            Vm.Player.ProgressVisible = false; Window.Close(); Pump(); App.Dispose();
            Assert.Empty(_endpoint.Writes);
            Directory.Delete(_root, recursive: true);
        }
    }
}
