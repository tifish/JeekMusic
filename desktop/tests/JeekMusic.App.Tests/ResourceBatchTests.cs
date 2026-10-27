using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using JeekMusic.Automation;
using JeekMusic.Library;
using JeekMusic.Online;
using JeekMusic.Settings;
using JeekMusic.UI;
using JeekMusic.UI.ViewModels;
using SkiaSharp;

namespace JeekMusic.App.Tests;

// Only isolated fixture files, in-memory DB and fake audio endpoints. No online sources or real library writes.
[Collection("Avalonia UI")]
public sealed class ResourceBatchTests
{
    [Theory]
    [InlineData(DownloadKind.Lyrics)] [InlineData(DownloadKind.Covers)]
    public Task ListMenuCapturesAllVirtualizedRowsBeforeConfirmationAndKeepsSelectedActions(DownloadKind kind) => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        var feature = Configure(h);
        h.Table.SelectedItems.Add(h.Rows[1]);
        Assert.InRange(h.Realized.Count, 1, 40);
        var menu = h.Window.CreateListMenu();
        var all = Item(menu, kind == DownloadKind.Covers ? "获取全部封面" : "获取全部歌词");
        Assert.True(all.IsEnabled); Assert.True(Item(menu, "获取全部封面").IsEnabled);
        var captured = h.Rows.Select(r => r.Track).ToArray();
        all.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        h.Pump();
        var job = feature.Batches.Current!;
        Assert.Equal("preparing", job.State); Assert.Equal("current_list", job.Scope);
        Assert.Equal(kind, job.Kind);
        Assert.Equal(captured, job.Tracks); Assert.Equal(260, job.Tracks.Length);
        Assert.False(Item(h.Window.CreateListMenu(), "获取全部歌词").IsEnabled);
        var confirm = h.Window.OwnedWindows.Single();
        h.Vm.Navigate(new NavTarget.Search("song 001"));
        h.Wait(() => !h.Vm.ComputingRows && h.Rows.Count == 1);
        Assert.Equal(260, job.Tracks.Length);
        confirm.Close(); h.Pump();
        Assert.False(feature.Batches.Busy); Assert.Equal("cancelled", job.State);

