using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
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

// Production window, real shaped fonts, routed mouse input, memory library/fake audio.
[Collection("Avalonia UI")]
public sealed class NavigationOverflowTests
{
    [Fact]
    public Task OnlyClippedTextRevealsAfterDelayWithoutResizingOrChangingSelection() => Run(async () =>
    {
        using var h = new Harness();
        var before = h.Save(); var selected = h.Vm.SelectedNav;
        h.Hover(h.Short); await h.WaitDelay(); Assert.False(h.Visible);
        h.Hover(h.Long); Assert.True(h.D["pending"]!.GetValue<bool>()); Assert.False(h.Visible);
        await Task.Delay(100); h.Pump(); Assert.False(h.Visible);
        await h.WaitDelay(); Assert.True(h.Visible, h.D.ToJsonString());
        Assert.Equal(h.Long.Title, h.RevealText.Text);
        Assert.Same(selected, h.Vm.SelectedNav); Assert.Equal(before.NavWidth, h.Save().NavWidth);
        Assert.Equal(before.CollapsedFolders, h.Save().CollapsedFolders);
        Assert.False(h.Surface.IsHitTestVisible); Assert.False(h.Surface.Focusable);
        h.Window.MouseMove(new Point(700, 400)); h.Pump(); Assert.False(h.Visible);
    });

    [Theory]
    [InlineData(false, false, 1.0)]
    [InlineData(true, true, 1.25)]
    [InlineData(false, true, 1.5)]
    [InlineData(true, false, 2.0)]
    public Task TextOriginTypographyAndSelectedHoverColoursMatchOriginal(bool dark, bool selected, double scale) => Run(async () =>
    {
        using var h = new Harness();
        h.Window.SetRenderScaling(scale); h.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        h.Window.Background = dark ? new SolidColorBrush(Color.Parse("#202020")) : Brushes.White;
        if (selected) h.Vm.SelectedNav = h.Long;
        h.Pump(); h.Hover(h.Long); await h.WaitDelay(); Assert.True(h.Visible, h.D.ToJsonString());
        var original = h.Name(h.Long); var clone = h.RevealText;
        Assert.Equal(original.FontFamily, clone.FontFamily); Assert.Equal(original.FontSize, clone.FontSize);
        Assert.Equal(original.FontWeight, clone.FontWeight); Assert.Equal(original.FontStyle, clone.FontStyle);
        Assert.Same(original.Foreground, clone.Foreground); Assert.Equal(original.LetterSpacing, clone.LetterSpacing);
        var origin = original.TranslatePoint(default, h.Window)!.Value;
        var floated = clone.TranslatePoint(default, h.Window)!.Value;
        Assert.InRange(Math.Abs(origin.X - floated.X), 0, 1 / scale + .01);
        Assert.InRange(Math.Abs(origin.Y - floated.Y), 0, 1 / scale + .01);
        Assert.False(h.D["wrapped"]!.GetValue<bool>());
        var header = h.Row(h.Long).GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_LayoutRoot" && ReferenceEquals(b.TemplatedParent, h.Row(h.Long)));
        Assert.Same(header.Background, ((Border)h.Surface.Child!).Background);
        h.Capture($"overflow-{(dark ? "dark" : "light")}-{(selected ? "selected" : "hover")}-{scale}");
        // Click through the overlay still selects/navigates the original row.
        h.Click(original); Assert.Same(h.Long, h.Vm.SelectedNav); Assert.False(h.Visible);
    });

    [Fact]
    public Task AncestorViewportClippingRevealsEvenWhenTextItselfHasEnoughWidth() => Run(async () =>
    {
        using var h = new Harness();
        var text = h.Name(h.Long);
        text.Width = 600; text.TextTrimming = TextTrimming.None; h.Pump();
        Assert.True(text.Bounds.Width > 500);
        h.Hover(h.Long); await h.WaitDelay(); Assert.True(h.Visible, h.D.ToJsonString());
        Assert.True(h.D["naturalWidth"]!.GetValue<double>() < text.Bounds.Width);
        Assert.True(h.D["visibleTextWidth"]!.GetValue<double>() < h.D["naturalWidth"]!.GetValue<double>());
    });

