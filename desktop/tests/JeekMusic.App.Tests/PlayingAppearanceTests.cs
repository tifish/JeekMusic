using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JeekMusic.UI;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class PlayingAppearanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentPlayingPausedAndSelectedStatesRemainDistinctWithStableLayout(bool dark)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(() =>
        {
            using var h = new PlayingVisibilityTests.Harness();
            h.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            // Headless has no native Mica surface; give transparent window areas a theme backdrop.
            h.Window.Background = dark ? new SolidColorBrush(Color.Parse("#202020")) : Brushes.White;
            h.Pump();
            h.Table.SelectedItems.Add(h.Rows[2]); h.Table.SelectedItems.Add(h.Rows[4]);
            h.Window.PlayFrom(h.Track(5)); h.Ready();
            var current = Row(h, 5); var selected = Row(h, 4); var ordinary = Row(h, 3);
            var marker = Marker(current);
            var title = current.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("song-title"));
            var columnState = h.Table.Columns.Select(c => (c.Tag, c.IsVisible, c.ActualWidth, c.DisplayIndex)).ToArray();
            var rowHeight = current.Bounds.Height; var titleBounds = title.Bounds;
            Assert.Equal(ordinary.Bounds.Height, rowHeight);
            Assert.InRange(rowHeight, 32, 42);
            Assert.Equal(16, marker.Bounds.Height);
            Assert.True(marker.Bounds.Width > 40);
            Assert.Equal(FontWeight.SemiBold, title.FontWeight);
            Assert.NotEqual(FontWeight.SemiBold, ordinary.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("song-title")).FontWeight);

            foreach (var playing in new[] { true, false })
            foreach (var selectedCurrent in new[] { false, true })
            {
                if (selectedCurrent) h.Table.SelectedItems.Add(h.Rows[5]); else h.Table.SelectedItems.Remove(h.Rows[5]);
                if (playing) h.Player.Resume(); else h.Player.Pause();
                h.Pump();
                // Real keyboard focus and hover can coexist with current and extended selection.
                var cell = current.GetVisualDescendants().OfType<DataGridCell>().First();
                Assert.True(cell.Focus(NavigationMethod.Tab));
                // Exercise the actual hover style independently of other tests' shared headless mouse capture.
                ((IPseudoClasses)selected.Classes).Set(":pointerover", true); h.Pump();
                Assert.Contains(":pointerover", selected.Classes);
                Assert.True(h.Rows[5].IsCurrent); Assert.Equal(playing, h.Rows[5].IsPlaying);
                Assert.Equal(playing, marker.IsPlaying); Assert.True(marker.IsVisible);
                AssertMarker(marker, playing);
                var playerMarker = h.Window.FindControl<CurrentTrackMarker>("NowStatus")!;
                Assert.True(playerMarker.IsVisible);
                AssertMarker(playerMarker, playing);
                Assert.Equal(h.Track(5).Title, h.Window.FindControl<TextBlock>("NowTitle")!.Text);
                Assert.Equal(CurrentTrackMarker.Status(playing), AutomationProperties.GetName(marker));
                Assert.Equal(CurrentTrackMarker.Status(playing), AutomationProperties.GetHelpText(current));
                Assert.False(Marker(ordinary).IsVisible); Assert.False(Marker(selected).IsVisible);
                Assert.Contains("current", current.Classes); Assert.Equal(playing, current.Classes.Contains("playing"));
                Assert.Equal(selectedCurrent, current.IsSelected); Assert.True(selected.IsSelected);
                Assert.Equal(selectedCurrent ? 3 : 2, h.Table.SelectedItems.Count);
                // The full-row theme layer spans every column. Selection still owns its fill;
                // current identity adds an outline, independent of the focused title cell.
                var background = Background(current);
                Assert.InRange(Math.Abs(background.Bounds.Width - current.Bounds.Width), 0, .5);
                Assert.NotNull(background.Stroke);
                Assert.Null(Background(ordinary).Stroke);
                Assert.Null(Background(selected).Stroke);
                if (selectedCurrent)
                {
                    Assert.Equal(2, background.StrokeThickness);
                    Assert.Equal(BrushColor(Background(selected).Fill), BrushColor(background.Fill));
                }
                else
                {
                    Assert.Equal(1, background.StrokeThickness);
                    Assert.NotEqual(BrushColor(Background(ordinary).Fill), BrushColor(background.Fill));
                    Assert.InRange(background.Fill!.Opacity, .1, .3);
                    var normalFill = background.Fill;
                    ((IPseudoClasses)current.Classes).Set(":pointerover", true); h.Pump();
                    Assert.True(background.Fill!.Opacity > normalFill!.Opacity);
                    ((IPseudoClasses)current.Classes).Set(":pointerover", false); h.Pump();
                    Assert.Equal(normalFill, background.Fill);
                    AssertFullRowPixels(h.Window, current, ordinary);
                }
                Assert.Equal(ordinary.Foreground, current.Foreground);
                Assert.Equal(titleBounds, title.Bounds); Assert.Equal(rowHeight, current.Bounds.Height);
                Assert.Equal(columnState, h.Table.Columns.Select(c => (c.Tag, c.IsVisible, c.ActualWidth, c.DisplayIndex)));
                var accent = Assert.IsAssignableFrom<ISolidColorBrush>(marker.Background).Color;
                Assert.True(Contrast(accent, Colors.White) >= 3, $"Marker contrast: {accent}");
                SaveFrame(h.Window, $"{(dark ? "dark" : "light")}-{(playing ? "playing" : "paused")}-{(selectedCurrent ? "selected" : "unselected")}");
            }
            ((IPseudoClasses)selected.Classes).Set(":pointerover", false);
            var selection = h.Table.SelectedItems.Cast<SongRow>().ToArray();
            h.Window.PlayFrom(h.Track(220)); h.Ready(); h.AssertCentered(220);
            Assert.Equal(selection, h.Table.SelectedItems.Cast<SongRow>());
            Assert.False(h.Rows[5].IsCurrent); Assert.True(h.Rows[220].IsCurrent);
            Assert.True(Marker(Row(h, 220)).IsPlaying);
            AssertMarker(h.Window.FindControl<CurrentTrackMarker>("NowStatus")!, true);
            Assert.Equal(h.Track(220).Title, h.Window.FindControl<TextBlock>("NowTitle")!.Text);
            Assert.NotNull(Background(Row(h, 220)).Stroke);
            SaveFrame(h.Window, $"{(dark ? "dark" : "light")}-offscreen-centered");
            h.Table.Columns[0].IsVisible = false; h.Pump();
            Assert.NotNull(Background(Row(h, 220)).Stroke);
            AssertFullRowPixels(h.Window, Row(h, 220), Row(h, 219));
            SaveFrame(h.Window, $"{(dark ? "dark" : "light")}-title-hidden");
            // Revisit recycled containers: the previous track retains only its normal selection.
            h.ScrollTo(5);
            Assert.Null(Background(Row(h, 5)).Stroke);
            Assert.DoesNotContain("current", Row(h, 5).Classes);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task QueueSharesTheCurrentTrackMarkerAndPauseUpdatesItWithoutRebuildingRows()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(() =>
        {
            using var h = new PlayingVisibilityTests.Harness();
            h.Window.PlayFrom(h.Track(5)); h.Ready();
            var flyout = QueueFlyout.Show(h.Window, h.App, h.Window.FindControl<Button>("QueueButton")!); h.Pump();
            var content = (Control)flyout.Content!;
            var marker = Assert.Single(content.GetVisualDescendants().OfType<CurrentTrackMarker>(), m => m.IsVisible);
            Assert.True(marker.IsPlaying);
            AssertMarker(marker, true);
            Assert.NotNull(marker.Background);
            var queueRow = marker.GetVisualAncestors().OfType<Grid>().Single(g => g.Tag is int);
            Assert.NotEqual(Brushes.Transparent, queueRow.Background);
            SaveQueue(content, "queue-playing");
            h.Player.Pause(); h.Pump();
            Assert.Same(marker, Assert.Single(content.GetVisualDescendants().OfType<CurrentTrackMarker>(), m => m.IsVisible));
            AssertMarker(marker, false);
            Assert.Equal(CurrentTrackMarker.Status(false), AutomationProperties.GetName(marker));
            SaveQueue(content, "queue-paused");
            var current = Row(h, 5); Assert.True(h.Rows[5].IsCurrent); Assert.False(h.Rows[5].IsPlaying);
            AssertMarker(Marker(current), false);
            h.Player.Next(); h.Ready();
            var nextMarker = Assert.Single(content.GetVisualDescendants().OfType<CurrentTrackMarker>(), m => m.IsVisible);
            Assert.NotSame(marker, nextMarker);
            AssertMarker(nextMarker, true);
            Assert.Equal(h.Track(6).Title, h.Window.FindControl<TextBlock>("NowTitle")!.Text);
            Assert.Null(Background(Row(h, 5)).Stroke);
            Assert.NotNull(Background(Row(h, 6)).Stroke);
            Assert.All(content.GetVisualDescendants().OfType<Grid>().Where(g => g.Tag is int index && index != 6),
                row => Assert.Equal(Brushes.Transparent, row.Background));
            flyout.Hide(); h.Pump();
            h.Player.Queue.Clear(); h.Pump();
            Assert.All(h.Rows, row => { Assert.False(row.IsCurrent); Assert.False(row.IsPlaying); });
            Assert.False(h.Window.FindControl<CurrentTrackMarker>("NowStatus")!.IsVisible);
            Assert.All(h.Realized, row => Assert.Null(Background(row).Stroke));
        }, CancellationToken.None);
    }

    static DataGridRow Row(PlayingVisibilityTests.Harness h, int index) => h.Realized.Single(r => ReferenceEquals(r.DataContext, h.Rows[index]));
    static CurrentTrackMarker Marker(DataGridRow row) => row.GetVisualDescendants().OfType<CurrentTrackMarker>().Single();
    static Rectangle Background(DataGridRow row) => row.GetVisualDescendants().OfType<Rectangle>()
        .Single(r => r.Name == "BackgroundRectangle" && ReferenceEquals(r.TemplatedParent, row));
    static Color BrushColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    static void AssertFullRowPixels(Window window, DataGridRow current, DataGridRow ordinary)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        // Sample blank space below the text in several non-title columns, away from
        // the outline. This catches a title-only mark or a fill hidden by the template.
        var cells = current.GetVisualDescendants().OfType<DataGridCell>().Where(c => c.IsVisible).Skip(1).Take(3).ToArray();
        Assert.Equal(3, cells.Length);
        var colors = cells.Select(cell => Pixel(cell.TranslatePoint(new Point(4, cell.Bounds.Height - 4), window)!.Value)).ToArray();
        Assert.All(colors, color => Assert.Equal(colors[0], color));
        var ordinaryPoint = ordinary.TranslatePoint(new Point(4, ordinary.Bounds.Height - 4), window)!.Value;
        Assert.NotEqual(Pixel(new Point(cells[0].TranslatePoint(new Point(4, 0), window)!.Value.X, ordinaryPoint.Y)), colors[0]);

        int Pixel(Point point)
        {
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
            try
            {
                frame.CopyPixels(new PixelRect((int)(point.X * window.RenderScaling), (int)(point.Y * window.RenderScaling), 1, 1), buffer, 4, 4);
                return System.Runtime.InteropServices.Marshal.ReadInt32(buffer);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
        }
    }

    static void AssertMarker(CurrentTrackMarker marker, bool playing)
    {
        Assert.Equal(playing, marker.IsPlaying);
        var text = marker.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        Assert.Contains(playing ? Icons.Play : Icons.Pause, text);
        Assert.Contains(playing ? L.T("正在播放") : L.T("已暂停"), text);
    }

    static void SaveFrame(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame); Assert.True(frame.PixelSize.Width >= 1000);
        if (Environment.GetEnvironmentVariable("JEEKMUSIC_VISUAL_OUTPUT") is { Length: > 0 } path)
        {
            Directory.CreateDirectory(path);
            frame.Save(Path.Combine(path, name + ".png"), PngBitmapEncoderOptions.Default);
        }
    }

    static void SaveQueue(Control content, string name)
    {
        if (Environment.GetEnvironmentVariable("JEEKMUSIC_VISUAL_OUTPUT") is not { Length: > 0 } path) return;
        var root = TopLevel.GetTopLevel(content) ?? content;
        using var bitmap = new RenderTargetBitmap(PixelSize.FromSize(root.Bounds.Size, 1));
        bitmap.Render(root);
        Directory.CreateDirectory(path);
        bitmap.Save(Path.Combine(path, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Linear(byte channel) { var n = channel / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var x = L(a); var y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
