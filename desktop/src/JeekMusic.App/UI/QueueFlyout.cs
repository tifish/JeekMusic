using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using JeekMusic.Library;

namespace JeekMusic.UI;

/// <summary>
/// 播放队列（需求 P-6、P-15）：当前歌曲后面分"手动添加的"和"来自「列表名」"两段；点一首播放，× 移除，拖动右边的手柄调整顺序。
/// 打开时当前歌曲在正中间（同移动端 P-10）。
/// </summary>
public static class QueueFlyout
{
    public static void Show(Control anchor, AppServices app, Control target)
    {
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, ShowMode = FlyoutShowMode.Standard };
        var list = new StackPanel { Spacing = 0 };
        var scroll = new ScrollViewer { Content = list, Width = 420, MaxHeight = 560 };
        var header = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 4, 8, 8) };
        var clear = new Button { Content = L.T("清空队列"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(10, 2) };
        clear.Click += (_, _) => { app.Player.Queue.Clear(); app.Player.Pause(); };
        flyout.Content = new StackPanel { Children = { new Grid { Children = { header, clear } }, scroll } };

        Control? currentRow = null;
        int dragFrom = -1;
        void Fill()
        {
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
            var title = new TextBlock { Text = track?.Title ?? L.T("（不可用）"), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal };
            var artist = new TextBlock { Text = track is null ? "" : Text.Artist(track), Opacity = 0.6, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            var remove = new Button { Content = Icons.Close, Classes = { "icon" }, FontSize = 10, Padding = new Thickness(6) };
            remove.Click += (_, _) => app.Player.Queue.RemoveAt(index);
            var handle = new TextBlock { Text = "", Classes = { "glyph" }, Opacity = 0.5, Margin = new Thickness(6, 0), Cursor = new Cursor(StandardCursorType.SizeNorthSouth) };
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Background = current ? new SolidColorBrush(Color.FromArgb(60, 0, 120, 215)) : Brushes.Transparent,
                Opacity = played ? 0.55 : 1, Tag = index,
            };
            var text = new StackPanel { Margin = new Thickness(8, 4), Children = { title, artist } };
            grid.Children.Add(text);
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);
            Grid.SetColumn(handle, 2);
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

        void OnChanged() => Fill();
        app.Player.Queue.Changed += OnChanged;
        flyout.Closed += (_, _) => app.Player.Queue.Changed -= OnChanged;
        Fill();
        flyout.ShowAt(target);
        // 当前歌曲在正中间
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (currentRow is null) return;
            var y = currentRow.Bounds.Top - scroll.Viewport.Height / 2 + currentRow.Bounds.Height / 2;
            scroll.Offset = new Vector(0, Math.Max(0, y));
        }, Avalonia.Threading.DispatcherPriority.Background);
    }
}
