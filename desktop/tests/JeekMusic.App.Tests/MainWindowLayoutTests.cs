using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic;
using JeekMusic.Automation;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.App.Tests;

// Real routed Headless pointer input, production MainWindow, memory DB and fake audio only.
[Collection("Avalonia UI")]
public sealed class MainWindowLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NavigationDragsBothWaysRepeatedlyWithArtistHiddenOrShown(bool artists) => Run(() =>
    {
        using var h = new Harness();
        if (artists) { h.Vm.Navigate(new NavTarget.Artists(null)); h.Pump(); }
        var initial = h.NavWidth;
        h.Drag(90); Assert.InRange(h.NavWidth - initial, 88, 92);
        h.Drag(-130); Assert.InRange(h.NavWidth - initial, -42, -38);
        h.Drag(65); Assert.InRange(h.NavWidth - initial, 23, 27);
        h.Drag(-20); h.Drag(20); Assert.InRange(h.NavWidth - initial, 23, 27);
        h.AssertBudget();
        Assert.Equal(3, h.Main.ColumnDefinitions.Count);
        Assert.True(h.Main.ColumnDefinitions[2].Width.IsStar);
        Assert.True(h.Right.ColumnDefinitions[1].Width.IsStar);
    });

    [Theory]
    [InlineData(760, false, false)]
    [InlineData(760, true, false)]
    [InlineData(760, false, true)]
    [InlineData(760, true, true)]
    [InlineData(1200, true, true)]
    public Task BoundariesAndAuxiliaryPanesKeepUsefulWidthsAndRemainAdjustable(double width, bool artists, bool lyrics) => Run(() =>
    {
        using var h = new Harness(width);
        if (artists) h.Vm.Navigate(new NavTarget.Artists(null));
        if (lyrics) h.Lyrics(true);
        h.Pump(); h.AssertBudget();
        h.Drag(-2000); Assert.InRange(h.NavWidth, 159.5, 160.5); h.AssertBudget();
        h.Drag(2000); var maximum = h.Main.ColumnDefinitions[0].MaxWidth;
        Assert.InRange(h.NavWidth, maximum - 1, maximum + 1); h.AssertBudget();
        h.Drag(-10); Assert.True(h.NavWidth < maximum - 5);
        h.Window.Width = 1500; h.Pump(); h.Drag(80); h.AssertBudget();
        h.Window.Width = width; h.Pump(); h.AssertBudget();
        h.Drag(-2000); h.Drag(12); Assert.InRange(h.NavWidth, 171, 173);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e9)]
    public Task InvalidStoredWidthRecoversAndPreservesOtherLayoutFields(double width) => Run(() =>
    {
        using var h = new Harness(state: new UiState { NavWidth = width, SongColumnsVersion = 1,
            Columns = [new ColumnState { Tag = "Title", Width = 287, Order = 0, Visible = true }] });
        h.AssertBudget(); Assert.InRange(h.NavWidth, 160, 540);
        var state = h.Save(); Assert.True(double.IsFinite(state.NavWidth));
        Assert.Equal(287, state.Columns.Single(c => c.Tag == "Title").Width);
        h.Drag(-20); h.Drag(30); h.AssertBudget();
    });

    [Theory]
    [InlineData("null")]
    [InlineData("\"NaN\"")]
    [InlineData("\"bad\"")]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    [InlineData("1e999")]
    public void BadNavigationJsonDoesNotDiscardTheRestOfSnapshot(string value)
    {
        var json = "{\"navWidth\":" + value + ",\"width\":1400,\"viewId\":19,\"lyricsPanel\":true,\"collapsedFolders\":[\"folders:v1\"]}";
        var state = JsonSerializer.Deserialize(json, UiStateJson.Default.UiState)!;
        Assert.Equal(230, state.NavWidth); Assert.Equal(1400, state.Width); Assert.Equal(19, state.ViewId);
        Assert.True(state.LyricsPanel); Assert.Single(state.CollapsedFolders);
        Assert.Equal(230, RoundTrip(state).NavWidth);
    }

    [Fact]
    public Task LegalWidePreferenceSurvivesCollapseJsonRestartAndTemporarySmallWindow() => Run(() =>
    {
        UiState state;
        using (var h = new Harness(state: new UiState { NavWidth = 500 }))
        {
            Assert.Equal(500, h.NavWidth, 1);
            h.Window.Width = 760; h.Pump(); h.AssertBudget();
            h.Window.Width = 1200; h.Pump(); Assert.Equal(500, h.NavWidth, 1);
            h.Click(h.Control<Button>("NavCollapse")); Assert.Equal(56, h.NavWidth, 1);
            Assert.False(h.Control<GridSplitter>("NavSplitter").IsVisible);
            state = RoundTrip(h.Save()); Assert.Equal(500, state.NavWidth, 1);
            h.Click(h.Control<Button>("NavCollapse")); Assert.Equal(500, h.NavWidth, 1);
        }
        using var restarted = new Harness(state: state);
        Assert.Equal(500, restarted.NavWidth, 1); restarted.Drag(-50); restarted.Drag(25); restarted.AssertBudget();
    });

    [Fact]
    public Task UserExpansionFitsVisibleTextButIgnoresCollapsedDescendantsAndDoesNotShrink() => Run(() =>
    {
        using var h = new Harness();
        h.Toggle(h.Root); // collapse first, no auto shrink
        h.Drag(-2000); var minimum = h.NavWidth;
        h.Toggle(h.Root); Assert.True(h.NavWidth > minimum + 30);
        var grown = h.NavWidth;
        Assert.InRange(h.D["lastMeasuredFolderCount"]!.GetValue<int>(), 4, 4);
        h.Toggle(h.Root); Assert.Equal(grown, h.NavWidth, 1);
        h.Drag(-2000); h.Toggle(h.Root); h.Toggle(h.Long);
        Assert.InRange(h.NavWidth, h.Main.ColumnDefinitions[0].MaxWidth - 1, h.Main.ColumnDefinitions[0].MaxWidth + 1);
        Assert.True(h.D["lastMeasuredContentWidth"]!.GetValue<double>() > h.NavWidth);
        Assert.Contains(h.D["automaticWidthResult"]!.GetValue<string>(), new[] { "grown-to-limit", "at-limit" });
        h.Drag(-50); var manual = h.NavWidth; h.Pump(); h.Pump(); Assert.Equal(manual, h.NavWidth, 1);
        h.Toggle(h.Long); Assert.Equal(manual, h.NavWidth, 1);
        h.Drag(20); Assert.Equal(manual + 20, h.NavWidth, 1);
    });

    [Fact]
    public Task ShortNamesAlreadyFitAndActualFontAndIndentContributeToMeasurement() => Run(() =>
    {
        using var h = new Harness();
        var longNode = h.Long;
        h.Root.Children.Remove(longNode); h.Pump();
        h.Toggle(h.Short); h.Toggle(h.Short); // a short leaf's parent adds only short content
        Assert.Equal(230, h.NavWidth, 1);
        h.Root.Children.Add(longNode); h.Pump();
        h.Toggle(h.Root); h.Window.FontSize = 20; h.Pump(); h.Toggle(h.Root);
        var large = h.D["lastMeasuredContentWidth"]!.GetValue<double>();
        h.Toggle(h.Root); h.Window.FontSize = 11; h.Pump(); h.Drag(-2000); h.Toggle(h.Root);
        Assert.True(large > h.D["lastMeasuredContentWidth"]!.GetValue<double>() + 20);
        h.Toggle(h.Long);
        Assert.True(h.D["lastMeasuredContentWidth"]!.GetValue<double>() > large);
    });

    [Fact]
    public Task QuickExpansionAndManualInputInvalidateStaleCallbacksAndLocateDoesNotFit() => Run(() =>
    {
        using var h = new Harness();
        h.Toggle(h.Root); h.Drag(-2000);
        h.Toggle(h.Root, pump: false); h.Toggle(h.Root, pump: false); h.Pump();
        Assert.False(h.Root.IsExpanded);
        var afterRapid = h.NavWidth; h.Pump(); Assert.Equal(afterRapid, h.NavWidth, 1);
        h.Drag(-2000);
        h.Toggle(h.Root, pump: false);
        // Process the input observer, leaving its lower-priority fit queued.
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Normal);
        h.Drag(30); var manual = h.NavWidth; h.Pump(); Assert.Equal(manual, h.NavWidth, 1);
        h.Toggle(h.Root); var collapsed = h.Save().CollapsedFolders;
        h.Vm.Navigate(new NavTarget.Folder(h.RootId, "Short\\Leaf\\"));
        h.Vm.RevealCurrentFolderPath(); h.Pump();
        Assert.Equal(manual, h.NavWidth, 1); Assert.Equal(collapsed, h.Save().CollapsedFolders);
    });

    [Fact]
    public Task QueuedExpansionReadsLatestRebuiltTreeAndClosedWindowCannotResize() => Run(() =>
    {
        using var h = new Harness();
        h.Toggle(h.Root); h.Drag(-2000); h.Toggle(h.Root, pump: false);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Normal);
        h.App.Library.RebuildSnapshot(); h.Pump(); h.AssertBudget();
        Assert.False(h.D["automaticWidthPending"]!.GetValue<bool>());
        h.Toggle(h.Root); h.Toggle(h.Root, pump: false);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Normal);
        h.Close(); h.Pump(); Assert.False(h.D["created"]!.GetValue<bool>());
    });

    [Theory]
    [InlineData(false, "zh-Hans", 1.0)]
    [InlineData(false, "en", 1.25)]
    [InlineData(true, "en", 1.5)]
    [InlineData(true, "zh-Hans", 2.0)]
    public Task ThemesNarrowTitleBarAndScaledLayoutKeepSettingsAndSplitterTargetsSeparate(bool dark, string language, double scale) => Run(() =>
    {
        using var h = new Harness(760, language: language);
        h.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        h.Window.SetRenderScaling(scale);
        h.Pump();
        Assert.Equal(scale, h.D["renderScaling"]!.GetValue<double>());
        var settings = h.Control<Button>("SettingsButton");
        var title = h.Control<Grid>("TitleBar");
        Assert.Same(title, settings.Parent); Assert.Equal(3, Grid.GetColumn(settings));
        Assert.Equal(WindowDecorationsElementRole.User, WindowDecorationProperties.GetElementRole(settings));
        Assert.True(settings.Focusable); Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(settings)));
        var p = settings.TranslatePoint(default, title)!.Value;
        Assert.True(p.X >= 0 && p.X + settings.Bounds.Width <= title.Bounds.Width - 150 + .5);
        Assert.True(h.Control<TextBox>("SearchBox").Bounds.Width > 0);
        var splitter = h.Control<GridSplitter>("NavSplitter");
        Assert.InRange(splitter.Bounds.Width, 6, 7);
        h.Window.MouseMove(splitter.TranslatePoint(new Point(3, 100), h.Window)!.Value); h.Pump();
        Assert.True(splitter.IsPointerOver); Assert.Equal("SizeWestEast", splitter.Cursor!.ToString());
        Assert.NotNull(splitter.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Width == 1 && b.Background is not null));
        var before = h.NavWidth;
        h.Drag(-15); Assert.InRange(before - h.NavWidth, 14, 16);
        h.Drag(15); Assert.InRange(h.NavWidth, before - 1, before + 1); h.AssertBudget();
    });

    [Fact]
    public Task LyricsSplitterStillDragsBothWaysAndResponsiveShrinkDoesNotOverwritePreference() => Run(() =>
    {
        using var h = new Harness(); h.Lyrics(true);
        var splitter = h.Control<GridSplitter>("LyricsSplitter");
        var initial = h.Right.ColumnDefinitions[3].ActualWidth;
        DragLyrics(-45); Assert.InRange(h.Right.ColumnDefinitions[3].ActualWidth - initial, 44, 46);
        DragLyrics(75); Assert.InRange(h.Right.ColumnDefinitions[3].ActualWidth - initial, -31, -29);
        var preferred = RoundTrip(h.Save()).LyricsWidth;
        h.Window.Width = 760; h.Pump(); h.AssertBudget();
        Assert.Equal(preferred, h.Save().LyricsWidth, 1);
        h.Window.Width = 1200; h.Pump(); Assert.Equal(preferred, h.Right.ColumnDefinitions[3].ActualWidth, 1);
        void DragLyrics(double delta)
        {
            var start = splitter.TranslatePoint(new Point(3, 100), h.Window)!.Value;
            h.Window.MouseDown(start, MouseButton.Left); h.Window.MouseMove(start + new Vector(delta, 0)); h.Pump();
            h.Window.MouseUp(start + new Vector(delta, 0), MouseButton.Left); h.Pump();
        }
    });

    [Fact]
    public Task LyricsFullPageAndReturnKeepColumnsAndNavigationResizable() => Run(() =>
    {
        using var h = new Harness(760);
        h.Vm.Navigate(new NavTarget.Artists(null)); h.Pump();
        h.Window.RestoreLayout(new UiState { LyricsPanel = true, LyricsFullPage = true }); h.Pump();
        Assert.False(h.Control<Grid>("SongPanel").IsVisible);
        Assert.Equal(0, h.Right.ColumnDefinitions[0].ActualWidth);
        h.Drag(30); h.Drag(-30);
        var fullPageButton = h.Control<ContentControl>("LyricsHost").GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(ToolTip.GetTip(b), L.T("切换整页 / 侧栏")));
        h.Click(fullPageButton);
        Assert.True(h.Control<Grid>("SongPanel").IsVisible);
        h.AssertBudget(); h.Drag(-10); h.Drag(10); h.AssertBudget();
    });

    [Fact]
    public Task DiagnosticsAreReadOnlyCompatibleAndDoNotCreateOrRetainWindows() => Run(() =>
    {
        using var h = new Harness(show: false);
        var before = h.Save(); var count = h.Tools.Catalog.Count;
        for (var i = 0; i < 3; i++) Assert.True(h.D["created"]!.GetValue<bool>());
        Assert.Equal(before.NavWidth, h.Save().NavWidth);
        Assert.False(h.Window.IsVisible); Assert.False(h.App.Player.EngineReady);
        Assert.Equal(24, count);
        var schema = h.Tools.Catalog.Single(t => t!["name"]!.GetValue<string>() == "get_diagnostics")!["inputSchema"]!;
        Assert.Empty(schema["properties"]!.AsObject()); Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        h.Window.Show(); h.Pump(); h.AssertBudget(); h.Close();
        Assert.False(h.D["created"]!.GetValue<bool>()); Assert.Null(h.App.MainLayoutDiagnostics);
    });

    [Fact]
    public async Task RenderRealFontsAndSplitterInLightDarkAndNarrowWindows()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(() =>
        {
            using var h = new Harness();
            foreach (var dark in new[] { false, true })
            foreach (var width in new[] { 1200, 760 })
            {
                h.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                // Headless has no Windows Mica compositor behind a transparent window.
                h.Window.Background = dark ? new SolidColorBrush(Color.Parse("#202020")) : Brushes.White;
                h.Window.Width = width; h.Pump();
                h.Toggle(h.Root); h.Toggle(h.Root); h.Pump(); h.AssertBudget();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = h.Window.CaptureRenderedFrame();
                Assert.NotNull(bitmap); Assert.Equal(width, bitmap.PixelSize.Width);
                if (Environment.GetEnvironmentVariable("JEEKMUSIC_TEST_ARTIFACTS") is { Length: > 0 } output)
                {
                    Directory.CreateDirectory(output);
                    bitmap.Save(Path.Combine(output, $"layout-{(dark ? "dark" : "light")}-{width}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
        }, CancellationToken.None);
    }

    static UiState RoundTrip(UiState state) => JsonSerializer.Deserialize(JsonSerializer.Serialize(state, UiStateJson.Default.UiState), UiStateJson.Default.UiState)!;
    static async Task Run(Action action)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(action, CancellationToken.None);
    }

    sealed class Harness : IDisposable
    {
        readonly string _settings = Path.Combine(Path.GetTempPath(), "jeek-layout-" + Guid.NewGuid() + ".json");
        readonly OutputModeSwitchTests.FakeEndpointVolume _endpoint = new();
        readonly List<OutputModeSwitchTests.FakeEngine> _engines = [];
        bool _closed;
        public AppServices App { get; }
        public MainWindow Window { get; }
        public MainViewModel Vm => Window.ViewModel;
        public McpTools Tools { get; }
        public JsonNode D => Tools.Call("get_diagnostics", new())["mainLayout"]!;
        public Grid Main => Control<Grid>("MainArea");
        public Grid Right => Control<Grid>("RightArea");
        public double NavWidth => Main.ColumnDefinitions[0].ActualWidth;
        public long RootId { get; }
        public NavNode Root => Vm.Nav.Single(n => n.Kind == NavKind.Folders).Children.Single();
        public NavNode Long => Root.Children.Single(n => n.Title != "Short");
        public NavNode Short => Root.Children.Single(n => n.Title == "Short");
        public Harness(double width = 1200, UiState? state = null, bool show = true, string language = "zh-Hans")
        {
            var settings = new SettingsStore(_settings); settings.Current.Language = language;
            App = new AppServices(settings, new LibraryDb(":memory:"), source => new PlayerService(source,
                options => { var e = new OutputModeSwitchTests.FakeEngine(options); _engines.Add(e); return e; }, _endpoint));
            Tools = new McpTools(App); Assert.False(D["created"]!.GetValue<bool>());
            RootId = App.Db.AddFolder("fake-volume", "\\layout\\", "C:\\mock-layout\\", "Layout");
            using (var tx = App.Db.Begin())
            {
                var index = 0;
                foreach (var dir in new[] { "Short\\Leaf\\", "Visible directory\\" + new string('W', 100) + "\\Deep\\" })
                {
                    var file = $"{++index}.flac";
                    App.Db.ReplaceFile(tx, RootId, dir, file, [new JeekMusic.Library.Track { FolderId = RootId, TrackKey = file, Dir = dir, FileName = file, Title = file, ArtistDisplay = "Test", Artists = ["Test"] }]);
                }
                tx.Commit();
            }
            Window = new MainWindow(App) { Width = width, Height = 650, ExitRequested = true };
            Window.Styles.Add(new StyleInclude(new Uri("avares://JeekMusic/")) { Source = new Uri("avares://JeekMusic/UI/Styles.axaml") });
            if (state is not null) Window.RestoreLayout(state);
            App.Library.RebuildSnapshot(); Pump();
            Long.IsExpanded = false; Short.IsExpanded = false; // fixture setup, not the action under test
            Vm.Navigate(new NavTarget.Folder(RootId, ""));
            if (show) Window.Show(); Pump();
        }
        public T Control<T>(string name) where T : Control => Window.FindControl<T>(name)!;
        public void Pump() { for (var i = 0; i < 8; i++) { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
        public UiState Save() { var s = new UiState(); Window.SaveLayout(s); return s; }
        public void Click(Control target, bool pump = true)
        {
            var p = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(p, MouseButton.Left); Window.MouseUp(p, MouseButton.Left); if (pump) Pump();
        }
        public void Toggle(NavNode node, bool pump = true)
        {
            var item = Control<TreeView>("NavTree").GetVisualDescendants().OfType<TreeViewItem>().Single(t => ReferenceEquals(t.DataContext, node));
            Click(item.GetVisualDescendants().OfType<ToggleButton>().Single(b => ReferenceEquals(b.TemplatedParent, item)), pump);
        }
        public void Drag(double delta, double scale = 1)
        {
            var splitter = Control<GridSplitter>("NavSplitter");
            var start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, 100), Window)!.Value;
            Window.MouseMove(start); Window.MouseDown(start, MouseButton.Left);
            // Multiple actual moves/layout passes expose direction locks and feedback loops.
            for (var step = 1; step <= 3; step++) { Window.MouseMove(start + new Vector(delta * scale * step / 3, 0)); Pump(); }
            Window.MouseUp(start + new Vector(delta * scale, 0), MouseButton.Left); Pump();
        }
        public void Lyrics(bool enabled) { Control<ToggleButton>("LyricsToggle").IsChecked = enabled; Pump(); }
        public void AssertBudget()
        {
            Assert.InRange(NavWidth, 159.4, Main.ColumnDefinitions[0].MaxWidth + 1);
            Assert.True(NavWidth <= Main.Bounds.Width * .45 + 1);
            var songs = Control<Grid>("SongPanel");
            if (songs.IsVisible) Assert.True(songs.Bounds.Width >= 243, $"song width={songs.Bounds.Width}; {D}");
            Assert.True(Right.Bounds.Right <= Main.Bounds.Width + 1, D.ToJsonString());
        }
        public void Close() { if (_closed) return; _closed = true; Vm.Player.ProgressVisible = false; Window.Close(); Pump(); }
        public void Dispose()
        {
            Close(); App.Dispose(); Assert.Empty(_engines); Assert.Empty(_endpoint.Writes); File.Delete(_settings);
        }
    }
}

public class LayoutRenderingApplication : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<LayoutRenderingApplication>().UseSkia()
        .With(Typography.FontOptions()).UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    public override void Initialize()
    {
        Resources["ContentControlThemeFontFamily"] = FontFamily.Default;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Avalonia.Controls.DataGrid/"))
            { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        Styles.Add(new StyleInclude(new Uri("avares://JeekMusic/"))
            { Source = new Uri("avares://JeekMusic/UI/Styles.axaml") });
    }
}
