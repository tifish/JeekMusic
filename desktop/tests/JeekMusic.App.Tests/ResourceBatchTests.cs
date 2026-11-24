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
        var lrcPath = Path.ChangeExtension(path, ".lrc");
        File.WriteAllText(lrcPath, "[00:00.00]Local fixture lyrics");
        var before = File.ReadAllBytes(path);
        h.App.Settings.Current.LyricsSaveLocation = LyricsSaveLocation.Sidecar;
        // Existing same-name .lrc is used directly without modifying audio when save location is not Embed.
        Assert.False(await feature.Fetch(track, DownloadKind.Lyrics, CancellationToken.None));
        Assert.True(File.Exists(lrcPath));
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

    [Theory]
    [InlineData(LyricsSaveLocation.Sidecar)]
    [InlineData(LyricsSaveLocation.HiddenFolder)]
    [InlineData(LyricsSaveLocation.AppCache)]
    public Task ExistingSidecarLyricsUsedDirectlyWithoutEmbeddingWhenNotEmbedMode(LyricsSaveLocation location) => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        var feature = Configure(h);
        var track = PrepareAudioTrack(h, h.Track(0), clearLyricsCache: true);
        var path = h.App.Library.PathOf(track)!;
        var lrcPath = Path.ChangeExtension(path, ".lrc");
        // Seed a stale null lyrics cache and a negative miss record before dropping the sidecar file.
        Assert.Null(h.App.Lyrics.Load(track));
        h.App.Db.CacheLyrics(track.TrackKey, null, false, null);
        File.WriteAllText(lrcPath, "[00:01.00]Direct sidecar lyric");
        var before = File.ReadAllBytes(path);
        h.App.Settings.Current.LyricsSaveLocation = location;
        var notified = 0;
        h.App.Lyrics.Changed += t => { if (t.TrackKey == track.TrackKey) notified++; };

        Assert.False(await feature.Fetch(track, DownloadKind.Lyrics, CancellationToken.None));
        Assert.True(File.Exists(lrcPath));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(1, notified);
        Assert.Equal("Direct sidecar lyric", h.App.Lyrics.Load(track)?.Text.Lines[0].Text);
        Assert.Null(h.App.Db.CachedLyrics(track.TrackKey));
        LibraryRefresh.Stop();
    });

    [Fact]
    public Task ExistingSidecarLyricsEmbedsAndDeletesSidecarInEmbedModeAndSkipsOnRepeat() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        var feature = Configure(h);
        var track = PrepareAudioTrack(h, h.Track(0), clearLyricsCache: true);
        var path = h.App.Library.PathOf(track)!;
        var lrcPath = Path.ChangeExtension(path, ".lrc");
        var mtimeBefore = File.GetLastWriteTimeUtc(path);
        // GBK encoded sidecar + stale null memory cache + stale miss record.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        const string lrcText = "[00:01.00]本地同名歌词\r\n[00:05.00]第二行";
        Assert.Null(h.App.Lyrics.Load(track));
        h.App.Db.CacheLyrics(track.TrackKey, null, false, null);
        File.WriteAllBytes(lrcPath, System.Text.Encoding.GetEncoding(936).GetBytes(lrcText));
        h.App.Settings.Current.LyricsSaveLocation = LyricsSaveLocation.Embed;

        var job = feature.Batches.Reserve([track], DownloadKind.Lyrics, "track_ids", "fixture");
        await feature.Batches.Run(job);

        Assert.Equal(1, job.Saved); Assert.Equal(0, job.Failed);
        Assert.False(File.Exists(lrcPath));
        Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(path));
        Assert.Equal(lrcText.Replace("\r\n", "\n"), TagReader.ReadLyrics(path)?.Replace("\r\n", "\n"));
        // Even with the pre-refresh Track object (HasEmbeddedLyrics == false), LyricsService immediately reads the embedded lyrics.
        var loaded = h.App.Lyrics.Load(track);
        Assert.NotNull(loaded);
        Assert.Equal(L.T("内嵌歌词"), loaded!.Source);
        Assert.Equal("本地同名歌词", loaded.Text.Lines[0].Text);
        Assert.Null(h.App.Db.CachedLyrics(track.TrackKey));
        Assert.False(File.Exists(Lyrics.LyricsService.HiddenPath(h.App.Library.Snapshot.Folder(track.FolderId)!, track)));

        // Fetching again sees embedded lyrics and does not rewrite the audio file.
        var afterFirst = File.ReadAllBytes(path);
        Assert.False(await feature.Fetch(track, DownloadKind.Lyrics, CancellationToken.None));
        Assert.Equal(afterFirst, File.ReadAllBytes(path));

        // External / temporary-folder track (FolderId == 0) also embeds and deletes its same-name .lrc.
        var extDir = Path.Combine(Path.GetTempPath(), "jeekmusic-ext-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extDir);
        try
        {
            var extAudio = Path.Combine(extDir, "external.flac");
            var extLrc = Path.Combine(extDir, "external.lrc");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tag-writer-silence.flac"), extAudio, true);
            File.WriteAllText(extLrc, "[00:02.00]External sidecar lyric");
            var external = h.App.Library.OpenExternal(extAudio);
            Assert.Equal(0, external.FolderId);
            Assert.True(await feature.Fetch(external, DownloadKind.Lyrics, CancellationToken.None));
            Assert.False(File.Exists(extLrc));
            Assert.Equal("[00:02.00]External sidecar lyric", TagReader.ReadLyrics(extAudio));
            Assert.Equal("External sidecar lyric", h.App.Lyrics.Load(external)?.Text.Lines[0].Text);
            Assert.False(await feature.Fetch(external, DownloadKind.Lyrics, CancellationToken.None));
        }
        finally { try { Directory.Delete(extDir, true); } catch { } }
        LibraryRefresh.Stop();
    });

    [Fact]
    public Task SidecarEmbedDefersWhilePlayingAndPreservesFileOnUnsupportedFormatOrCueOrHigherPriority() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        var feature = Configure(h);
        h.App.Settings.Current.LyricsSaveLocation = LyricsSaveLocation.Embed;

        // 1. Playing (in-use) track defers embedding, keeps sidecar until idle, does not duplicate to hidden/cache.
        var playing = PrepareAudioTrack(h, h.Track(0), clearLyricsCache: true);
        var playingPath = h.App.Library.PathOf(playing)!;
        var playingLrc = Path.ChangeExtension(playingPath, ".lrc");
        File.WriteAllText(playingLrc, "[00:02.00]Deferred sidecar");
        bool inUse = true;
        feature.IsFileInUseOverride = t => inUse && t.TrackKey == playing.TrackKey;
        Assert.False(await feature.Fetch(playing, DownloadKind.Lyrics, CancellationToken.None));
        Assert.True(File.Exists(playingLrc));
        Assert.Null(h.App.Db.CachedLyrics(playing.TrackKey));
        Assert.False(File.Exists(Lyrics.LyricsService.HiddenPath(h.App.Library.Snapshot.Folder(playing.FolderId)!, playing)));
        inUse = false;
        await feature.FlushPending();
        Assert.False(File.Exists(playingLrc));
        Assert.Equal("[00:02.00]Deferred sidecar", TagReader.ReadLyrics(playingPath));

        // 2. Unsupported extension keeps sidecar intact and does not copy to hidden/cache.
        var folder = h.App.Library.Snapshot.Folder(playing.FolderId)!;
        var unsupportedPath = Path.Combine(folder.Root, "unsupported.dsf");
        var unsupportedLrc = Path.Combine(folder.Root, "unsupported.lrc");
        File.WriteAllBytes(unsupportedPath, [1, 2, 3, 4]);
        File.WriteAllText(unsupportedLrc, "[00:03.00]Keep sidecar");
        using (var tx = h.App.Db.Begin())
        {
            h.App.Db.ReplaceFile(tx, folder.Id, "", "unsupported.dsf", [new Track
            {
                TrackKey = "unsupported-dsf", FolderId = folder.Id, Dir = "", FileName = "unsupported.dsf",
                Title = "Unsupported", ArtistDisplay = "Fixture"
            }]);
            tx.Commit();
        }
        h.App.Library.RebuildSnapshot(); h.Wait(() => h.App.Library.ByKey("unsupported-dsf") is not null);
        var unsupported = h.App.Library.ByKey("unsupported-dsf")!;
        Assert.False(await feature.Fetch(unsupported, DownloadKind.Lyrics, CancellationToken.None));
        Assert.True(File.Exists(unsupportedLrc));
        Assert.Null(h.App.Db.CachedLyrics(unsupported.TrackKey));
        Assert.False(File.Exists(Lyrics.LyricsService.HiddenPath(folder, unsupported)));

        // 3. CUE track keeps its sidecar untouched.
        var cueTrack = PrepareAudioTrack(h, h.Track(1), clearLyricsCache: true, cueTrackNo: 1, title: "Cue Song");
        var cueLrc = Path.Combine(folder.Root, "01. Cue Song.lrc");
        File.WriteAllText(cueLrc, "[00:04.00]Cue sidecar");
        Assert.False(await feature.Fetch(cueTrack, DownloadKind.Lyrics, CancellationToken.None));
        Assert.True(File.Exists(cueLrc));
        Assert.Null(TagReader.ReadLyrics(h.App.Library.PathOf(cueTrack)!));

        // 4. Higher-priority hidden folder lyrics are not overwritten by older sidecar.
        var manual = PrepareAudioTrack(h, h.Track(2), clearLyricsCache: true);
        var manualPath = h.App.Library.PathOf(manual)!;
        var manualLrc = Path.ChangeExtension(manualPath, ".lrc");
        var hiddenPath = Lyrics.LyricsService.HiddenPath(folder, manual);
        Directory.CreateDirectory(Path.GetDirectoryName(hiddenPath)!);
        File.WriteAllText(hiddenPath, "[00:00.00]Manual replacement");
        File.WriteAllText(manualLrc, "[00:00.00]Older sidecar");
        Assert.False(await feature.Fetch(manual, DownloadKind.Lyrics, CancellationToken.None));
        Assert.True(File.Exists(manualLrc));
        Assert.True(File.Exists(hiddenPath));
        Assert.Null(TagReader.ReadLyrics(manualPath));
        LibraryRefresh.Stop();
    });

    [Fact]
    public Task CombinedCoverAndSidecarLyricsEmbedInOneFlowAndDeleteSidecar() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        var feature = Configure(h);
        var track = PrepareAudioTrack(h, h.Track(0), clearLyricsCache: true);
        var path = h.App.Library.PathOf(track)!;
        var lrcPath = Path.ChangeExtension(path, ".lrc");
        File.WriteAllText(lrcPath, "[00:01.00]Combined sidecar lyric");
        using var bitmap = new SKBitmap(8, 8); bitmap.Erase(SKColors.Orange);
        using var image = SKImage.FromBitmap(bitmap); using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "cover.jpg"), jpeg.ToArray());
        h.App.Settings.Current.CoverSaveMode = CoverSaveMode.Embed;
        h.App.Settings.Current.LyricsSaveLocation = LyricsSaveLocation.Embed;

        await feature.FetchPair(track, covers: true, lyrics: true, CancellationToken.None);

        Assert.False(File.Exists(lrcPath));
        Assert.True(TagReader.Read(path).HasCover);
        Assert.Equal("[00:01.00]Combined sidecar lyric", TagReader.ReadLyrics(path));
        Assert.Null(h.App.Db.CachedLyrics(track.TrackKey));
        LibraryRefresh.Stop();
    });

    static Track PrepareAudioTrack(PlayingVisibilityTests.Harness h, Track original, bool clearLyricsCache = false, int? cueTrackNo = null, string? title = null)
    {
        var key = "sidecar-test-" + Guid.NewGuid();
        var path = h.App.Library.PathOf(original)!;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tag-writer-silence.flac"), path, true);
        using (var tx = h.App.Db.Begin())
        {
            h.App.Db.ReplaceFile(tx, original.FolderId, "", original.FileName, [new Track
            {
                TrackKey = key, FolderId = original.FolderId, FileName = original.FileName, Dir = "",
                Title = title ?? original.Title, ArtistDisplay = "Fixture artist", Album = "Fixture album",
                AlbumKey = key + "-album", CueTrackNo = cueTrackNo
            }]);
            tx.Commit();
        }
        if (clearLyricsCache) h.App.Db.DeleteCachedLyrics(key);
        h.App.Library.RebuildSnapshot();
        h.Wait(() => h.App.Library.ByKey(key) is not null);
        return h.App.Library.ByKey(key)!;
    }

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
