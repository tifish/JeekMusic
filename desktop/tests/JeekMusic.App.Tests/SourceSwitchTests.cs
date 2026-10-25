using Avalonia.Headless;
using Avalonia.Input;
using JeekMusic.Core.Queue;
using JeekMusic.Playback;
using JeekMusic.Library;
using JeekMusic.Settings;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.App.Tests;

// Isolated handoff service: real headless window + injected engine, no client/pipe/native audio.
// Deliberately does not make arbitrary browsing navigation take ownership of the play queue.
[Collection("Avalonia UI")]
public sealed class SourceSwitchTests
{
    static QueueContext Context(int id) => new(new QueueSource.Playlist(id), $"Playlist {id}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HandoffKeepsCurrentStreamPositionPauseSelectionAndReplacesOldPreload(bool paused) => Run(h =>
    {
        if (paused) { h.Player.Pause(); h.Pump(); }
        h.Table.SelectedItems.Add(h.Rows[1]); h.Table.SelectedItems.Add(h.Rows[3]); h.Pump();
        h.Engine.Properties["time-pos"] = "42.125";
        var current = h.Player.CurrentTrack; var engine = h.Engine;
        var source = h.Vm.Current; var reveal = h.Version;
        var replacements = Replacements(h);
        var stale = engine.GetLong("playlist/1/id")!.Value;
        var revision = h.Player.BeginSourceSwitch(Context(2), () => [h.Track(100).Id, h.Track(101).Id]);
        Assert.Equal("1", engine.Get("playlist-count")); // Old prefetched entry actually removed.
        engine.StartEntryId(stale); h.Pump(); Assert.Same(current, h.Player.CurrentTrack);
        Assert.True(h.Player.CompleteSourceSwitch(revision, [h.Track(100).Id, h.Track(101).Id])); h.Pump();
        Assert.Same(engine, h.Engine); Assert.False(engine.Disposed);
        Assert.Same(current, h.Player.CurrentTrack); Assert.Equal(42.125, h.Player.Position.TotalSeconds, 3);
        Assert.Equal(!paused, h.Player.IsPlaying); Assert.Equal(replacements, Replacements(h));
        Assert.Equal(Context(2), h.Player.Queue.Context); Assert.Equal(0, h.Player.Queue.SourceAnchorIndex);
        Assert.Equal(h.Track(100).Id, h.Player.Queue.Items[h.Player.Queue.NextIndex(true)!.Value].TrackId);
        Assert.Equal("2", engine.Get("playlist-count"));
        Assert.EndsWith(h.Track(100).FileName, engine.Commands.Last(c => c[0] == "loadfile")[1]);
        Assert.Equal(new[] { h.Rows[1], h.Rows[3] }, h.Table.SelectedItems.Cast<SongRow>());
        Assert.Same(source, h.Vm.Current); Assert.Equal(reveal, h.Version);
    });

    [Theory]
    [InlineData("service")]
    [InlineData("shortcut")]
    [InlineData("mcp")]
    public Task ImmediateNextResolvesCapturedMemoryOnceBeforeAnyAsyncRowsResult(string entry) => Run(h =>
    {
        var resolved = 0;
        var revision = h.Player.BeginSourceSwitch(Context(2), () => { resolved++; return [h.Track(100).Id, h.Track(101).Id]; });
        if (entry == "service") h.Player.Next();
        else if (entry == "mcp") h.Tools.Call("playback", new() { ["action"] = "next" });
        else
        {
            Assert.True(h.Table.Focus()); h.Window.KeyPress(Key.X, RawInputModifiers.None, PhysicalKey.None, null);
            h.Window.KeyRelease(Key.X, RawInputModifiers.None, PhysicalKey.None, null);
        }
        h.Ready();
        Assert.Equal(1, resolved); Assert.Null(h.Player.PendingSourceContext);
        Assert.Equal(h.Track(100).Id, h.Player.CurrentTrack!.Id);
        Assert.False(h.Player.CompleteSourceSwitch(revision, [h.Track(150).Id]));
        Assert.Equal(Context(2), h.Player.Queue.Context);
    });