    [Fact]
    public Task EnglishPlaylistAndChineseNestedFoldersShareTheSameRevealWithoutOpeningBranches() => Run(async () =>
    {
        using var h = new Harness();
        var playlist = h.Vm.Nav.Single(n => n.Kind == NavKind.Playlists);
        playlist.IsExpanded = true; h.Pump();
        var english = playlist.Children.Single();
        h.Hover(english); await h.WaitDelay(); Assert.True(h.Visible); Assert.Equal(english.Title, h.RevealText.Text);
        h.Window.MouseMove(new Point(700, 400)); h.Pump();
        h.Long.IsExpanded = true;
        var branch = h.Long.Children.Single(); branch.IsExpanded = false; h.Pump();
        h.Tree.ScrollIntoView(branch); h.Pump(); h.Hover(branch); await h.WaitDelay();
        Assert.True(h.Visible, h.D.ToJsonString()); Assert.False(branch.IsExpanded);
        Assert.Equal(branch.Title, h.RevealText.Text);
    });

    [Fact]
    public Task VeryLongNameWrapsWithoutScalingAndRemainsWithinClientAndScreen() => Run(async () =>
    {
        using var h = new Harness(760);
        var row = h.Long;
        var text = h.Name(row); text.Text = string.Concat(Enumerable.Repeat("很长的目录Name", 18)); h.Pump();
        h.Hover(row); await h.WaitDelay(); Assert.True(h.Visible);
        Assert.True(h.D["wrapped"]!.GetValue<bool>()); Assert.Equal(text.FontSize, h.RevealText.FontSize);
        Assert.Equal(text.Text, h.RevealText.Text); Assert.Equal(TextTrimming.None, h.RevealText.TextTrimming);
        var bounds = new Rect(h.Surface.Bounds.Size).TransformToAABB(h.Surface.TransformToVisual(h.Window)!.Value);
        Assert.InRange(bounds.Left, 0, h.Window.ClientSize.Width); Assert.True(bounds.Right <= h.Window.ClientSize.Width);
        Assert.True(bounds.Bottom <= h.Window.ClientSize.Height); Assert.True(bounds.Top >= 0);
        Assert.True(h.RevealText.Bounds.Height > text.Bounds.Height);
        h.Capture("overflow-long-wrapped-760");
    });

    [Theory]
    [InlineData("wheel")]
    [InlineData("resize")]
    [InlineData("splitter")]
    [InlineData("collapse")]
    [InlineData("refresh")]
    [InlineData("detach")]
    [InlineData("inactive")]
    [InlineData("close")]
    public Task LifecycleAndInputChangesCloseVisibleAndPendingReveal(string change) => Run(async () =>
    {
        using var h = new Harness();
        h.Hover(h.Long); await h.WaitDelay(); Assert.True(h.Visible);
        Change(); h.Pump(); Assert.False(h.Visible);
        if (change == "close") { Assert.Null(h.App.MainLayoutDiagnostics); return; }
        Assert.False(h.D["pending"]!.GetValue<bool>());
        if (change is "wheel" or "resize" or "splitter")
        {
            h.Tree.ScrollIntoView(h.Long); h.Pump(); h.Hover(h.Long); Assert.True(h.D["pending"]!.GetValue<bool>());
            Change(); await h.WaitDelay(); Assert.False(h.Visible); Assert.False(h.D["pending"]!.GetValue<bool>());
        }
        void Change()
        {
            switch (change)
            {
                case "wheel": h.Window.MouseWheel(h.Name(h.Long).TranslatePoint(default, h.Window)!.Value, new Vector(0, -1)); break;
                case "resize": h.Window.Width -= 30; break;
                case "splitter": h.Drag(10); break;
                case "collapse": h.Click(h.Control<Button>("NavCollapse")); break;
                case "refresh": h.App.Library.RebuildSnapshot(); break;
                case "detach": h.Root.Children.Remove(h.Long); break;
                case "inactive":
                    // Drive the callback registered with the Headless backend; the
                    // interface member is internal in Avalonia 12. No production hook.
                    ((Action)h.Window.PlatformImpl!.GetType().GetProperty("Deactivated")!.GetValue(h.Window.PlatformImpl)!).Invoke();
                    break;
                case "close": h.Close(); break;
            }
        }
    });

