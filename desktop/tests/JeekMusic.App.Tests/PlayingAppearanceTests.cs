using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
            Assert.Equal(new Size(18, 18), marker.Bounds.Size);
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
                Assert.Equal(playing ? Icons.Play : Icons.Pause, ((TextBlock)marker.Child!).Text);
                Assert.Equal(CurrentTrackMarker.Status(playing), AutomationProperties.GetName(marker));
                Assert.Equal(CurrentTrackMarker.Status(playing), AutomationProperties.GetHelpText(current));
                Assert.False(Marker(ordinary).IsVisible); Assert.False(Marker(selected).IsVisible);
                Assert.Contains("current", current.Classes); Assert.Equal(playing, current.Classes.Contains("playing"));
                Assert.Equal(selectedCurrent, current.IsSelected); Assert.True(selected.IsSelected);
                Assert.Equal(selectedCurrent ? 3 : 2, h.Table.SelectedItems.Count);
                // Current identity must not replace selection/hover colors or whiten all text.
                Assert.Equal(ordinary.Background, current.Background);
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
            SaveFrame(h.Window, $"{(dark ? "dark" : "light")}-offscreen-centered");
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
            Assert.Equal(Icons.Play, ((TextBlock)marker.Child!).Text);
            Assert.NotNull(marker.Background);
            h.Player.Pause(); h.Pump();
            Assert.Same(marker, Assert.Single(content.GetVisualDescendants().OfType<CurrentTrackMarker>(), m => m.IsVisible));
            Assert.False(marker.IsPlaying); Assert.Equal(Icons.Pause, ((TextBlock)marker.Child!).Text);
            Assert.Equal(CurrentTrackMarker.Status(false), AutomationProperties.GetName(marker));
            var current = Row(h, 5); Assert.True(h.Rows[5].IsCurrent); Assert.False(h.Rows[5].IsPlaying);
            Assert.Equal(Icons.Pause, ((TextBlock)Marker(current).Child!).Text);
            flyout.Hide(); h.Pump();
            h.Player.Queue.Clear(); h.Pump();
            Assert.All(h.Rows, row => { Assert.False(row.IsCurrent); Assert.False(row.IsPlaying); });
        }, CancellationToken.None);
    }

    static DataGridRow Row(PlayingVisibilityTests.Harness h, int index) => h.Realized.Single(r => ReferenceEquals(r.DataContext, h.Rows[index]));
    static CurrentTrackMarker Marker(DataGridRow row) => row.GetVisualDescendants().OfType<CurrentTrackMarker>().Single();

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