    [Fact]
    public Task EofWhileRowsArePendingUsesTheNewSourceAndRejectsTheOldFileStarted() => Run(h =>
    {
        var old = h.Player.CurrentTrack; var stale = h.Engine.GetLong("playlist/1/id")!.Value;
        h.Player.BeginSourceSwitch(Context(2), () => [h.Track(100).Id, h.Track(101).Id]);
        h.Engine.StartEntryId(stale); h.Pump(); Assert.Same(old, h.Player.CurrentTrack);
        h.Engine.EndCurrent(Mpv.EndReason.Eof); h.Pump(); h.Ready();
        Assert.Equal(h.Track(100).Id, h.Player.CurrentTrack!.Id); Assert.True(h.Player.IsPlaying);
        Assert.Equal(Context(2), h.Player.Queue.Context);
    });

    [Fact]
    public Task CompletedHandoffSupportsNaturalPrefetchedStartWithoutReloadingCurrent() => Run(h =>
    {
        var revision = h.Player.BeginSourceSwitch(Context(2), () => [h.Track(100).Id, h.Track(101).Id]);
        h.Player.CompleteSourceSwitch(revision, [h.Track(100).Id, h.Track(101).Id]);
        var loads = Replacements(h);
        h.Engine.StartEntry(1); h.Pump();
        Assert.Equal(h.Track(100).Id, h.Player.CurrentTrack!.Id);
        Assert.Equal(loads, Replacements(h));
    });

