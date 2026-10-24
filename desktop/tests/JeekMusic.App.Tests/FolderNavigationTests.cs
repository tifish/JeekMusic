using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.Automation;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.App.Tests;

// Only Headless controls, memory DB, temporary settings/snapshot and injected fake audio.
// Never start Shell, Program, a pipe bridge, scanner, decoder or a real endpoint.
[Collection("Avalonia UI")]
public sealed class FolderNavigationTests
{
    [Fact]
    public Task FirstUseAndNewDirectoriesExpandEveryFolderButLeaveOtherGroupsAlone() => Run(() =>
    {
        using var h = new Harness();
        Assert.All(h.Folders, n => Assert.True(n.IsExpanded, n.Key));
        Assert.All(h.Folders, n => Assert.True(h.Container(n).IsExpanded));
        Assert.False(h.Vm.Nav.Single(n => n.Kind == NavKind.Playlists).IsExpanded);
        Assert.False(h.Vm.Nav.Single(n => n.Kind == NavKind.Artists).IsExpanded);
        Assert.Empty(h.Save().CollapsedFolders);
        h.AddTrack(h.RootId, "New\\Deeper\\"); h.Rebuild();
        Assert.True(h.Node("New\\").IsExpanded); Assert.True(h.Node("New\\Deeper\\").IsExpanded);
        Assert.Empty(h.Save().CollapsedFolders);
    });

    [Fact]
    public Task RealMouseAndKeyboardOperationsKeepParentAndChildPreferencesIndependent() => Run(() =>
    {
        using var h = new Harness();
        h.Toggle(h.Node("Rock\\Live\\"));
        Assert.False(h.Node("Rock\\Live\\").IsExpanded);
        Assert.Single(h.Save().CollapsedFolders);
        h.Toggle(h.Node("Rock\\"));
        Assert.False(h.Node("Rock\\").IsExpanded);
        Assert.Equal(2, h.Save().CollapsedFolders.Count);
        Assert.True(h.Node("Rock\\Live\\Disc\\").IsExpanded); // Hiding is not a user collapse.
        h.Key(h.Node("Rock\\"), Key.Right);
        Assert.True(h.Node("Rock\\").IsExpanded);
        Assert.False(h.Node("Rock\\Live\\").IsExpanded);
        Assert.Single(h.Save().CollapsedFolders);
        h.Key(h.Node("Rock\\Live\\"), Key.Right);
        Assert.True(h.Node("Rock\\Live\\").IsExpanded); Assert.Empty(h.Save().CollapsedFolders);
        h.Key(h.Node("Rock\\"), Key.Left);
        Assert.False(h.Node("Rock\\").IsExpanded); Assert.Single(h.Save().CollapsedFolders);
        h.Vm.Refresh(); h.Pump();
        Assert.False(h.Node("Rock\\").IsExpanded); Assert.True(h.Node("Rock\\Live\\").IsExpanded);
    });

    [Fact]
    public Task CollapsedFolderGroupSurvivesSelectionRefreshAndSnapshotReplacement() => Run(() =>
    {
        using var h = new Harness();
        h.Toggle(h.Group);
        Assert.False(h.Group.IsExpanded); Assert.Single(h.Save().CollapsedFolders);
        h.Vm.SelectedNav = null; h.Vm.SelectedNav = h.Group; h.Pump();
        Assert.False(h.Group.IsExpanded); // Selecting the group no longer forces it open.
        h.Vm.Navigate(new NavTarget.Folder(h.RootId, "Rock\\Live\\Disc\\"), history: false); h.Pump();
        Assert.False(h.Group.IsExpanded);
        h.Vm.Refresh(); h.Pump(); Assert.False(h.Group.IsExpanded);
        h.AddTrack(h.RootId, "AddedDuringScan\\"); h.Rebuild();
        Assert.False(h.Group.IsExpanded); Assert.True(h.Node("AddedDuringScan\\").IsExpanded);
        Assert.All(h.Folders.Where(n => n != h.Group), n => Assert.True(n.IsExpanded));
        Assert.Single(h.Save().CollapsedFolders);
        h.Toggle(h.Group); Assert.Empty(h.Save().CollapsedFolders);
    });