        // The existing selected-track entry still targets exactly the one supplied selection.
        var selected = new ContextMenu();
        typeof(MainWindow).GetMethod("AddOnlineActions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(h.Window, [selected, new[] { h.Track(1) }]);
        var one = selected.Items.OfType<MenuItem>().Single(i => Equals(i.Header, L.T("获取缺失歌词")));
        one.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        h.Wait(() => !feature.Batches.Busy);
        Assert.Single(feature.Batches.Current!.Tracks);
        Assert.Equal(h.Track(1).Id, feature.Batches.Current.Tracks[0].Id);
        Assert.Equal("selected", feature.Batches.Current.Scope);
        await Task.CompletedTask;
    });

    [Fact]
    public Task CapturesFilteredSourceAndSearchCapAndDisablesEmptyOrComputingLists() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        Configure(h);
        h.Vm.Navigate(new NavTarget.Search("song"));
        h.Wait(() => !h.Vm.ComputingRows && h.Rows.Count == 200);
        Assert.Equal(200, h.Vm.CaptureResourceTracks().Length);
        h.App.Db.SetTags([(h.Track(230).TrackKey, "MOOD", "SPORT", "MANUAL")]);
        h.App.Library.RebuildSnapshot(); h.Wait(() => h.App.Library.Snapshot.Tags.ContainsKey(h.Track(230).TrackKey));
        h.Vm.Navigate(new NavTarget.Folder(h.Track(0).FolderId, ""));
        h.Vm.ToggleTag("SPORT");
        Assert.Empty(h.Vm.CaptureResourceTracks());
        Assert.False(Item(h.Window.CreateListMenu(), "获取全部封面").IsEnabled);
        h.Wait(() => !h.Vm.ComputingRows && h.Rows.Count == 1);
        Assert.Equal(h.Track(230).Id, Assert.Single(h.Vm.CaptureResourceTracks()).Id);
        h.Vm.Navigate(new NavTarget.Search("does not exist"));
        h.Wait(() => !h.Vm.ComputingRows && h.Rows.Count == 0);
        Assert.False(Item(h.Window.CreateListMenu(), "获取全部封面").IsEnabled);
        Assert.False(Item(h.Window.CreateListMenu(), "获取全部歌词").IsEnabled);
        await Task.CompletedTask;
    });

    [Fact]
    public Task CapturesFolderRecursionArtistAlbumAndPlaylistMembership() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        Configure(h);
        var folder = h.Track(0).FolderId;
        using (var tx = h.App.Db.Begin())
        {
            h.App.Db.ReplaceFile(tx, folder, "child\\", "child.flac", [new Track
            {
                TrackKey = "resource-child", FolderId = folder, Dir = "child\\", FileName = "child.flac", Title = "child",
                ArtistDisplay = "Fake artist", Artists = ["Fake artist"], Album = "Child album", AlbumKey = "resource-child-album"
            }]);
            tx.Commit();
        }
        h.App.Library.RebuildSnapshot(); h.Wait(() => h.App.Library.Snapshot.Tracks.Count == 261 && !h.Vm.ComputingRows);
        Assert.Equal(260, h.Vm.CaptureResourceTracks().Length);
        h.Vm.IncludeSubfolders = true;
        h.Wait(() => !h.Vm.ComputingRows && h.Rows.Count == 261);
        Assert.Equal(261, h.Vm.CaptureResourceTracks().Length);
        h.Vm.Navigate(new NavTarget.Artists(h.App.Library.Snapshot.Artists.Single(a => a.Name == "Fake artist").NameKey));
        h.Wait(() => !h.Vm.ComputingRows && h.Rows.Count == 261);
        Assert.Equal(261, h.Vm.CaptureResourceTracks().Length);
        h.Vm.Navigate(new NavTarget.Album("resource-child-album"));
        h.Wait(() => !h.Vm.ComputingRows && h.Rows.Count == 1);
        Assert.Equal("resource-child", Assert.Single(h.Vm.CaptureResourceTracks()).TrackKey);
        var playlist = h.App.Playlists.Create("Resource fixture");
        h.App.Db.SetPlaylistItems(playlist, [(h.Track(0).TrackKey, null), ("unmatched", "missing.flac"),
            (h.Track(1).TrackKey, null), (h.Track(0).TrackKey, null)]);
        h.App.Playlists.Invalidate(playlist);
        h.Vm.Navigate(new NavTarget.Playlist(playlist));
        h.Wait(() => !h.Vm.ComputingRows && h.Rows.Count == 4);
        Assert.Equal(new[] { h.Track(0).Id, h.Track(1).Id, h.Track(0).Id }, h.Vm.CaptureResourceTracks().Select(t => t.Id));
        await Task.CompletedTask;
    });

    [Fact]
    public Task McpSharesFrozenFullListJobGuardCancellationAndDiagnostics() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        var feature = Configure(h);
        h.Table.SelectedItems.Add(h.Rows[1]);
        var request = new JsonObject { ["action"] = "start", ["kind"] = "lyrics", ["scope"] = "current_list" };
        var result = h.Tools.Call("fetch_resources", request);
        Assert.Equal(260, result["targetSongCount"]!.GetValue<int>());
        Assert.Throws<InvalidOperationException>(() => h.Tools.Call("fetch_resources", request));
        h.Vm.Navigate(new NavTarget.Search("song 001"));
        h.Wait(() => !feature.Batches.Busy && !h.Vm.ComputingRows);
        var state = h.Tools.Call("fetch_resources", new() { ["action"] = "status" });
        Assert.Equal("completed", state["state"]!.GetValue<string>());
        Assert.Equal(260, state["processedUnits"]!.GetValue<int>());
        Assert.Equal(0, state["savedUnits"]!.GetValue<int>());
        Assert.Equal(1, state["currentList"]!["availableSongCount"]!.GetValue<int>());
        Assert.Equal(state["state"]!.ToString(), h.Tools.Call("get_diagnostics", new())["resourceBatch"]!["state"]!.ToString());

        var job = feature.Batches.Reserve([h.Track(0)], DownloadKind.Lyrics, "selected", "selection");
        Assert.Throws<InvalidOperationException>(() => h.Tools.Call("fetch_resources", request));
        h.Tools.Call("fetch_resources", new() { ["action"] = "cancel" });
        await feature.Batches.Run(job);
        Assert.Equal("cancelled", job.State); Assert.Equal(0, job.Done);
        Assert.True(Item(h.Window.CreateListMenu(), "获取全部歌词").IsEnabled);
        Assert.Throws<ArgumentException>(() => h.Tools.Call("fetch_resources", new() { ["action"] = "status", ["kind"] = "covers" }));
        h.App.Settings.Current.OnlineLyrics = false;
        Assert.Throws<InvalidOperationException>(() => h.Tools.Call("fetch_resources", request));
        Assert.False(h.App.Settings.Current.OnlineLyrics);
        Assert.Throws<ArgumentException>(() => h.Tools.Call("fetch_resources", new() { ["action"] = "start", ["kind"] = "covers", ["scope"] = "current_list", ["trackIds"] = new JsonArray(1) }));
    });

    [Fact]
    public Task RunnerDeduplicatesAlbumsTracksAndCountsFailuresWithoutBlockingOrReentry() => Run(async () =>
    {
        var tracks = Enumerable.Range(0, 3000).Select(i => new Track { Id = i, TrackKey = "batch-test-" + i,
            FileName = i + ".flac", Dir = "", Title = i.ToString(), ArtistDisplay = "", AlbumKey = "album-" + i / 2 }).ToList();
        var calls = new List<Track>();
        var runner = new ResourceBatch((track, _, _) =>
        { calls.Add(track); return track.Id == 4 ? Task.FromException<bool>(new IOException("fixture failure")) : Task.FromResult(track.Id == 0); }, CancellationToken.None);
        var job = runner.Reserve(tracks, DownloadKind.Covers, "current_list", "fixture");
        tracks.Clear(); // Caller mutation cannot change an already reserved job.
        Assert.Throws<InvalidOperationException>(() => runner.Reserve(job.Tracks, DownloadKind.Covers, "selected", ""));
        var run = runner.Run(job);
        var uiResponsive = false;
        Dispatcher.UIThread.Post(() => uiResponsive = runner.Busy);
        await run;
        Assert.True(uiResponsive);
        Assert.Equal(3000, job.Tracks.Length); Assert.Equal(1500, job.Total);
        Assert.Equal(1500, job.Done); Assert.Equal(1, job.Saved); Assert.Equal(1, job.Failed);
        Assert.Equal(1500, calls.Select(t => t.AlbumKey).Distinct().Count());
        Assert.Equal("completed", job.State); Assert.False(runner.Busy);
        Assert.Throws<ArgumentException>(() => runner.Reserve([], DownloadKind.Lyrics, "current_list", ""));
        var replacement = runner.Reserve([job.Tracks[0]], DownloadKind.Lyrics, "selected", "replacement");
        runner.Cancel(job); // A closing old progress window must never cancel its successor.
        Assert.Equal("preparing", replacement.State);
        await runner.Run(replacement);
        Assert.Equal("completed", replacement.State);

        var gate = new TaskCompletionSource<bool>();
        var blocked = new ResourceBatch(async (_, _, _) => await gate.Task, CancellationToken.None);
        var cancelled = blocked.Reserve(job.Tracks, DownloadKind.Lyrics, "selected", "fixture");
        var pending = blocked.Run(cancelled);
        while (cancelled.State == "preparing") await Task.Delay(1);
        blocked.Cancel();
        Assert.True(blocked.Busy); Assert.Equal("cancelling", cancelled.State);
        gate.SetResult(false);
        await pending;
        Assert.False(blocked.Busy); Assert.Equal("cancelled", cancelled.State); Assert.Equal(1, cancelled.Done);
    });

    [Theory]
    [InlineData(CoverSaveMode.HiddenFolder)] [InlineData(CoverSaveMode.Sidecar)] [InlineData(CoverSaveMode.Embed)]
    public Task LocalResourcesKeepFetchAndSavePolicy(CoverSaveMode mode) => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        var feature = Configure(h);
        var albumKey = "resource-test-" + Guid.NewGuid();
        using (var tx = h.App.Db.Begin())
        {
            foreach (var original in new[] { h.Track(0), h.Track(1) })
            {
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tag-writer-silence.flac"), h.App.Library.PathOf(original)!, true);
                h.App.Db.ReplaceFile(tx, original.FolderId, "", original.FileName, [new Track
                {
                    TrackKey = albumKey + original.Id, FolderId = original.FolderId, FileName = original.FileName,
                    Dir = "", Title = original.Title, ArtistDisplay = "Fixture artist", Album = "Fixture album", AlbumKey = albumKey
                }]);
            }
            tx.Commit();
        }
        h.App.Library.RebuildSnapshot(); h.Wait(() => h.App.Library.Snapshot.Albums.ContainsKey(albumKey));
        var album = h.App.Library.Snapshot.Albums[albumKey];
        var track = album[0];
        var path = h.App.Library.PathOf(track)!;
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:00.00]Local fixture lyrics");
        var before = File.ReadAllBytes(path);
        h.App.Settings.Current.LyricsSaveLocation = LyricsSaveLocation.Embed;
        // Existing local/cache lyrics are skipped even when the configured destination is embedding.
        Assert.False(await feature.Fetch(track, DownloadKind.Lyrics, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(path));
        using var bitmap = new SKBitmap(8, 8); bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap); using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "cover.jpg"), jpeg.ToArray());
        h.App.Settings.Current.CoverSaveMode = mode;
        var job = feature.Batches.Reserve(album, DownloadKind.Covers, "track_ids", "fixture");
        await feature.Batches.Run(job);
        Assert.Equal(1, job.Total); Assert.Equal(1, job.Done); Assert.Equal(0, job.Failed);
        Assert.Null(h.App.Db.CoverMissAt(track.TrackKey));
        Assert.Equal(mode, h.App.Settings.Current.CoverSaveMode);
        if (mode == CoverSaveMode.HiddenFolder) Assert.Equal(before, File.ReadAllBytes(path));
        if (mode == CoverSaveMode.Sidecar) Assert.All(album, t => Assert.True(File.Exists(Path.ChangeExtension(h.App.Library.PathOf(t)!, ".jpg"))));
        if (mode == CoverSaveMode.Embed)
        {
            Assert.All(album, t => Assert.True(TagReader.Read(h.App.Library.PathOf(t)!).HasCover));
            h.App.Library.RebuildSnapshot(); h.Wait(() => h.App.Library.Snapshot.ById[track.Id].HasEmbeddedCover);
            track = h.App.Library.Snapshot.ById[track.Id];
        }
        var savedBytes = File.ReadAllBytes(path);
        Assert.False(await feature.Fetch(track, DownloadKind.Covers, CancellationToken.None));
        Assert.Equal(savedBytes, File.ReadAllBytes(path));
        LibraryRefresh.Stop();
    });

    static OnlineFeature Configure(PlayingVisibilityTests.Harness h)
    {
        h.App.Settings.Current.OnlineLyrics = h.App.Settings.Current.OnlineCovers = true;
        h.App.Settings.Current.CoverSources = []; h.App.Settings.Current.LyricsSources = [];
        foreach (var row in h.Rows) h.App.Db.CacheLyrics(row.Track!.TrackKey, "[00:00.00]Fixture", true, "fixture");
        return new OnlineFeature(h.App);
    }

    static MenuItem Item(MenuFlyout menu, string name) => menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, L.T(name)));
    static async Task Run(Func<Task> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(async () => { await test(); return 0; }, CancellationToken.None);
    }
}