    [Fact]
    public Task ScrolledContainersAndChangedTextCannotLeaveStaleOverlay() => Run(async () =>
    {
        using var h = new Harness();
        h.Hover(h.Long); await h.WaitDelay(); Assert.True(h.Visible);
        var scroll = h.Tree.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, 200); h.Pump(); Assert.False(h.Visible);
        scroll.Offset = default; h.Pump(); h.Hover(h.Long); await h.WaitDelay(); Assert.True(h.Visible);
        h.Name(h.Long).Text = "changed"; h.Pump(); Assert.False(h.Visible);
        h.Hover(h.Short); await h.WaitDelay(); Assert.False(h.Visible);
    });

    [Fact]
    public Task OverlayDoesNotStealDoubleClickRightClickWheelOrFocus() => Run(async () =>
    {
        using var h = new Harness();
        var row = h.Long; h.Vm.SelectedNav = row; h.Pump();
        h.Name(row).Focus(); var focus = h.Window.FocusManager!.GetFocusedElement();
        h.Hover(row); await h.WaitDelay(); Assert.True(h.Visible);
        Assert.Same(focus, h.Window.FocusManager.GetFocusedElement());
        var downs = 0; var wheels = 0;
        h.Tree.AddHandler(InputElement.PointerPressedEvent, (_, _) => downs++, RoutingStrategies.Tunnel, true);
        h.Tree.AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => wheels++, RoutingStrategies.Tunnel, true);
        h.Click(h.Name(row)); h.Click(h.Name(row)); h.Pump(); Assert.Equal(2, downs);
        h.Hover(row); await h.WaitDelay();
        h.Click(h.Name(row), MouseButton.Right); h.Pump(); Assert.Equal(3, downs); Assert.False(h.Visible);
        h.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        h.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null); h.Pump();
        h.Window.MouseWheel(h.Name(row).TranslatePoint(new Point(2, 2), h.Window)!.Value, new Vector(0, -1));
        h.Pump(); Assert.Equal(1, wheels);
    });

    [Fact]
    public Task DiagnosticsReadOnlyKeepTwentyTwoToolsAndReleaseReferencesOnClose() => Run(async () =>
    {
        using var h = new Harness(); var width = h.Save().NavWidth;
        Assert.Equal(22, h.Tools.Catalog.Count);
        var schema = h.Tools.Catalog.Single(t => t!["name"]!.GetValue<string>() == "get_diagnostics")!["inputSchema"]!;
        Assert.Empty(schema["properties"]!.AsObject()); Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        h.Hover(h.Long);
        for (var i = 0; i < 3; i++) Assert.False(h.D["visible"]!.GetValue<bool>());
        await h.WaitDelay(); Assert.True(h.Visible); Assert.Equal(width, h.Save().NavWidth);
        h.Close(); Assert.Null(h.App.MainLayoutDiagnostics);
        Assert.False(h.Tools.Call("get_diagnostics", new())["mainLayout"]!["created"]!.GetValue<bool>());
    });

    [Fact]
    public Task ZeroWidthDeepNameRevealsFromRowAndCountKeepsItsSecondaryStyle() => Run(async () =>
    {
        using var h = new Harness();
        var name = h.Name(h.Long); name.Width = 0; h.Pump();
        var row = h.Row(h.Long);
        var icon = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("nav-icon") && ReferenceEquals(t.DataContext, h.Long));
        var point = icon.TranslatePoint(new Point(2, 2), h.Window)!.Value;
        h.Window.MouseMove(point); h.Pump(); await h.WaitDelay(); Assert.True(h.Visible);
        Assert.Equal(h.Long.Title, h.RevealText.Text);
        h.Window.MouseMove(new Point(700, 400)); h.Pump();
        name.Width = 20;
        var count = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("nav-count") && ReferenceEquals(t.DataContext, h.Long));
        count.Text = "123456789"; count.Width = 20; h.Pump();
        point = count.TranslatePoint(new Point(2, 2), h.Window)!.Value;
        Assert.Same(row, (h.Window.InputHitTest(point) as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true));
        h.Window.MouseMove(point); h.Pump(); await h.WaitDelay(); Assert.True(h.Visible);
        Assert.Equal(count.Text, h.RevealText.Text); Assert.Equal(count.FontSize, h.RevealText.FontSize);
        Assert.Equal(count.Opacity, h.RevealText.Opacity); Assert.Same(count.Foreground, h.RevealText.Foreground);
    });

    [Fact]
    public Task TextLongerThanScreenRemainsReadableAtOriginalSizeWithClickThroughReading() => Run(async () =>
    {
        using var h = new Harness(760);
        var name = h.Name(h.Long); name.Text = string.Concat(Enumerable.Repeat("全文内容Long name ", 500)); h.Pump();
        h.Hover(h.Long); await h.WaitDelay(); Assert.True(h.Visible);
        Assert.True(h.D["automaticReading"]!.GetValue<bool>());
        Assert.Equal(name.Text, h.RevealText.Text); Assert.Equal(name.FontSize, h.RevealText.FontSize);
        await Task.Delay(1300); h.Pump(); Assert.True(h.D["readingOffset"]!.GetValue<double>() > 0);
        Assert.False(h.Surface.IsHitTestVisible);
        h.Window.MouseWheel(name.TranslatePoint(new Point(2, 2), h.Window)!.Value, new Vector(0, -1));
        h.Pump(); Assert.False(h.Visible); Assert.False(h.D["automaticReading"]!.GetValue<bool>());
    });

    [Fact]
    public Task QuickHoverChangesCancelOldDelayAndMovingInsideTheRowDoesNotFlicker() => Run(async () =>
    {
        using var h = new Harness();
        h.Hover(h.Long); Assert.True(h.D["pending"]!.GetValue<bool>());
        h.Hover(h.Short); await h.WaitDelay(); Assert.False(h.Visible);
        h.Hover(h.Long); await h.WaitDelay(); Assert.True(h.Visible);
        var surface = h.Surface;
        var point = h.Name(h.Long).TranslatePoint(new Point(12, 4), h.Window)!.Value;
        h.Window.MouseMove(point); h.Pump(); Assert.Same(surface, h.Surface);
        Assert.False(h.D["pending"]!.GetValue<bool>());
        h.Window.MouseMove(new Point(600, point.Y)); h.Pump(); Assert.False(h.Visible);
    });

    static async Task Run(Func<Task> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(async () => { await test(); return 0; }, CancellationToken.None);
    }

    sealed class Harness : IDisposable
    {
        readonly string _path = Path.Combine(Path.GetTempPath(), "jeek-overflow-" + Guid.NewGuid() + ".json");
        readonly OutputModeSwitchTests.FakeEndpointVolume _endpoint = new();
        readonly List<OutputModeSwitchTests.FakeEngine> _engines = [];
        bool _closed;
        public AppServices App { get; }
        public MainWindow Window { get; }
        public MainViewModel Vm => Window.ViewModel;
        public McpTools Tools { get; }
        public JsonNode D => Tools.Call("get_diagnostics", new())["mainLayout"]!["navigationOverflow"]!;
        public bool Visible => !_closed && D["visible"]!.GetValue<bool>();
        public TreeView Tree => Control<TreeView>("NavTree");
        public NavNode Root => Vm.Nav.Single(n => n.Kind == NavKind.Folders).Children.Single();
        public NavNode Long => Root.Children.Single(n => n.Title.StartsWith("a中文"));
        public NavNode Short => Root.Children.Single(n => n.Title == "b短");
        public Border Surface => OverlayLayer.GetOverlayLayer(Tree)!.Children.OfType<Border>().Single(b => b.Name == "NavigationOverflowReveal");
        public TextBlock RevealText => Surface.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "NavigationOverflowText");
        public Harness(double width = 1200)
        {
            App = new AppServices(new SettingsStore(_path), new LibraryDb(":memory:"), source => new PlayerService(source,
                options => { var e = new OutputModeSwitchTests.FakeEngine(options); _engines.Add(e); return e; }, _endpoint));
            Tools = new McpTools(App); Assert.False(Tools.Call("get_diagnostics", new())["mainLayout"]!["created"]!.GetValue<bool>());
            var folder = App.Db.AddFolder("fake-volume", "\\reveal\\", "C:\\mock-reveal\\", "Reveal");
            App.Playlists.Create("Long English playlist name for overflow reveal");
            using (var tx = App.Db.Begin())
            {
                var dirs = new[] { "a中文目录与 English name 保持原字体样式\\深层目录名称也需要完整阅读\\Child\\", "b短\\" }
                    .Concat(Enumerable.Range(0, 45).Select(i => $"z-row-{i:D2}\\"));
                var i = 0;
                foreach (var dir in dirs)
                {
                    var file = $"{++i}.flac";
                    App.Db.ReplaceFile(tx, folder, dir, file, [new JeekMusic.Library.Track { FolderId = folder, TrackKey = file, Dir = dir, FileName = file, Title = file, Artists = ["Test"], ArtistDisplay = "Test" }]);
                }
                tx.Commit();
            }
            Window = new MainWindow(App) { Width = width, Height = 650, ExitRequested = true, Background = Brushes.White };
            Window.RestoreLayout(new UiState { NavWidth = 230 });
            Vm.InitialTarget = new NavTarget.Folder(folder, ""); App.Library.RebuildSnapshot(); Window.Show(); Pump();
            // Keep fixtures collapsed without user input; actual hover must never change this.
            Long.IsExpanded = false; Pump();
        }
        public T Control<T>(string name) where T : Control => Window.FindControl<T>(name)!;
        public TreeViewItem Row(NavNode node) => Tree.GetVisualDescendants().OfType<TreeViewItem>().Single(c => ReferenceEquals(c.DataContext, node));
        public TextBlock Name(NavNode node) => Row(node).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("nav-name") && ReferenceEquals(t.DataContext, node));
        public void Pump() { for (var i = 0; i < 6; i++) { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
        public async Task WaitDelay() { await Task.Delay(420); Pump(); }
        public UiState Save() { var state = new UiState(); Window.SaveLayout(state); return state; }
        public void Hover(NavNode node)
        {
            var scroll = Tree.GetVisualDescendants().OfType<ScrollViewer>().First();
            var viewport = scroll.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().First();
            for (var i = 0; i < 3; i++)
            {
                var pos = Name(node).TranslatePoint(default, viewport)!.Value;
                if (pos.Y < 0 || pos.Y + Name(node).Bounds.Height > viewport.Bounds.Height)
                    scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y + pos.Y - viewport.Bounds.Height / 2));
                else if (scroll.Offset.X != 0) scroll.Offset = new Vector(0, scroll.Offset.Y);
                Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
            }
            var text = Name(node); var p = text.TranslatePoint(new Point(2, text.Bounds.Height / 2), Window)!.Value;
            var pane = Control<DockPanel>("NavPanel"); var right = pane.TranslatePoint(new Point(pane.Bounds.Width - 2, 0), Window)!.Value.X;
            var point = new Point(Math.Min(p.X, right), p.Y);
            var hit = Window.InputHitTest(point) as Visual;
            Assert.True(ReferenceEquals(Row(node), hit?.FindAncestorOfType<TreeViewItem>(includeSelf: true)),
                $"Wanted {node.Title}; point={point}; name={text.Bounds}; tree={Tree.Bounds}; got={(hit?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as NavNode)?.Title}; {D}");
            Window.MouseMove(point); Pump();
        }
        public void Click(Control control, MouseButton button = MouseButton.Left)
        {
            var p = control.TranslatePoint(new Point(Math.Min(3, control.Bounds.Width / 2), control.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(p, button); Window.MouseUp(p, button); Pump();
        }
        public void Drag(double delta)
        {
            var splitter = Control<GridSplitter>("NavSplitter"); var start = splitter.TranslatePoint(new Point(3, 100), Window)!.Value;
            Window.MouseDown(start, MouseButton.Left); Window.MouseMove(start + new Vector(delta, 0)); Pump();
            Window.MouseUp(start + new Vector(delta, 0), MouseButton.Left); Pump();
        }
        public void Capture(string name)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = Window.CaptureRenderedFrame(); Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("JEEKMUSIC_TEST_ARTIFACTS") is { Length: > 0 } output)
            { Directory.CreateDirectory(output); frame.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default); }
        }
        public void Close() { if (_closed) return; _closed = true; Vm.Player.ProgressVisible = false; Window.Close(); Pump(); }
        public void Dispose() { Close(); App.Dispose(); Assert.Empty(_engines); Assert.Empty(_endpoint.Writes); File.Delete(_path); }
    }
}