    [Fact]
    public Task FilteredOutAndTemporarilyMissingNodesKeepPreferencesWithoutInventingNodes() => Run(() =>
    {
        using var h = new Harness();
        h.Toggle(h.Node("Rock\\"));
        var saved = h.Save().CollapsedFolders.ToArray();
        var jazz = h.App.Library.Snapshot.Tracks.Single(t => t.Dir == "Jazz\\");
        h.App.Db.SetTags([(jazz.TrackKey, "MOOD", "SPORT", "MANUAL")]); h.Rebuild();
        h.Vm.ToggleTag("SPORT"); h.Pump();
        Assert.DoesNotContain(h.Folders, n => n.FolderId == h.RootId && n.Dir == "Rock\\");
        Assert.Equal(saved, h.Save().CollapsedFolders);
        h.Vm.ClearFilterCommand.Execute(null); h.Pump();
        Assert.False(h.Node("Rock\\").IsExpanded);
        h.App.Db.ClearFolder(h.RootId); h.Rebuild();
        Assert.DoesNotContain(h.Folders, n => n.FolderId == h.RootId && n.Dir == "Rock\\");
        Assert.Equal(saved, h.Save().CollapsedFolders);
        h.AddTrack(h.RootId, "Rock\\Live\\Disc\\"); h.Rebuild();
        Assert.False(h.Node("Rock\\").IsExpanded); Assert.True(h.Node("Rock\\Live\\").IsExpanded);
        Assert.Equal(saved, h.Save().CollapsedFolders);
    });

    [Fact]
    public Task SameNamesInDifferentRootsAndDirectoriesNeverSharePreferences() => Run(() =>
    {
        using var h = new Harness();
        h.Toggle(h.Node("Rock\\"));
        Assert.False(h.Node("Rock\\").IsExpanded);
        Assert.True(h.Node("Rock\\", h.OtherRootId).IsExpanded);
        Assert.True(h.Node("Jazz\\Rock\\").IsExpanded);
        Assert.Equal(h.Root.Title, h.Node("", h.OtherRootId).Title);
        h.Vm.Refresh(); h.Pump();
        Assert.False(h.Node("Rock\\").IsExpanded);
        Assert.True(h.Node("Rock\\", h.OtherRootId).IsExpanded);
        Assert.True(h.Node("Jazz\\Rock\\").IsExpanded);
    });

