using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using JeekMusic.Library;

namespace JeekMusic.UI;

/// <summary>
/// 播放队列（需求 P-6、P-15）：当前歌曲后面分"手动添加的"和"来自「列表名」"两段；点一首播放，× 移除，拖动右边的手柄调整顺序。
/// 打开与开始播放时将当前行一次性滚入视口，已完整可见则保持位置。
/// </summary>
public static class QueueFlyout
{
    public static Flyout Show(Control anchor, AppServices app, Control target)
    {
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, ShowMode = FlyoutShowMode.Standard };
        var list = new StackPanel { Spacing = 0 };
        var scroll = new ScrollViewer { Content = list, Width = 420, MaxHeight = 560 };
        var header = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 4, 8, 8) };
        var clear = new Button { Content = L.T("清空队列"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(10, 2) };
        clear.Click += (_, _) => { app.Player.Queue.Clear(); app.Player.Pause(); };
        flyout.Content = new StackPanel { Children = { new Grid { Children = { header, clear } }, scroll } };

        Control? currentRow = null;
        CurrentTrackMarker? currentMarker = null;
        var generation = 0;
        PlayingRowVisibility? visibility = null;
        int dragFrom = -1;
        void Fill()
        {
            generation++;
            visibility?.Cancel("queue-changed");
            currentRow = null;
            currentMarker = null;
            list.Children.Clear();
            var q = app.Player.Queue;
            var snapshot = app.Library.Snapshot;
            header.Text = L.F($"播放队列（{q.Items.Count} 首）");
            var manualEnd = q.CurrentIndex + 1;
            while (manualEnd < q.Items.Count && q.Items[manualEnd].Manual) manualEnd++;
            for (var i = 0; i < q.Items.Count; i++)
            {
                if (i == q.CurrentIndex + 1 && i < manualEnd) list.Children.Add(Section(L.T("手动添加的")));
                if (i == manualEnd && i > q.CurrentIndex && q.Context is { } ctx) list.Children.Add(Section(L.F($"来自「{ctx.Title}」")));
                var track = snapshot.ById.GetValueOrDefault(q.Items[i].TrackId);
                var row = Row(i, track, i == q.CurrentIndex, i < q.CurrentIndex);
                if (i == q.CurrentIndex) currentRow = row;
                list.Children.Add(row);
            }
        }

        Control Section(string text) => new TextBlock { Text = text, Opacity = 0.6, FontSize = 12, Margin = new Thickness(8, 10, 8, 4) };

        Control Row(int index, Track? track, bool current, bool played)
        {
            var marker = new CurrentTrackMarker { IsVisible = current, IsPlaying = app.Player.IsPlaying,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (current) currentMarker = marker;
            var title = new TextBlock { Text = track?.Title ?? L.T("（不可用）"), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal };
            var artist = new TextBlock { Text = track is null ? "" : Text.Artist(track), Opacity = 0.6, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            var remove = new Button { Content = Icons.Close, Classes = { "icon" }, FontSize = 10, Padding = new Thickness(6) };
            remove.Click += (_, _) => app.Player.Queue.RemoveAt(index);
            var handle = new TextBlock { Text = "", Classes = { "glyph" }, Opacity = 0.5, Margin = new Thickness(6, 0), Cursor = new Cursor(StandardCursorType.SizeNorthSouth) };
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("22,*,Auto,Auto"), Background = Brushes.Transparent,
                Opacity = played ? 0.55 : 1, Tag = index,
            };
            var text = new StackPanel { Margin = new Thickness(8, 4), Children = { title, artist } };
            grid.Children.Add(marker);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
            Grid.SetColumn(handle, 3);
            grid.Children.Add(handle);
            text.PointerReleased += (_, e) => { if (dragFrom < 0 && e.InitialPressMouseButton == MouseButton.Left) app.Player.JumpTo(index); };
            // 拖动手柄调整顺序
            handle.PointerPressed += (_, e) => { dragFrom = index; e.Pointer.Capture(handle); e.Handled = true; };
            handle.PointerReleased += (_, e) =>
            {
                if (dragFrom < 0) return;
                var y = e.GetPosition(list).Y;
                var to = list.Children.OfType<Grid>().Where(g => g.Tag is int).LastOrDefault(g => g.Bounds.Top <= y)?.Tag as int? ?? 0;
                var from = dragFrom;
                dragFrom = -1;
                e.Pointer.Capture(null);
                if (to != from) app.Player.Queue.Move(from, to);
            };
            return grid;
        }

        visibility = new PlayingRowVisibility(scroll,
            () => scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault(),
            id => app.Player.Queue.Current?.TrackId == id && currentRow is not null,
            id => app.Player.Queue.Current?.TrackId == id ? currentRow : null,
            _ =>
            {
                var viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
                if (currentRow is null || viewport is null || currentRow.TranslatePoint(default, viewport) is not { } p) return;
                var dy = p.Y < 0 ? p.Y : Math.Max(0, p.Y + currentRow.Bounds.Height - viewport.Bounds.Height);
                scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + dy));
            }, () => true, () => generation, () => app.Player.CurrentTrack?.Id);
        app.QueueVisibility = visibility;
        void OnStarted(Track track) => visibility.Request(track.Id);
        void OnChanged() => Fill();
        void OnPlaybackState() { if (currentMarker is not null) currentMarker.IsPlaying = app.Player.IsPlaying; }
        app.Player.Queue.Changed += OnChanged;
        app.Player.PlaybackStarted += OnStarted;
        app.Player.StateChanged += OnPlaybackState;
        flyout.Closed += (_, _) =>
        {
            app.Player.Queue.Changed -= OnChanged;
            app.Player.PlaybackStarted -= OnStarted;
            app.Player.StateChanged -= OnPlaybackState;
            visibility.Dispose();
            currentRow = null;
            currentMarker = null;
            if (ReferenceEquals(app.QueueVisibility, visibility)) app.QueueVisibility = null;
        };
        Fill();
        flyout.ShowAt(target);
        if (app.Player.CurrentTrack is { } current) visibility.Request(current.Id);
        return flyout;
    }
}
