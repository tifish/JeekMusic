using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class SongOverflowTests
{
    const string LongTitle = "远方的灯火 — 夜航与归途（管弦乐现场录音 / The Long Journey Home, Live）";

    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.25)]
    [InlineData(false, 1.5)]
    [InlineData(true, 2.0)]
    public Task ActualRowFillHeightBaselineAndOutlineSurviveReveal(bool dark, double scale) => Run(() =>
    {
        using var h = new PlayingVisibilityTests.Harness(LongTitle);
        h.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        h.Window.Background = dark ? new SolidColorBrush(Color.Parse("#202020")) : Brushes.White;
        h.Window.SetRenderScaling(scale);
        h.Table.Columns[0].Width = new DataGridLength(230); h.Pump();
        var row = h.Realized.Single(r => ReferenceEquals(r.DataContext, h.Rows[5]));
        var title = Title(row);
        Assert.Null(ToolTip.GetTip(title));
        var point = title.TranslatePoint(new Point(3, title.Bounds.Height / 2), h.Window)!.Value;
        for (var current = 0; current < 2; current++)
        {
            if (current == 1) { h.Window.PlayFrom(h.Track(5)); h.Ready(); h.Player.Pause(); h.Pump(); }
            foreach (var selected in new[] { false, true })
            {
                h.Window.MouseMove(new Point(700, 80)); h.Pump();
                if (selected) h.Table.SelectedItems.Add(h.Rows[5]); else h.Table.SelectedItems.Remove(h.Rows[5]);
                // Keep ordinary multiselection and cell keyboard focus intact.
                h.Table.SelectedItems.Add(h.Rows[2]);
                row.GetVisualDescendants().OfType<DataGridCell>().First().Focus(NavigationMethod.Tab);
                h.Pump();
                var selection = h.Table.SelectedItems.Cast<object>().ToArray();
                var focus = h.Window.FocusManager!.GetFocusedElement();
                var height = row.Bounds.Height;
                h.Window.MouseMove(point); h.Pump();
                var d = D(h);
                Assert.True(d["visible"]!.GetValue<bool>(), d.ToJsonString());
                var surface = Surface(h); var clone = surface.GetVisualDescendants().OfType<TextBlock>().Single();
                Assert.False(surface.IsHitTestVisible); Assert.False(surface.Focusable);
                Assert.Equal(new Thickness(0), surface.BorderThickness); Assert.Null(surface.BorderBrush);
                Assert.Equal(new Thickness(0), surface.Padding); Assert.Equal(0, surface.BoxShadow.Count);
                Assert.Equal(height, surface.Bounds.Height);
                Assert.Equal(row.TranslatePoint(default, h.Window)!.Value.Y, surface.TranslatePoint(default, h.Window)!.Value.Y, 4);
                Assert.Equal(title.TranslatePoint(default, h.Window)!.Value.Y, clone.TranslatePoint(default, h.Window)!.Value.Y, 4);
                Assert.Equal(title.FontSize, clone.FontSize); Assert.Equal(title.FontWeight, clone.FontWeight);
                Assert.Equal(LongTitle, clone.Text); Assert.Equal(TextTrimming.None, clone.TextTrimming);
                Assert.Equal(selection, h.Table.SelectedItems.Cast<object>()); Assert.Same(focus, h.Window.FocusManager.GetFocusedElement());
                // Compare actual rendered blank row pixels against the continuation,
                // including DataGrid's selected/hover/focus alpha compositing.
                var y = row.TranslatePoint(new Point(0, height - 5), h.Window)!.Value.Y;
                var sourceX = title.TranslatePoint(new Point(-4, 0), h.Window)!.Value.X;
                var floatedX = surface.TranslatePoint(new Point(surface.Bounds.Width - 3, 0), h.Window)!.Value.X;
                using var before = Frame(h);
                AssertPixelNear(Pixel(before, sourceX, y, scale), Pixel(before, floatedX, y, scale));
                // Every part of the existing amber outline stays visible through the
                // surface, while its extension has no border of its own.
                var outlinePoint = row.TranslatePoint(new Point(150, .5), h.Window)!.Value;
                var outlined = Pixel(before, outlinePoint.X, outlinePoint.Y, scale);
                var fill = row.GetVisualDescendants().OfType<Rectangle>().Single(r => r.Name == "BackgroundRectangle");
                Assert.Equal(current == 1, fill.Stroke is not null);
                Save(before, $"song-{(dark ? "dark" : "light")}-{scale}-{(current == 1 ? "current" : "ordinary")}-{(selected ? "selected" : "hover")}");
                var existing = surface;
                h.Window.MouseMove(point + new Vector(5, 0)); h.Pump(); Assert.Same(existing, Surface(h));
                h.Window.MouseMove(new Point(700, 80)); h.Pump(); Assert.False(D(h)["visible"]!.GetValue<bool>());
                using var after = Frame(h);
                if (current == 1) AssertPixelNear(outlined, Pixel(after, outlinePoint.X, outlinePoint.Y, scale));
            }
        }
        // A title which fits after a column resize no longer opens a redundant hint.
        h.Table.Columns[0].Width = new DataGridLength(900); h.Pump();
        h.Window.MouseMove(point); h.Pump(); Assert.False(D(h)["visible"]!.GetValue<bool>());
        h.Window.MouseMove(new Point(700, 80)); h.Pump();
        h.Table.Columns[0].Width = new DataGridLength(230); h.Pump();
        var shortTitle = Title(h.Realized.Single(r => ReferenceEquals(r.DataContext, h.Rows[3])));
        h.Window.MouseMove(shortTitle.TranslatePoint(new Point(3, 3), h.Window)!.Value); h.Pump();
        Assert.False(D(h)["visible"]!.GetValue<bool>());
    });

    [Fact]
    public Task ResizeVirtualizationClickMultiselectAndKeyboardCannotLeaveAStaleSurface() => Run(() =>
    {
        using var h = new PlayingVisibilityTests.Harness(LongTitle);
        h.Table.Columns[0].Width = new DataGridLength(230); h.Pump();
        var title = Title(h.Realized.Single(r => ReferenceEquals(r.DataContext, h.Rows[5])));
        var point = title.TranslatePoint(new Point(3, 3), h.Window)!.Value;
        void Hover() { title = Title(h.Realized.Single(r => ReferenceEquals(r.DataContext, h.Rows[5]))); point = title.TranslatePoint(new Point(3, 3), h.Window)!.Value; h.Window.MouseMove(point); h.Pump(); Assert.True(D(h)["visible"]!.GetValue<bool>(), D(h).ToJsonString()); }
        Hover(); h.Window.MouseDown(point, MouseButton.Left); h.Window.MouseUp(point, MouseButton.Left); h.Pump();
        Assert.Same(h.Rows[5], h.Table.SelectedItem); Assert.False(D(h)["visible"]!.GetValue<bool>());
        Hover(); h.Table.Columns[0].Width = new DataGridLength(250); h.Pump(); Assert.False(D(h)["visible"]!.GetValue<bool>());
        Hover(); h.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null); h.Window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.None, null); h.Pump(); Assert.False(D(h)["visible"]!.GetValue<bool>());
        Hover(); h.ScrollTo(220); Assert.False(D(h)["visible"]!.GetValue<bool>());
        h.ScrollTo(5); Hover(); h.Window.MouseWheel(point, new Vector(0, -1)); h.Pump(); Assert.False(D(h)["visible"]!.GetValue<bool>());
    });

    [Fact]
    public Task RightmostTitleMovesInsideWindowAndExtremeTextKeepsRowHeight() => Run(() =>
    {
        using var h = new PlayingVisibilityTests.Harness(LongTitle);
        h.Table.Columns[0].Width = new DataGridLength(180);
        h.Table.Columns[0].DisplayIndex = h.Table.Columns.Count - 1; h.Pump();
        h.Table.ScrollIntoView(h.Rows[5], h.Table.Columns[0]); h.Pump();
        var row = h.Realized.Single(r => ReferenceEquals(r.DataContext, h.Rows[5]));
        var title = Title(row);
        var point = title.TranslatePoint(new Point(3, title.Bounds.Height / 2), h.Window)!.Value;
        h.Window.MouseMove(point); h.Pump(); Assert.True(D(h)["visible"]!.GetValue<bool>(), D(h).ToJsonString());
        var surface = Surface(h);
        var rect = new Rect(surface.Bounds.Size).TransformToAABB(surface.TransformToVisual(h.Window)!.Value);
        Assert.True(rect.Left < title.TranslatePoint(default, h.Window)!.Value.X);
        Assert.InRange(rect.Left, 0, h.Window.ClientSize.Width); Assert.True(rect.Right <= h.Window.ClientSize.Width);
        Assert.Equal(row.Bounds.Height, rect.Height, 4);
        title.Text = string.Concat(Enumerable.Repeat("漫长的旅程 Long journey ", 100)); h.Pump();
        Assert.False(D(h)["visible"]!.GetValue<bool>());
        h.Window.MouseMove(point); h.Pump(); Assert.True(D(h)["automaticReading"]!.GetValue<bool>());
        Assert.False(D(h)["wrapped"]!.GetValue<bool>()); Assert.Equal(row.Bounds.Height, Surface(h).Bounds.Height);
        Assert.False(Surface(h).IsHitTestVisible);
        Assert.Equal(28, h.Tools.Catalog.Count);
        Assert.Contains("songOverflow", h.Tools.Catalog.Single(t => t!["name"]!.GetValue<string>() == "get_diagnostics")!["description"]!.GetValue<string>());
        using var frame = Frame(h); Save(frame, "song-right-edge-horizontal-reading");
    });

    static TextBlock Title(DataGridRow row) => row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("song-title"));
    static JsonNode D(PlayingVisibilityTests.Harness h) => h.Tools.Call("get_diagnostics", new())["mainLayout"]!["songOverflow"]!;
    static Border Surface(PlayingVisibilityTests.Harness h) => OverlayLayer.GetOverlayLayer(h.Table)!.Children.OfType<Border>().Single(b => b.Name == "SongOverflowReveal");
    static Bitmap Frame(PlayingVisibilityTests.Harness h) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); return h.Window.CaptureRenderedFrame()!; }
    static int Pixel(Bitmap frame, double x, double y, double scale)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try { frame.CopyPixels(new PixelRect((int)(x * scale), (int)(y * scale), 1, 1), buffer, 4, 4); return Marshal.ReadInt32(buffer); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    static void AssertPixelNear(int expected, int actual)
    {
        for (var shift = 0; shift < 32; shift += 8) Assert.InRange(Math.Abs(((expected >> shift) & 255) - ((actual >> shift) & 255)), 0, 2);
    }
    static void Save(Bitmap frame, string name)
    {
        if (Environment.GetEnvironmentVariable("JEEKMUSIC_TEST_ARTIFACTS") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output); frame.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }
    static async Task Run(Action test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(test, CancellationToken.None);
    }
}