    [Fact]
    public Task RestartUsesProductionJsonAndLayoutHooksAndDoesNotRevealRestoredSelection() => Run(() =>
    {
        UiState state;
        using (var first = new Harness())
        {
            first.Vm.Navigate(new NavTarget.Folder(first.RootId, "Rock\\Live\\Disc\\")); first.Pump();
            first.Toggle(first.Node("Rock\\")); first.Toggle(first.Group);
            state = RoundTrip(first.Save());
            Assert.Equal(2, state.CollapsedFolders.Count);
        }
        using var restarted = new Harness(state, new NavTarget.Folder(1, "Rock\\Live\\Disc\\"));
        Assert.False(restarted.Group.IsExpanded); Assert.False(restarted.Node("Rock\\").IsExpanded);
        Assert.True(restarted.Node("Rock\\Live\\").IsExpanded);
        Assert.Equal("Rock\\Live\\Disc\\", restarted.Vm.SelectedNav!.Dir);
        Assert.Equal(state.CollapsedFolders, restarted.Save().CollapsedFolders);
        restarted.Toggle(restarted.Group); restarted.Toggle(restarted.Node("Rock\\"));
        Assert.Empty(RoundTrip(restarted.Save()).CollapsedFolders);
    });

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"expandedNav\":[\"Folders\",\"folder:1:Rock\\\\\"]}")]
    public Task OldExpandedNavNeverBecomesAnImplicitCollapseList(string json) => Run(() =>
    {
        var old = JsonSerializer.Deserialize(json, UiStateJson.Default.UiState)!;
        using var h = new Harness(old);
        Assert.All(h.Folders, n => Assert.True(n.IsExpanded));
        Assert.Empty(h.Save().CollapsedFolders);
    });

    [Fact]
    public void StableKeysNormalizeCaseSeparatorsAndTrailingSlashWithoutDependingOnRowIdOrDrive()
    {
        var a = new LibraryFolder { Id = 1, VolumeId = "{Volume}", PathInVolume = "\\Music\\", Root = "C:\\Music\\", DisplayName = "A" };
        var b = new LibraryFolder { Id = 99, VolumeId = "{volume}", PathInVolume = "/music", Root = "Z:\\music\\", DisplayName = "B" };
        Assert.Equal(FolderExpansionState.DirectoryKey(a, "Rock\\Live\\"), FolderExpansionState.DirectoryKey(b, "/rock/live"));
        Assert.Equal(FolderExpansionState.DirectoryKey(a, ""), FolderExpansionState.DirectoryKey(b, "/"));
        var state = new FolderExpansionState();
        state.Restore(new UiState { CollapsedFolders = [FolderExpansionState.DirectoryKey(a, "Rock\\").ToLowerInvariant()] });
        var node = new NavNode { Kind = NavKind.Folder, Title = "B", Icon = "", FolderExpansionKey = FolderExpansionState.DirectoryKey(b, "rock") };
        state.Apply(node); Assert.False(node.IsExpanded);
        node.IsExpanded = true; // Programmatic reveal, never a user preference update.
        var saved = new UiState(); state.Save(saved); Assert.Single(saved.CollapsedFolders);
        state.Apply(node); Assert.False(node.IsExpanded);
    }

    [Fact]
    public Task ProgrammaticModelAndControlWritesAndOrdinarySelectionNeverChangeUserPreferences() => Run(() =>
    {
        using var h = new Harness();
        h.Toggle(h.Node("Rock\\"));
        var saved = h.Save().CollapsedFolders.ToArray();
        var node = h.Node("Rock\\");
        node.IsExpanded = true; h.Pump();
        Assert.True(h.Container(node).IsExpanded); Assert.Equal(saved, h.Save().CollapsedFolders);
        h.Container(node).IsExpanded = false; h.Pump();
        h.Container(node).IsExpanded = true; h.Pump();
        Assert.Equal(saved, h.Save().CollapsedFolders);
        h.Vm.Navigate(new NavTarget.Folder(h.RootId, "Rock\\Live\\Disc\\")); h.Pump();
        Assert.Equal(saved, h.Save().CollapsedFolders);
        h.Vm.Refresh(); h.Pump(); Assert.False(h.Node("Rock\\").IsExpanded);
        h.ClickHeader(h.Root); // A no-op input must not turn a programmatic change elsewhere into intent.
        Assert.Equal(saved, h.Save().CollapsedFolders);
    });

    [Fact]
    public Task ActualHeaderDoubleClickRecordsCollapseAndExpand() => Run(() =>
    {
        using var h = new Harness();
        h.DoubleClickHeader(h.Node("Rock\\"));
        Assert.False(h.Node("Rock\\").IsExpanded); Assert.Single(h.Save().CollapsedFolders);
        h.DoubleClickHeader(h.Node("Rock\\"));
        Assert.True(h.Node("Rock\\").IsExpanded); Assert.Empty(h.Save().CollapsedFolders);
    });

    [Fact]
    public Task NavOrderPinnedArtistsAndPlaylistSelectionKeepExistingBehavior() => Run(() =>
    {
        using var h = new Harness();
        h.Toggle(h.Node("Rock\\"));
        var artist = h.App.Library.Snapshot.Artists.Single();
        var order = new List<NavItem> { new("PLAYLISTS", null), new("ARTIST", artist.NameKey), new("ARTISTS", null), new("FOLDERS", null) };
        h.App.Db.SetNavItems(order); h.Vm.Refresh(); h.Pump();
        Assert.Equal(order, h.Vm.Nav.Select(n => n.Item));
        var pinned = h.Vm.Nav.Single(n => n.Kind == NavKind.PinnedArtist);
        Assert.Equal($"artist:{artist.NameKey}", pinned.Key);
        h.Vm.SelectedNav = pinned; h.Pump();
        Assert.Equal(artist.NameKey, Assert.IsType<NavTarget.PinnedArtist>(h.Vm.Current).ArtistKey);
        var lists = h.Vm.Nav.Single(n => n.Kind == NavKind.Playlists);
        Assert.False(lists.IsExpanded);
        h.Vm.SelectedNav = lists; h.Pump();
        Assert.True(lists.IsExpanded); Assert.IsType<NavTarget.Playlist>(h.Vm.Current);
        Assert.False(h.Node("Rock\\").IsExpanded); Assert.Single(h.Save().CollapsedFolders);
        Assert.Equal(order, h.App.Db.NavItems());
    });

    [Fact]
    public void LargePreferenceSetKeepsOnlyExplicitExceptionsAcrossLinearReapplication()
    {
        var state = new FolderExpansionState();
        var folder = new LibraryFolder { VolumeId = "fake", PathInVolume = "\\music\\", Root = "C:\\mock\\", DisplayName = "Music" };
        var nodes = Enumerable.Range(0, 10000).Select(i => new NavNode { Kind = NavKind.Folder, Title = "Same", Icon = "",
            FolderExpansionKey = FolderExpansionState.DirectoryKey(folder, $"dir{i}\\") }).ToArray();
        foreach (var node in nodes) state.Apply(node);
        Assert.All(nodes, n => Assert.True(n.IsExpanded));
        foreach (var index in new[] { 0, 4999, 9999 }) state.RecordUserExpansion(nodes[index], expanded: false);
        foreach (var node in nodes) state.Apply(node);
        Assert.Equal(3, nodes.Count(n => !n.IsExpanded));
        var saved = new UiState(); state.Save(saved); Assert.Equal(3, saved.CollapsedFolders.Count);
        Assert.Equal(10000, nodes.Select(n => n.FolderExpansionKey).Distinct().Count());
    }

    [Fact]
    public Task ThemeAndWindowRecreationRestoreCollapsedAncestorsWithoutChangingOtherGroups() => Run(() =>
    {
        using var h = new Harness();
        h.Toggle(h.Node("Rock\\"));
        var playlists = h.Vm.Nav.Single(n => n.Kind == NavKind.Playlists);
        Assert.False(playlists.IsExpanded);
        h.Toggle(playlists); Assert.True(playlists.IsExpanded);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            h.Window.RequestedThemeVariant = theme; h.Pump();
            h.Vm.Refresh(); h.Pump();
            Assert.False(h.Node("Rock\\").IsExpanded);
            Assert.True(h.Vm.Nav.Single(n => n.Kind == NavKind.Playlists).IsExpanded);
            Assert.False(h.Vm.Nav.Single(n => n.Kind == NavKind.Artists).IsExpanded);
        }
        var saved = h.Save();
        h.RecreateWindow(new NavTarget.Folder(h.RootId, "Rock\\Live\\Disc\\"));
        Assert.False(h.Node("Rock\\").IsExpanded); Assert.True(h.Node("Rock\\Live\\").IsExpanded);
        Assert.Equal("Rock\\Live\\Disc\\", h.Vm.SelectedNav!.Dir);
        Assert.Equal(saved.CollapsedFolders, h.Save().CollapsedFolders);
    });

    [Fact]
    public Task ExplicitLocateRevealsOnlyAncestorsTemporarilyAndKeepsSerializedUserIntent() => Run(() =>
    {
        using var h = new Harness();
        h.Toggle(h.Node("Rock\\")); h.Toggle(h.Group);
        var saved = h.Save().CollapsedFolders.ToArray();
        var track = h.App.Library.Snapshot.Tracks.Single(t => t.Dir == "Rock\\Live\\Disc\\");
        h.App.Player.Restore([new(track.Id, false)], 0, false, RepeatMode.Off, null, TimeSpan.Zero);
        h.Vm.Navigate(new NavTarget.Artists(null)); h.Pump();
        h.Window.LocatePlaying(); h.Wait(() => h.Window.FindControl<DataGrid>("Table")!.SelectedItem is SongRow);
        Assert.True(h.Group.IsExpanded); Assert.True(h.Node("Rock\\").IsExpanded);
        Assert.Equal("Rock\\Live\\Disc\\", h.Vm.SelectedNav!.Dir);
        Assert.Equal(saved, h.Save().CollapsedFolders);
        Assert.Equal(2, h.Diagnostic["temporarilyExpandedCount"]!.GetValue<int>());
        h.Vm.Refresh(); h.Pump();
        Assert.False(h.Group.IsExpanded); Assert.False(h.Node("Rock\\").IsExpanded);
        Assert.Equal(saved, RoundTrip(h.Save()).CollapsedFolders);
        h.Window.LocatePlaying(); h.Pump(); // Track is already in the current table.
        Assert.True(h.Group.IsExpanded); Assert.True(h.Node("Rock\\").IsExpanded);
        Assert.Equal(saved, h.Save().CollapsedFolders);
    });

    [Fact]
    public Task DiagnosticsReadExistingStateBeforeDuringAndAfterWindowWithoutSideEffects() => Run(() =>
    {
        using var h = new Harness();
        var tools = new McpTools(h.App);
        h.Toggle(h.Node("Rock\\"));
        var selected = h.Vm.SelectedNav; var current = h.Vm.Current;
        var state = h.Save().CollapsedFolders.ToArray();
        var queue = h.App.Player.Queue.Items.ToArray();
        for (var i = 0; i < 5; i++)
        {
            var d = h.Diagnostic;
            Assert.True(d["created"]!.GetValue<bool>()); Assert.True(d["defaultExpanded"]!.GetValue<bool>());
            Assert.Equal(1, d["collapsedPreferenceCount"]!.GetValue<int>());
            Assert.Equal(h.Folders.Count, d["nodeCount"]!.GetValue<int>());
            Assert.Equal(h.Folders.Count - 1, d["expandedNodeCount"]!.GetValue<int>());
            Assert.Equal(0, d["temporarilyExpandedCount"]!.GetValue<int>());
            Assert.Equal("preserve-collapsed", d["automaticNavigation"]!.GetValue<string>());
            Assert.Equal("temporary-reveal", d["explicitLocate"]!.GetValue<string>());
        }
        Assert.Same(selected, h.Vm.SelectedNav); Assert.Equal(current, h.Vm.Current);
        Assert.Equal(state, h.Save().CollapsedFolders); Assert.Equal(queue, h.App.Player.Queue.Items);
        Assert.Equal(22, tools.Catalog.Count);
        var schema = tools.Catalog.Single(t => t!["name"]!.GetValue<string>() == "get_diagnostics")!["inputSchema"]!;
        Assert.Empty(schema["properties"]!.AsObject()); Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        h.CloseWindow();
        Assert.False(h.Diagnostic["created"]!.GetValue<bool>());
        Assert.Equal(1, h.Diagnostic["collapsedPreferenceCount"]!.GetValue<int>());
        Assert.Null(h.App.Navigation); Assert.False(h.App.Player.EngineReady);
    });

    static UiState RoundTrip(UiState state)
    {
        var path = Path.Combine(Path.GetTempPath(), "jeek-nav-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(state, UiStateJson.Default.UiState));
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), UiStateJson.Default.UiState)!;
        }
        finally { File.Delete(path); }
    }

    static async Task Run(Action test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(test, CancellationToken.None);
    }

    sealed class Harness : IDisposable
    {
        readonly string _settingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        readonly OutputModeSwitchTests.FakeEndpointVolume _endpoint = new();
        readonly List<OutputModeSwitchTests.FakeEngine> _engines = [];
        int _file;
        public AppServices App { get; }
        public MainWindow Window { get; private set; }
        public MainViewModel Vm => Window.ViewModel;
        public TreeView Tree => Window.FindControl<TreeView>("NavTree")!;
        public long RootId { get; }
        public long OtherRootId { get; }
        public NavNode Group => Vm.Nav.Single(n => n.Kind == NavKind.Folders);
        public NavNode Root => Node("");
        public IReadOnlyList<NavNode> Folders => Vm.Nav.SelectMany(Flatten).Where(n => n.Kind is NavKind.Folder or NavKind.Folders).ToArray();
        public System.Text.Json.Nodes.JsonNode Diagnostic => new McpTools(App).Call("get_diagnostics", new())["folderNavigation"]!;
        public Harness(UiState? ui = null, NavTarget? initial = null)
        {
            App = new AppServices(new SettingsStore(_settingsPath), new LibraryDb(":memory:"), source =>
                new PlayerService(source, options => { var e = new OutputModeSwitchTests.FakeEngine(options); _engines.Add(e); return e; }, _endpoint));
            Assert.False(new McpTools(App).Call("get_diagnostics", new())["folderNavigation"]!["created"]!.GetValue<bool>());
            RootId = App.Db.AddFolder("fake-volume", "\\music\\", "C:\\mock-music\\", "Same name");
            OtherRootId = App.Db.AddFolder("fake-volume", "\\other\\", "C:\\mock-other\\", "Same name");
            AddTrack(RootId, "Rock\\Live\\Disc\\"); AddTrack(RootId, "Jazz\\"); AddTrack(RootId, "Jazz\\Rock\\Disc\\");
            AddTrack(OtherRootId, "Rock\\Live\\");
            App.Playlists.Create("Keep playlist behavior");
            Window = CreateWindow();
            if (ui is not null) Window.RestoreLayout(ui);
            Vm.InitialTarget = initial;
            Rebuild(); Window.Show(); Pump();
        }
        MainWindow CreateWindow()
        {
            var window = new MainWindow(App) { ExitRequested = true };
            window.Styles.Add(new StyleInclude(new Uri("avares://JeekMusic/")) { Source = new Uri("avares://JeekMusic/UI/Styles.axaml") });
            return window;
        }
        public void RecreateWindow(NavTarget initial)
        {
            CloseWindow(); Window = CreateWindow(); Vm.InitialTarget = initial;
            Rebuild(); Window.Show(); Pump();
        }
        public void AddTrack(long root, string dir)
        {
            var file = $"{++_file}.flac";
            using var tx = App.Db.Begin();
            App.Db.ReplaceFile(tx, root, dir, file, [new JeekMusic.Library.Track { FolderId = root, TrackKey = $"{root}:{dir}{file}", Dir = dir, FileName = file,
                Title = file, ArtistDisplay = "Test", Artists = ["Test"] }]);
            tx.Commit();
        }
        public void Rebuild() { App.Library.RebuildSnapshot(); Pump(); }
        public NavNode Node(string dir, long? root = null) => Folders.Single(n => n.Kind == NavKind.Folder && n.FolderId == (root ?? RootId) && n.Dir == dir);
        public TreeViewItem Container(NavNode node) => Tree.GetVisualDescendants().OfType<TreeViewItem>().Single(c => ReferenceEquals(c.DataContext, node));
        public void Toggle(NavNode node)
        {
            var item = Container(node);
            var expander = item.GetVisualDescendants().OfType<ToggleButton>().Single(b => ReferenceEquals(b.TemplatedParent, item));
            var point = expander.TranslatePoint(new Point(expander.Bounds.Width / 2, expander.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(point, MouseButton.Left); Window.MouseUp(point, MouseButton.Left); Pump();
        }
        public void ClickHeader(NavNode node)
        {
            var text = Container(node).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("navtitle") && ReferenceEquals(t.DataContext, node) && t.Text == node.Title);
            var point = text.TranslatePoint(new Point(text.Bounds.Width / 2, text.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(point, MouseButton.Left); Window.MouseUp(point, MouseButton.Left); Pump();
        }
        public void DoubleClickHeader(NavNode node) { ClickHeader(node); ClickHeader(node); }
        public void Key(NavNode node, Key key)
        {
            Assert.True(Container(node).Focus());
            Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            Window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null); Pump();
        }
        public UiState Save() { var state = new UiState(); Window.SaveLayout(state); return state; }
        public void Pump() { for (var i = 0; i < 8; i++) { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
        public void Wait(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(3);
            do { Pump(); if (condition()) return; Thread.Yield(); } while (DateTime.UtcNow < until);
            Assert.Fail("Timed out waiting for the simulated UI computation");
        }
        public void CloseWindow() { Vm.Player.ProgressVisible = false; Window.Close(); Pump(); }
        public void Dispose()
        {
            CloseWindow(); App.Dispose();
            Assert.Empty(_endpoint.Writes); Assert.Empty(_engines);
            File.Delete(_settingsPath);
        }
        static IEnumerable<NavNode> Flatten(NavNode node) => node.Children.SelectMany(Flatten).Prepend(node);
    }
}