    [Fact]
    public Task LatestIntentWinsWhenBRowsArriveAfterCAndOldCallbacksCannotChangeC() => Run(h =>
    {
        var bResolved = 0; var cResolved = 0;
        var b = h.Player.BeginSourceSwitch(Context(2), () => { bResolved++; return [h.Track(100).Id]; });
        var c = h.Player.BeginSourceSwitch(Context(3), () => { cResolved++; return [h.Track(200).Id, h.Track(201).Id]; });
        Assert.False(h.Player.CompleteSourceSwitch(b, [h.Track(100).Id]));
        Assert.Equal(Context(3), h.Player.PendingSourceContext);
        h.Player.Next(); h.Ready();
        Assert.Equal(0, bResolved); Assert.Equal(1, cResolved);
        Assert.Equal(h.Track(200).Id, h.Player.CurrentTrack!.Id);
        Assert.False(h.Player.CompleteSourceSwitch(c, [h.Track(205).Id]));
        Assert.False(h.Player.CompleteSourceSwitch(b, [h.Track(100).Id]));
        Assert.Equal(Context(3), h.Player.Queue.Context);
        Assert.Equal(h.Track(201).Id, h.Player.Queue.Items[h.Player.Queue.NextIndex(true)!.Value].TrackId);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExplicitPlayOrQueueJumpCancelsPendingBrowsingIntent(bool jump) => Run(h =>
    {
        var oldContext = h.Player.Queue.Context; var resolved = 0;
        var revision = h.Player.BeginSourceSwitch(Context(2), () => { resolved++; return [h.Track(100).Id]; });
        if (jump) h.Player.JumpTo(5);
        else h.Player.Play(null, [h.Track(6).Id, h.Track(7).Id], 0);
        h.Ready();
        Assert.Equal(0, resolved); Assert.Null(h.Player.PendingSourceContext);
        Assert.Equal("cancelled", h.Player.SourceSwitchStatus);
        Assert.Equal(h.Track(jump ? 5 : 6).Id, h.Player.CurrentTrack!.Id);
        Assert.False(h.Player.CompleteSourceSwitch(revision, [h.Track(100).Id]));
        Assert.Equal(jump ? oldContext : null, h.Player.Queue.Context);
    });

    [Fact]
    public Task ManualRequestsKeepPriorityAndEmptyOrUnavailableListsDoNotReloadOldMusic() => Run(h =>
    {
        h.Player.PlayNext([h.Track(20).Id]);
        var revision = h.Player.BeginSourceSwitch(Context(2), () => [h.Track(100).Id]);
        h.Player.CompleteSourceSwitch(revision, [h.Track(100).Id]);
        h.Player.Next(); h.Ready(); Assert.Equal(h.Track(20).Id, h.Player.CurrentTrack!.Id);
        h.Player.Next(); h.Ready(); Assert.Equal(h.Track(100).Id, h.Player.CurrentTrack!.Id);
        var empty = h.Player.BeginSourceSwitch(Context(3), () => []);
        h.Player.CompleteSourceSwitch(empty, []); var loads = Replacements(h);
        h.Player.Next(); h.Pump(); Assert.Equal(loads, Replacements(h)); Assert.True(h.Player.IsPlaying);
        h.Engine.EndCurrent(Mpv.EndReason.Eof); h.Pump(); Assert.False(h.Player.IsPlaying);
        Assert.Equal(h.Track(100).Id, h.Player.CurrentTrack!.Id);
        File.Delete(h.App.Library.PathOf(h.Track(200))!);
        h.Player.BeginSourceSwitch(Context(4), () => [h.Track(200).Id, h.Track(201).Id]);
        h.Player.Next(); h.Ready(); Assert.Equal(h.Track(201).Id, h.Player.CurrentTrack!.Id);
    });

    [Fact]
    public Task NavigatingSavedPlaylistThenImmediateMcpNextFollowsNewRowsWithoutReloadOnNavigation() => Run(h =>
    {
        var playlist = h.App.Playlists.Create("B"); h.App.Playlists.Add(playlist, [h.Track(100), h.Track(101)]);
        var current = h.Player.CurrentTrack; h.Engine.Properties["time-pos"] = "32.25";
        var loads = Replacements(h);
        h.Vm.Navigate(new NavTarget.Playlist(playlist));
        Assert.Same(current, h.Player.CurrentTrack); Assert.Equal(32.25, h.Player.Position.TotalSeconds);
        Assert.Equal(loads, Replacements(h)); Assert.True(h.Vm.ComputingRows);
        Assert.Equal(new QueueSource.Playlist(playlist), h.Player.PendingSourceContext!.Source);
        h.Tools.Call("playback", new() { ["action"] = "next" }); h.Ready();
        h.Wait(() => !h.Vm.ComputingRows);
        Assert.Equal(h.Track(100).Id, h.Player.CurrentTrack!.Id);
        Assert.Equal(new QueueSource.Playlist(playlist), h.Player.Queue.Context!.Source);
        Assert.Equal(new[] { h.Track(100).Id, h.Track(101).Id }, h.Rows.Select(r => r.Track!.Id));
    });

    [Fact]
    public Task NavigatingAThenBThenCRejectsOlderRowsAndExplicitPlayCancelsTheViewIntent() => Run(h =>
    {
        var b = h.App.Playlists.Create("B"); var c = h.App.Playlists.Create("C");
        h.App.Playlists.Add(b, [h.Track(100)]); h.App.Playlists.Add(c, [h.Track(200), h.Track(201)]);
        h.Vm.Navigate(new NavTarget.Playlist(b)); var old = h.Player.SourceSwitchRevision;
        h.Vm.Navigate(new NavTarget.Playlist(c));
        Assert.False(h.Player.CompleteSourceSwitch(old, [h.Track(100).Id]));
        h.Player.Next(); h.Ready(); h.Wait(() => !h.Vm.ComputingRows);
        Assert.Equal(h.Track(200).Id, h.Player.CurrentTrack!.Id);
        Assert.Equal(new QueueSource.Playlist(c), h.Player.Queue.Context!.Source);
        Assert.Equal(h.Track(200).Id, h.Rows[0].Track!.Id);
        h.Vm.Navigate(new NavTarget.Playlist(b));
        h.Player.Play(null, [h.Track(6).Id], 0); h.Ready(); h.Wait(() => !h.Vm.ComputingRows);
        Assert.Equal(h.Track(6).Id, h.Player.CurrentTrack!.Id); Assert.Null(h.Player.Queue.Context);
    });

    [Fact]
    public Task SearchFilterAndReverseSortUseDisplayedOrderButOrdinaryRefreshAndRowSelectionDoNotResetHistory() => Run(h =>
    {
        h.Vm.Navigate(new NavTarget.Search("song 10")); h.Player.Next(); h.Ready(); h.Wait(() => !h.Vm.ComputingRows);
        Assert.Equal(h.Track(100).Id, h.Player.CurrentTrack!.Id);
        Assert.Equal(10, h.Rows.Count);
        h.App.Db.SetTags([(h.Track(230).TrackKey, "MOOD", "SPORT", "MANUAL")]);
        h.App.Library.RebuildSnapshot(); h.Wait(() => h.App.Library.Snapshot.Tags.ContainsKey(h.Track(230).TrackKey));
        h.Vm.Navigate(new NavTarget.Folder(h.Track(0).FolderId, "")); h.Wait(() => !h.Vm.ComputingRows);
        h.Vm.ToggleTag("SPORT"); h.Player.Next(); h.Ready(); h.Wait(() => !h.Vm.ComputingRows);
        Assert.Single(h.Rows); Assert.Equal(h.Track(230).Id, h.Player.CurrentTrack!.Id);
        h.Vm.ClearFilterCommand.Execute(null); h.Wait(() => !h.Vm.ComputingRows);
        h.Vm.SetSort(SongSort.Title); h.Wait(() => !h.Vm.ComputingRows);
        h.Vm.SetSort(SongSort.Title); h.Player.Next(); h.Ready(); h.Wait(() => !h.Vm.ComputingRows);
        Assert.True(h.Vm.SortDescending); Assert.Equal(h.Track(229).Id, h.Player.CurrentTrack!.Id);
        h.Player.SetShuffle(true); h.Pump();
        var revision = h.Player.SourceSwitchRevision; var order = h.Player.Queue.Items.ToArray();
        h.Vm.Recompute(); h.Wait(() => !h.Vm.ComputingRows);
        h.Table.SelectedItem = h.Rows[3]; h.Pump();
        Assert.Equal(revision, h.Player.SourceSwitchRevision); Assert.Equal(order, h.Player.Queue.Items);
    });

    [Fact]
    public Task FolderRecursiveArtistAndAlbumNavigationAllFollowTheirOwnDisplayedSongs() => Run(h =>
    {
        var root = Path.GetDirectoryName(h.App.Library.PathOf(h.Track(0)))!;
        Directory.CreateDirectory(Path.Combine(root, "Child"));
        using (var tx = h.App.Db.Begin())
        {
            foreach (var file in new[] { "child-a.flac", "child-b.flac" })
            {
                File.WriteAllBytes(Path.Combine(root, "Child", file), []);
                h.App.Db.ReplaceFile(tx, h.Track(0).FolderId, "Child\\", file, [new JeekMusic.Library.Track
                { FolderId = h.Track(0).FolderId, Dir = "Child\\", FileName = file, TrackKey = file, Title = file,
                    Artists = ["Alternate"], ArtistDisplay = "Alternate", Album = "Album", AlbumKey = "test-album", DurationMs = 120000 }]);
            }
            tx.Commit();
        }
        h.App.Library.RebuildSnapshot(); h.Wait(() => h.App.Library.Snapshot.Tracks.Count == 262 && !h.Vm.ComputingRows);
        var children = h.App.Library.Snapshot.Tracks.Where(t => t.Dir == "Child\\").OrderBy(t => t.FileName).ToArray();
        h.Vm.Navigate(new NavTarget.Folder(h.Track(0).FolderId, "Child\\")); h.Player.Next(); h.Ready();
        Assert.Equal(children[0].Id, h.Player.CurrentTrack!.Id);
        h.Vm.Navigate(new NavTarget.Folder(h.Track(0).FolderId, "")); h.Wait(() => !h.Vm.ComputingRows);
        h.Vm.IncludeSubfolders = true; h.Player.Next(); h.Ready(); h.Wait(() => !h.Vm.ComputingRows);
        Assert.Equal(children[1].Id, h.Player.CurrentTrack!.Id);
        Assert.True(Assert.IsType<QueueSource.Folder>(h.Player.Queue.Context!.Source).Recursive);
        var artist = h.App.Library.Snapshot.Artists.Single(a => a.Name == "Alternate");
        h.Vm.Navigate(new NavTarget.Artists(artist.NameKey)); h.Player.Previous(); h.Ready(); h.Wait(() => !h.Vm.ComputingRows);
        Assert.Equal(children[0].Id, h.Player.CurrentTrack!.Id);
        h.Vm.Navigate(new NavTarget.Album("test-album")); h.Player.Next(); h.Ready(); h.Wait(() => !h.Vm.ComputingRows);
        Assert.Equal(children[1].Id, h.Player.CurrentTrack!.Id);
        Assert.Equal(new QueueSource.Album("test-album"), h.Player.Queue.Context!.Source);
    });

    [Fact]
    public Task CaptureKeepsPlaylistMembershipFilterAndOrderIndependentOfLaterRepositoryOrSettingsWrites() => Run(h =>
    {
        var id = h.App.Playlists.Create("Captured"); h.App.Playlists.Add(id, [h.Track(101), h.Track(100)]);
        var entries = h.App.Playlists.Entries(id);
        var compute = ListBuilder.Capture(h.App.Library.Snapshot, new QueueSource.Playlist(id), h.App.Settings.Current, entries);
        h.App.Playlists.Add(id, [h.Track(200)]);
        h.App.Settings.Current.SortDescending = true; h.App.Settings.Current.TagFilter = ["SPORT"];
        Assert.Equal(new[] { h.Track(101).Id, h.Track(100).Id }, compute().Select(t => t.Id));
    });

    [Fact]
    public Task McpQueueJumpOverridesPendingViewRowsWithoutLettingTheirLateResultRetakeTheQueue() => Run(h =>
    {
        var id = h.App.Playlists.Create("New view"); h.App.Playlists.Add(id, [h.Track(100)]);
        var oldContext = h.Player.Queue.Context;
        h.Vm.Navigate(new NavTarget.Playlist(id));
        h.Tools.Call("edit_queue", new() { ["action"] = "jump", ["index"] = 5 });
        h.Ready(); h.Wait(() => !h.Vm.ComputingRows);
        Assert.Equal(h.Track(5).Id, h.Player.CurrentTrack!.Id); Assert.Equal(oldContext, h.Player.Queue.Context);
        Assert.Single(h.Rows); Assert.Equal(h.Track(100).Id, h.Rows[0].Track!.Id);
        Assert.Null(h.Player.PendingSourceContext);
    });

    [Fact]
    public Task SourceDiagnosticsAreCompatibleReadOnlyAndDoNotResolvePendingIntent() => Run(h =>
    {
        var resolved = 0;
        var revision = h.Player.BeginSourceSwitch(Context(2), () => { resolved++; return [h.Track(100).Id]; });
        for (var i = 0; i < 3; i++)
        {
            var source = h.Tools.Call("get_queue", new())["sourceState"]!;
            Assert.Equal(2L, source["pendingContext"]!["playlistId"]!.GetValue<long>());
            Assert.Equal("pending", source["status"]!.GetValue<string>());
            var diagnostics = h.Tools.Call("get_diagnostics", new())["queueSource"]!;
            Assert.Equal(revision, diagnostics["revision"]!.GetValue<long>());
        }
        Assert.Equal(0, resolved);
        h.Player.Next(); h.Ready();
        var after = h.Tools.Call("get_diagnostics", new())["queueSource"]!;
        Assert.Null(after["pendingContext"]); Assert.Equal("committed", after["status"]!.GetValue<string>());
        Assert.Equal(0, after["sourceAnchorIndex"]!.GetValue<int>());
        Assert.Equal(24, h.Tools.Catalog.Count);
    });

    [Fact]
    public Task PersistedExternalCurrentRemainsHistoryOnlyAfterRestore() => Run(h =>
    {
        var revision = h.Player.BeginSourceSwitch(Context(2), () => [h.Track(100).Id, h.Track(101).Id]);
        h.Player.CompleteSourceSwitch(revision, [h.Track(100).Id, h.Track(101).Id]);
        h.App.QueuePersistence.SaveQueue();
        using var restored = new PlayerService(h.App.Library, _ => throw new InvalidOperationException("Restore must not start an engine"));
        new QueuePersistence(h.App.Db, restored).Restore(h.App.Library.Snapshot);
        Assert.Equal(0, restored.Queue.SourceAnchorIndex);
        restored.Queue.MoveTo(restored.Queue.Items.Count - 1); restored.Queue.Repeat = RepeatMode.All;
        Assert.Equal(h.Track(100).Id, restored.Queue.Items[restored.Queue.NextIndex(false)!.Value].TrackId);
    });

    static int Replacements(PlayingVisibilityTests.Harness h) => h.Engine.Commands.Count(c => c[0] == "loadfile" && c[2] == "replace");
    static async Task Run(Action<PlayingVisibilityTests.Harness> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(() =>
        {
            using var h = new PlayingVisibilityTests.Harness();
            h.Window.PlayFrom(h.Track(1)); h.Ready(); test(h);
        }, CancellationToken.None);
    }
}
