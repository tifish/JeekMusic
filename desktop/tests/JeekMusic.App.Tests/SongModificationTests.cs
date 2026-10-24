using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using JeekMusic.Automation;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.App.Tests;

// Only cached metadata, Headless controls, memory DB and injected fake audio.
[Collection("Avalonia UI")]
public sealed class SongModificationTests
{
    static readonly long Date = new DateTime(2026, 10, 24, 8, 30, 0, DateTimeKind.Utc).Ticks;
    static Track Song(long id, string name, long mtime, int? cue = null, int? track = null) => new()
    {
        Id = id, FolderId = 1, TrackKey = "song" + id, Dir = "", FileName = name, SortFile = name,
        Title = name, ArtistDisplay = "", Mtime = mtime, CueTrackNo = cue, TrackNo = track,
    };
    static long[] Ids(IEnumerable<Track> tracks) => tracks.Select(t => t.Id).ToArray();

    [Fact]
    public void MissingPreferenceSurvivesSaveAndLegacyExplicitChoicesArePreserved()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new SettingsStore(path);
            Assert.Equal(SongSort.Modified, store.Current.SongSort);
            Assert.False(store.Current.HasSongSortPreference);
            store.Current.Language = "en"; store.WriteNow();
            Assert.False(JsonNode.Parse(File.ReadAllText(path))!.AsObject().ContainsKey("songSort"));
            Assert.False(new SettingsStore(path).Current.HasSongSortPreference);
            foreach (var value in Enum.GetValues<SongSort>())
            {
                File.WriteAllText(path, new JsonObject { ["songSort"] = value.ToString(), ["sortDescending"] = true }.ToJsonString());
                store = new SettingsStore(path);
                Assert.True(store.Current.HasSongSortPreference);
                Assert.Equal(value, store.Current.SortFor(orderedView: true));
                store.WriteNow();
                var restored = new SettingsStore(path).Current;
                Assert.Equal(value, restored.SongSort); Assert.True(restored.SortDescending);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RealDatesSortBeforeUnknownInBothDirectionsAndCueTiesStayInTrackOrder()
    {
        List<Track> tracks = [Song(9, "9.flac", long.MaxValue), Song(8, "8.flac", -1), Song(7, "7.flac", 0),
            Song(3, "album.flac", Date, 2), Song(2, "album.flac", Date, 1), Song(1, "old.flac", Date - 1), Song(4, "new.flac", Date + 1)];
        Assert.Equal([4, 2, 3, 1, 7, 8, 9], Ids(ListBuilder.Sort(tracks.ToList(), ListKind.Folder, SongSort.Modified, false)));
        Assert.Equal([1, 2, 3, 4, 7, 8, 9], Ids(ListBuilder.Sort(tracks.ToList(), ListKind.Folder, SongSort.Modified, true)));
        foreach (var value in new[] { 0L, -1, DateTime.UnixEpoch.Ticks, DateTime.FromFileTimeUtc(0).Ticks, long.MaxValue })
        {
            Assert.Equal("—", Text.Modified(value)); Assert.Null(FileModified.Utc(value));
        }
        Assert.Equal(new DateTime(Date, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture), Text.Modified(Date));
        Assert.Equal(Text.Modified(Date), Text.Modified(Date + 1)); // same display still sorts by real ticks
    }

    [Fact]
    public void DefaultsKeepOrderedViewsWhileManualSortAndMcpOverridesRemainEffective()
    {
        var settings = new AppSettings();
        Assert.Equal(SongSort.Modified, settings.SortFor(false));
        Assert.Equal(SongSort.FileName, settings.SortFor(true));
        var first = Song(1, "z.flac", Date, track: 1); var second = Song(2, "a.flac", Date + 1, track: 2);
        Assert.Equal([1, 2], Ids(ListBuilder.Sort([first, second], ListKind.Playlist, settings.SortFor(true), false)));
        Assert.Equal([1, 2], Ids(ListBuilder.Sort([second, first], ListKind.Album, settings.SortFor(true), false)));
        Assert.Equal([2, 1], Ids(McpTools.SortSearch([first, second], new(), settings)));
        settings.SongSort = SongSort.FileName;
        Assert.Equal([2, 1], Ids(McpTools.SortSearch([first, second], new(), settings)));
        Assert.Equal([1, 2], Ids(McpTools.SortSearch([first, second], new() { ["sort"] = "Modified", ["reverse"] = true }, settings)));
        Assert.Equal(SongSort.FileName, settings.SongSort);
    }

    [Fact]
    public async Task OldHiddenColumnIsRevealedOnceThenWidthOrderAndExplicitHidingPersist()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(() =>
        {
            using var h = new Harness();
            var grid = h.Window.FindControl<DataGrid>("Table")!;
            var state = new UiState { Columns = [new() { Tag = "Modified", Width = 130, Order = 0, Visible = false },
                new() { Tag = "Title", Width = 240, Order = 1 }, new() { Tag = "removed", Order = 2 }] };
            h.Window.RestoreLayout(state);
            var column = grid.Columns.Single(c => c.Tag is "Modified");
            Assert.True(column.IsVisible); Assert.True(column.Width.Value >= 154); Assert.Equal(0, column.DisplayIndex);
            column.IsVisible = false; column.Width = new DataGridLength(190);
            h.Window.SaveLayout(state);
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, UiStateJson.Default.UiState), UiStateJson.Default.UiState)!;
            Assert.Equal(1, state.SongColumnsVersion);
            h.Window.RestoreLayout(state);
            Assert.False(column.IsVisible); Assert.Equal(0, column.DisplayIndex);
            Assert.Equal(190, column.Width.Value);
            h.Window.RestoreLayout(new UiState()); Assert.True(column.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task McpSearchSortsBeforePagingAndExposesCachedDatesWithoutAudioOrScan()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(() =>
        {
            using var h = new Harness();
            var root = h.App.Db.AddFolder("mock", "\\music\\", "C:\\mock-music\\", "Music");
            using (var tx = h.App.Db.Begin())
            {
                foreach (var song in new[] { Song(1, "a.flac", Date), Song(2, "b.flac", Date + 1), Song(3, "c.flac", long.MaxValue) })
                    h.App.Db.ReplaceFile(tx, root, "", song.FileName, [song]);
                tx.Commit();
            }
            h.App.Library.RebuildSnapshot(); Dispatcher.UIThread.RunJobs();
            var tools = new McpTools(h.App);
            var page = tools.Call("search_tracks", new() { ["limit"] = 1 });
            Assert.Equal(3, page["total"]!.GetValue<int>());
            var item = page["items"]![0]!;
            Assert.Equal("b.flac", item["fileName"]!.GetValue<string>());
            Assert.Equal(Date + 1, item["mtimeUtcTicks"]!.GetValue<long>());
            Assert.EndsWith("Z", item["modifiedUtc"]!.GetValue<string>());
            Assert.Equal(Text.Modified(Date + 1), item["modifiedLocal"]!.GetValue<string>());
            var unknown = tools.Call("search_tracks", new() { ["sort"] = "Modified", ["reverse"] = true })["items"]!.AsArray().Last()!;
            Assert.Equal("c.flac", unknown["fileName"]!.GetValue<string>());
            Assert.Null(unknown["modifiedUtc"]); Assert.Equal("—", unknown["modifiedLocal"]!.GetValue<string>());
            Assert.False(h.App.Settings.Current.HasSongSortPreference);
            Assert.Empty(h.App.Player.Queue.Items); Assert.False(h.App.Library.Scanning);
            h.Window.ViewModel.Navigate(new NavTarget.Folder(root, ""));
            h.Wait(() => h.Window.ViewModel.Rows.Count == 3);
            Assert.Equal("b.flac", h.Window.ViewModel.Rows[0].Track!.FileName);
            var modifiedColumn = h.Window.FindControl<DataGrid>("Table")!.Columns.Single(c => c.Tag is "Modified");
            Assert.EndsWith(" ↓", modifiedColumn.Header!.ToString());
            h.Window.ViewModel.SetSort(SongSort.Modified);
            h.Wait(() => h.Window.ViewModel.Rows[0].Track!.FileName == "a.flac");
            Assert.Equal("c.flac", h.Window.ViewModel.Rows[2].Track!.FileName);
            Assert.EndsWith(" ↑", modifiedColumn.Header!.ToString());
            h.Window.ViewModel.SetSort(SongSort.Title);
            Assert.True(h.App.Settings.Current.HasSongSortPreference);
            Assert.False(h.App.Settings.Current.SortDescending);
            h.Window.ViewModel.SetSort(SongSort.Title); Assert.True(h.App.Settings.Current.SortDescending);
        }, CancellationToken.None);
    }

    sealed class Harness : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        readonly OutputModeSwitchTests.FakeEndpointVolume endpoint = new();
        readonly List<OutputModeSwitchTests.FakeEngine> engines = [];
        public AppServices App { get; }
        public MainWindow Window { get; }
        public Harness()
        {
            App = new AppServices(new SettingsStore(path), new LibraryDb(":memory:"), source =>
                new PlayerService(source, options => { var e = new OutputModeSwitchTests.FakeEngine(options); engines.Add(e); return e; }, endpoint));
            Window = new MainWindow(App) { ExitRequested = true };
        }
        public void Dispose()
        {
            Window.ViewModel.Player.ProgressVisible = false; Window.Close(); App.Dispose();
            Assert.Empty(endpoint.Writes); Assert.Empty(engines); File.Delete(path);
        }
        public void Wait(Func<bool> ready)
        {
            var until = DateTime.UtcNow.AddSeconds(3);
            do { Dispatcher.UIThread.RunJobs(); if (ready()) return; Thread.Yield(); } while (DateTime.UtcNow < until);
            Assert.Fail("Timed out waiting for simulated song rows");
        }
    }
}
