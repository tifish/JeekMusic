using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.Automation;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.App.Tests;

// Headless windows only; no Shell/Program/pipe bridge, native engine, device or audio file.
[Collection("Avalonia UI")]
public sealed class ArtistListTests
{
    [Fact]
    public Task ExistingClassificationIsGroupedWithUnknownAndSymbolsTogetherAndHeadersAreNotItems() => Run(() =>
    {
        using var h = new Harness(["123", "!symbol", "Alice", "Bob", "周杰伦", ""]);
        Assert.Equal(new[] { "A", "B", "Z", "#" }, h.List.Groups.Select(g => g.Letter));
        Assert.Equal(3, h.List.Groups[^1].Count);
        Assert.True(h.Artists[^1].IsUnknown);
        Assert.Equal(h.App.Library.Snapshot.Artists.Where(a => a.IndexLetter == "#"), h.Artists.Where(a => a.IndexLetter == "#"));
        Assert.Equal(h.Artists.Count, h.List.ItemCount);
        Assert.All(h.List.Items, a => Assert.IsType<ArtistInfo>(a));
        foreach (var group in h.List.Groups)
        {
            h.List.ScrollIntoView(group.FirstIndex); h.Pump();
            var container = h.List.ContainerFromIndex(group.FirstIndex)!;
            Assert.Equal(group.Letter, ArtistListBox.GetGroupHeader(container));
            var header = container.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("artist-group-header"));
            Assert.True(header.IsVisible);
            Assert.Equal(group.Letter, header.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        }
        h.List.SelectedIndex = 0; h.Pump(); Assert.True(h.List.ContainerFromIndex(0)!.Focus());
        h.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null); h.Window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null); h.Pump();
        Assert.Equal(1, h.List.SelectedIndex); // Header contributes no keyboard selection stop.
    });

    [Fact]
    public Task IndexClicksAlignRealizedHeadersAndBottomClampsToActualVisibleGroup() => Run(() =>
    {
        using var h = new Harness(Uneven());
        h.List.SelectedIndex = 0; h.Pump();
        var selected = h.List.SelectedItem;
        foreach (var letter in new[] { "M", "B", "A" })
        {
            h.ClickLetter(letter);
            var group = h.List.Groups.Single(g => g.Letter == letter);
            var container = h.List.ContainerFromIndex(group.FirstIndex)!;
            Assert.InRange(Math.Abs(container.TranslatePoint(default, h.Viewport)!.Value.Y), 0, 1);
            Assert.Equal(letter, h.List.CurrentLetter);
            Assert.Same(selected, h.List.SelectedItem);
            h.AssertVisible();
        }
        h.ClickLetter("#");
        h.AssertVisible();
        Assert.Equal("M", h.List.CurrentLetter); // Tiny Z/# tail cannot fill the viewport.
        Assert.False(h.List.Groups.Single(g => g.Letter == "#").IsCurrent);
        Assert.True(h.List.ContainerFromIndex(h.Artists.Count - 1)!.TranslatePoint(default, h.Viewport)!.Value.Y > 0);
        Assert.IsType<VirtualizingStackPanel>(h.List.ItemsPanelRoot);
        Assert.InRange(h.List.GetRealizedContainers().Count(), 1, 30);
    });

    [Fact]
    public Task WheelScrollbarAndSelectionUseVisibleTopRatherThanProportionsOrSelectedArtist() => Run(() =>
    {
        using var h = new Harness(Uneven());
        h.List.SelectedIndex = 0; h.Pump();
        var selected = h.List.SelectedItem;
        h.Scroll.Offset = new Vector(0, 6200); h.Pump();
        Assert.Equal("A", h.List.CurrentLetter); // Many rows into a very large A group; selection remains at row zero.
        h.AssertVisible();
        h.Window.MouseWheel(h.Scroll.TranslatePoint(new Point(40, 80), h.Window)!.Value, new Vector(0, -5)); h.Pump();
        h.AssertVisible(); Assert.Same(selected, h.List.SelectedItem);
        var bar = h.Scroll.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        bar.Value = bar.Maximum; h.Pump(); h.AssertVisible();
        Assert.Equal("M", h.List.CurrentLetter); Assert.Same(selected, h.List.SelectedItem);
        Assert.Equal("M", h.Window.FindControl<TextBlock>("CurrentArtistLetter")!.Text);
        h.List.SelectedIndex = h.Artists.Count - 1; h.Pump();
        h.AssertVisible(); // Selection may reveal the row; it cannot force the # highlight.
        Assert.Equal("M", h.List.CurrentLetter);
        Assert.Equal(h.Artists[^1].NameKey, Assert.IsType<NavTarget.Artists>(h.Window.ViewModel.Current).ArtistKey);
        Assert.Null(h.App.Player.CurrentTrack); Assert.False(h.App.Player.IsPlaying);
    });

    [Fact]
    public Task GroupBoundaryIsTheFirstRowWithMoreThanHalfAPixelVisible() => Run(() =>
    {
        using var h = new Harness(Uneven());
        h.ClickLetter("B");
        h.Scroll.Offset -= new Vector(0, 1); h.Pump();
        Assert.Equal("A", h.List.CurrentLetter); h.AssertVisible();
        h.Scroll.Offset += new Vector(0, 1); h.Pump();
        Assert.Equal("B", h.List.CurrentLetter); h.AssertVisible();
        h.Scroll.Offset += new Vector(0, 20); h.Pump(); // Header partially scrolled away.
        Assert.Equal("B", h.List.CurrentLetter); h.AssertVisible();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TouchAndThumbDragUpdatePromptWithoutChangingSelectionOrReturningToIt(bool touch) => Run(() =>
    {
        using var h = new Harness(Uneven());
        h.List.SelectedIndex = 0; h.Pump(); h.ClickLetter("B");
        var selected = h.List.SelectedItem;
        h.Scroll.IsScrollInertiaEnabled = false;
        var thumb = h.Scroll.GetVisualDescendants().OfType<ScrollBar>()
            .Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical).GetVisualDescendants().OfType<Thumb>().Single();
        var source = touch ? (Control)h.Scroll : thumb;
        var point = source.TranslatePoint(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2), h.Window)!.Value;
        var before = h.Scroll.Offset.Y;
        using var contact = touch ? h.Window.TouchBegin(point) : null;
        if (!touch) h.Window.MouseDown(point, MouseButton.Left);
        var moved = point + new Vector(0, touch ? -120 : 25);
        if (touch) h.Window.TouchMove(contact!, moved); else h.Window.MouseMove(moved);
        h.Pump(); h.AssertVisible(); Assert.NotEqual(before, h.Scroll.Offset.Y);
        Assert.Equal(h.List.CurrentLetter, h.Window.FindControl<TextBlock>("CurrentArtistLetter")!.Text);
        if (touch) h.Window.TouchEnd(contact!, moved); else h.Window.MouseUp(moved, MouseButton.Left);
        h.Pump(); h.AssertVisible(); Assert.Same(selected, h.List.SelectedItem);
        var after = h.Scroll.Offset;
        for (var i = 0; i < 30; i++) h.Pump();
        Assert.Equal(after, h.Scroll.Offset);
    });

    [Fact]
    public Task NewInputAndRefreshCancelQueuedIndexAlignmentAndLatestClickWins() => Run(() =>
    {
        using var h = new Harness(Uneven());
        h.List.NavigateToLetter("M"); h.List.NavigateToLetter("B"); h.Pump();
        Assert.Equal("B", h.List.CurrentLetter); h.AssertVisible();
        h.List.NavigateToLetter("M");
        h.Window.MouseWheel(h.Scroll.TranslatePoint(new Point(40, 80), h.Window)!.Value, new Vector(0, -2));
        Assert.False(h.List.Diagnostics()["positionPending"]!.GetValue<bool>());
        h.Pump(); h.AssertVisible();
        h.List.NavigateToLetter("B"); h.Replace(["Charlie"]);
        Assert.Equal("C", h.List.CurrentLetter);
        Assert.False(h.List.Diagnostics()["positionPending"]!.GetValue<bool>());
        h.Window.ViewModel.Navigate(new NavTarget.Search("Charlie")); h.Pump();
        Assert.False(h.List.Diagnostics()["visible"]!.GetValue<bool>());
        h.Window.ViewModel.Navigate(new NavTarget.Artists(null)); h.Pump();
        Assert.Equal("C", h.List.CurrentLetter); h.AssertVisible();
    });

    [Fact]
    public Task FilterRefreshEmptyAndHiddenReopenNeverKeepAnOldLetterOrPendingTarget() => Run(() =>
    {
        using var h = new Harness(Uneven());
        h.ClickLetter("M");
        h.App.Db.SetTags([(h.Artists.First(a => a.IndexLetter == "B").Tracks[0].TrackKey, "MOOD", "SPORT", "MANUAL")]);
        h.App.Library.RebuildSnapshot(); h.Pump();
        h.Window.ViewModel.ToggleTag("SPORT"); h.Pump();
        Assert.Single(h.Artists); Assert.Equal("B", h.List.CurrentLetter); h.AssertVisible();
        h.App.Db.ClearFolder(h.FolderId); h.App.Library.RebuildSnapshot(); h.Pump();
        Assert.Empty(h.Artists); Assert.Empty(h.List.Groups); Assert.Null(h.List.CurrentLetter);
        Assert.DoesNotContain(h.Index.GetVisualDescendants().OfType<TextBlock>(), t => t.DataContext is ArtistLetter);
        Assert.Equal(-1, h.List.Diagnostics()["firstVisibleIndex"]!.GetValue<int>());
        h.Window.ViewModel.ClearFilterCommand.Execute(null);
        h.Replace(["Charlie", "Celine"]);
        Assert.Equal("C", h.List.CurrentLetter); Assert.Single(h.List.Groups);
        h.Window.Hide(); h.Pump();
        var offset = h.Scroll.Offset;
        h.Replace(["Zed", "Zoe"]);
        Assert.Null(h.List.CurrentLetter); Assert.Equal(offset, h.Scroll.Offset);
        h.Window.Show(); h.Pump();
        Assert.Equal("Z", h.List.CurrentLetter); h.AssertVisible();
        Assert.False(h.List.Diagnostics()["positionPending"]!.GetValue<bool>());
    });

    [Fact]
    public Task ThemesWidthsAndLayoutScalingKeepPromptIndexAndScrollbarSeparate() => Run(() =>
    {
        using var h = new Harness(Enumerable.Range('A', 26).SelectMany(letter => Enumerable.Range(0, 10).Select(i => $"{(char)letter} name {i}")).Append(""));
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (var width in new[] { 760, 1300 })
        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            h.Window.RequestedThemeVariant = theme; h.Window.Width = width;
            h.Window.Height = width == 760 ? 480 : 900;
            h.ScaleHost.LayoutTransform = new Avalonia.Media.ScaleTransform(scale, scale);
            h.Window.SetRenderScaling(scale);
            h.Pump(); h.ClickLetter("B"); h.AssertVisible();
            var listRight = h.List.TranslatePoint(new Point(h.List.Bounds.Width, 0), h.Window)!.Value.X;
            var indexLeft = h.Index.TranslatePoint(default, h.Window)!.Value.X;
            Assert.True(indexLeft >= listRight + 1);
            var indexTop = h.Index.TranslatePoint(default, h.Window)!.Value.Y;
            var indexBottom = h.Index.TranslatePoint(new Point(0, h.Index.Bounds.Height), h.Window)!.Value.Y;
            Assert.True(indexTop >= h.List.TranslatePoint(default, h.Window)!.Value.Y);
            Assert.True(indexBottom <= h.List.TranslatePoint(new Point(0, h.List.Bounds.Height), h.Window)!.Value.Y + .5);
            var prompt = h.Window.FindControl<TextBlock>("CurrentArtistLetter")!;
            var promptBottom = prompt.TranslatePoint(new Point(0, prompt.Bounds.Height), h.Window)!.Value.Y;
            Assert.True(promptBottom < h.List.TranslatePoint(default, h.Window)!.Value.Y);
            var active = h.Index.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("current"));
            Assert.Equal(h.List.CurrentLetter, active.Text);
            Assert.Equal(1, active.Opacity);
            Assert.Equal(.14, active.Background!.Opacity);
        }
    });

    [Fact]
    public Task DiagnosticsReadTheSameUiStateWithoutAddingToolsOrChangingSchema() => Run(() =>
    {
        using var h = new Harness(Uneven());
        var tools = new McpTools(h.App);
        h.ClickLetter("B");
        var d = tools.Call("get_diagnostics", new())["artistList"]!;
        Assert.True(d["created"]!.GetValue<bool>()); Assert.True(d["visible"]!.GetValue<bool>());
        Assert.Equal("B", d["currentLetter"]!.GetValue<string>());
        Assert.Equal(h.List.Groups.Count, d["groups"]!.AsArray().Count);
        Assert.Equal(h.List.Groups.Single(g => g.Letter == "B").FirstIndex, d["firstVisibleIndex"]!.GetValue<int>());
        Assert.Equal(25, tools.Catalog.Count);
        var schema = tools.Catalog.Single(t => t!["name"]!.GetValue<string>() == "get_diagnostics")!["inputSchema"]!;
        Assert.Empty(schema["properties"]!.AsObject()); Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        h.Window.Close(); h.Pump();
        Assert.False(tools.Call("get_diagnostics", new())["artistList"]!["created"]!.GetValue<bool>());
    });

    static IEnumerable<string> Uneven() => Enumerable.Range(0, 240).Select(i => $"Alice {i:000}")
        .Concat(["Bob 1", "Bob 2"]).Concat(Enumerable.Range(0, 75).Select(i => $"Mike {i:000}"))
        .Concat(["Zoe", "123", ""]);

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
        public AppServices App { get; }
        public MainWindow Window { get; }
        public LayoutTransformControl ScaleHost { get; }
        public ArtistListBox List => Window.FindControl<ArtistListBox>("ArtistList")!;
        public ItemsControl Index => Window.FindControl<ItemsControl>("LetterIndex")!;
        public ScrollViewer Scroll => List.GetVisualDescendants().OfType<ScrollViewer>().First();
        public ScrollContentPresenter Viewport => Scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        public IReadOnlyList<ArtistInfo> Artists => Window.ViewModel.Artists;
        public long FolderId { get; }
        public Harness(IEnumerable<string> names)
        {
            App = new AppServices(new SettingsStore(_settingsPath), new LibraryDb(":memory:"), source =>
                new PlayerService(source, options => { var e = new OutputModeSwitchTests.FakeEngine(options); _engines.Add(e); return e; }, _endpoint));
            FolderId = App.Db.AddFolder("fake-volume", "\\music\\", "C:\\mock-music\\", "Memory only");
            Window = new MainWindow(App) { ExitRequested = true };
            var content = (Control)Window.Content!;
            Window.Content = null;
            ScaleHost = new LayoutTransformControl { Child = content };
            Window.Content = ScaleHost;
            Window.Styles.Add(new StyleInclude(new Uri("avares://JeekMusic/")) { Source = new Uri("avares://JeekMusic/UI/Styles.axaml") });
            Replace(names);
            Window.ViewModel.Navigate(new NavTarget.Artists(null));
            Window.Show(); Pump();
        }
        public void Replace(IEnumerable<string> names)
        {
            App.Db.ClearFolder(FolderId);
            using var tx = App.Db.Begin();
            var i = 0;
            foreach (var name in names)
            {
                var file = $"{++i}.flac";
                App.Db.ReplaceFile(tx, FolderId, "", file, [new JeekMusic.Library.Track { FolderId = FolderId, TrackKey = file, Dir = "", FileName = file,
                    Title = file, ArtistDisplay = name, Artists = name.Length == 0 ? [] : [name] }]);
            }
            tx.Commit();
            App.Library.RebuildSnapshot(); Pump();
        }
        public void ClickLetter(string letter)
        {
            var text = Index.GetVisualDescendants().OfType<TextBlock>().Single(t => t.DataContext is ArtistLetter g && g.Letter == letter);
            var point = text.TranslatePoint(new Point(text.Bounds.Width / 2, text.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(point, MouseButton.Left); Window.MouseUp(point, MouseButton.Left); Pump();
        }
        public void Pump()
        {
            for (var i = 0; i < 8; i++) { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        }
        public void AssertVisible()
        {
            var visible = List.GetRealizedContainers().Select(c => (c, y: c.TranslatePoint(default, Viewport)!.Value.Y))
                .Where(p => p.y + p.c.Bounds.Height > .5 && p.y < Viewport.Bounds.Height - .5).OrderBy(p => p.y).ToArray();
            Assert.NotEmpty(visible);
            var first = List.IndexFromContainer(visible[0].c);
            Assert.Equal(Artists[first].IndexLetter, List.CurrentLetter);
            Assert.Equal(first, List.Diagnostics()["firstVisibleIndex"]!.GetValue<int>());
            Assert.Single(List.Groups, g => g.IsCurrent);
            Assert.Equal(List.CurrentLetter, List.Groups.Single(g => g.IsCurrent).Letter);
        }
        public void Dispose()
        {
            Window.ViewModel.Player.ProgressVisible = false; Window.Close(); App.Dispose();
            Assert.Empty(_endpoint.Writes); Assert.Empty(_engines); // Never cold-start even a simulated decoder.
            File.Delete(_settingsPath);
        }
    }
}
