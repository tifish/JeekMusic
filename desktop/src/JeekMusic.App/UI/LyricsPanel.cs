using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using JeekMusic.Core.Lyrics;
using JeekMusic.Library;
using JeekMusic.Lyrics;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

/// <summary>
/// 歌词面板（需求 W-6；规则同移动端 NP-2、LY-3 ~ LY-5、LY-12）：从主窗口右侧拉出，显示封面和歌词，可以切换成整页。
/// 当前行显示在正中间并高亮；用户手动滚动后暂停跟随 3 秒；滚动时中间出现定位线，点线上的播放按钮跳到那一行。
/// 面板不可见时不跟随进度（设计 §13）。
/// </summary>
public sealed class LyricsPanel : UserControl
{
    readonly AppServices _app;
    readonly PlayerViewModel _player;
    readonly LyricsService _lyrics;
    readonly Image _cover = new() { Stretch = Stretch.UniformToFill };
    readonly Border _coverBox;
    readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    readonly TextBlock _subtitle = new() { Opacity = 0.65, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand) };
    readonly StackPanel _lines = new() { Spacing = 10, Margin = new Thickness(16, 0) };
    readonly ScrollViewer _scroll;
    readonly Border _locator;
    readonly TextBlock _locatorTime = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _empty;
    readonly Button _searchButton = new() { Content = "搜索歌词", HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _offsetText = new() { FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
    Track? _track;
    FoundLyrics? _found;
    int _current = -1;
    DateTime _manualUntil = DateTime.MinValue;
    bool _programmaticScroll;
    readonly DispatcherTimer _hideLocator;

    /// <summary>"搜索歌词"按钮（在线歌词做好后接上）。</summary>
    public event Action<Track>? SearchRequested;
    /// <summary>点了专辑 / 艺术家。</summary>
    public event Action<Track>? AlbumRequested;
    public event Action? FullPageToggled;

    public LyricsPanel(AppServices app, PlayerViewModel player, LyricsService lyrics)
    {
        _app = app;
        _player = player;
        _lyrics = lyrics;
        _coverBox = new Border
        {
            Width = 220, Height = 220, CornerRadius = new CornerRadius(8), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)), Child = _cover,
        };
        _subtitle.PointerPressed += (_, _) => { if (_track is not null) AlbumRequested?.Invoke(_track); };
        _scroll = new ScrollViewer { Content = new StackPanel { Children = { new Border { Height = 160 }, _lines, new Border { Height = 240 } } } };
        _scroll.ScrollChanged += OnScrollChanged;
        _scroll.AddHandler(PointerWheelChangedEvent, (_, _) => Manual(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(PointerPressedEvent, (_, _) => Manual(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        var play = new Button { Content = Icons.Play, Classes = { "icon" }, Padding = new Thickness(6, 2) };
        play.Click += (_, _) => SeekToCenter();
        _locator = new Border
        {
            IsVisible = false, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = true, Margin = new Thickness(8, 0),
            BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)),
            Child = new DockPanel { Children = { Dock(play, Avalonia.Controls.Dock.Right), _locatorTime } },
        };
        _hideLocator = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hideLocator.Tick += (_, _) => { _hideLocator.Stop(); _locator.IsVisible = false; };

        _searchButton.Click += (_, _) => { if (_track is not null) SearchRequested?.Invoke(_track); };
        _empty = new StackPanel
        {
            Spacing = 12, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
            Children = { new TextBlock { Text = "暂无歌词", Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center }, _searchButton },
        };

        Button Small(string text, string tip, Action action)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2), FontSize = 12, Background = Brushes.Transparent };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => action();
            return b;
        }
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4,
            Children =
            {
                Small("−0.5s", "歌词提前 0.5 秒", () => ChangeOffset(-500)), _offsetText, Small("+0.5s", "歌词推后 0.5 秒", () => ChangeOffset(500)),
                Small("整页", "切换整页 / 侧栏", () => FullPageToggled?.Invoke()),
            },
        };

        var lyricsArea = new Panel { Children = { _scroll, _locator, _empty } };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"), Margin = new Thickness(8, 8, 8, 4) };
        Row(_coverBox, 0);
        Row(new StackPanel { Margin = new Thickness(0, 12, 0, 8), Spacing = 2, Children = { _title, _subtitle } }, 1);
        Row(lyricsArea, 3);
        Row(toolbar, 4);
        void Row(Control c, int row) { Grid.SetRow(c, row); grid.Children.Add(c); }
        Content = grid;

        _player.TrackChanged += t => Load(t);
        _player.PositionUpdated += OnPosition;
        _player.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlayerViewModel.Cover)) _cover.Source = _player.Cover; };
        _lyrics.Changed += t => { if (t.TrackKey == _track?.TrackKey) Load(t); };
        Load(_player.Track);
    }

    static Control Dock(Control c, Dock dock)
    {
        DockPanel.SetDock(c, dock);
        return c;
    }

    /// <summary>整页时封面放大。</summary>
    public bool FullPage
    {
        set { _coverBox.Width = _coverBox.Height = value ? 320 : 220; }
    }

    public bool OnlineSearchAvailable { get; set; }

    void Load(Track? t)
    {
        _track = t;
        _title.Text = t?.Title ?? "";
        _subtitle.Text = t is null ? "" : (t.Album is { Length: > 0 } a ? $"{Text.Artist(t)} — {a}" : Text.Artist(t));
        _cover.Source = _player.Cover;
        _found = null;
        _current = -1;
        _lines.Children.Clear();
        _empty.IsVisible = false;
        if (t is null) return;
        _ = Task.Run(() => _lyrics.Load(t)).ContinueWith(task =>
        {
            if (_track != t) return;
            _found = task.Result;
            Render();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Render()
    {
        _lines.Children.Clear();
        _current = -1;
        _offsetText.Text = _found is { OffsetMs: not 0 } f ? $"{f.OffsetMs / 1000.0:+0.0;-0.0}s" : "0.0s";
        if (_found is null)
        {
            _empty.IsVisible = true;
            _searchButton.IsVisible = OnlineSearchAvailable;
            return;
        }
        _empty.IsVisible = false;
        foreach (var line in _found.Text.Lines)
        {
            var panel = new StackPanel { Spacing = 2, Tag = line, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = line.Text, FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.7 });
            if (line.Translation is { Length: > 0 } tr)
                panel.Children.Add(new TextBlock { Text = tr, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.55 });
            _lines.Children.Add(panel);
        }
        OnPosition(_app.Player.Position);
    }

    void OnPosition(TimeSpan position)
    {
        if (_found is not { Text.Synced: true } f || !IsEffectivelyVisible) return;
        var index = LrcParser.CurrentIndex(f.Text.Lines, (long)position.TotalMilliseconds - f.OffsetMs);
        if (index == _current) return;
        SetCurrent(index);
    }

    void SetCurrent(int index)
    {
        if (_current >= 0 && _current < _lines.Children.Count) Style(_lines.Children[_current], false);
        _current = index;
        if (index < 0 || index >= _lines.Children.Count) return;
        Style(_lines.Children[index], true);
        if (DateTime.Now >= _manualUntil) CenterOn(_lines.Children[index]);
    }

    static void Style(Control c, bool current)
    {
        if (c is not StackPanel p) return;
        foreach (var tb in p.Children.OfType<TextBlock>())
        {
            tb.Opacity = current ? 1 : 0.7;
            tb.FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal;
        }
        if (p.Children.FirstOrDefault() is TextBlock main) main.FontSize = current ? 19 : 16;
    }

    void CenterOn(Control line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var pos = line.TranslatePoint(new Point(0, line.Bounds.Height / 2), (Visual)_scroll.Content!);
            if (pos is null) return;
            _programmaticScroll = true;
            _scroll.Offset = new Vector(0, Math.Max(0, pos.Value.Y - _scroll.Viewport.Height / 2));
            _programmaticScroll = false;
        }, DispatcherPriority.Background);
    }

    /// <summary>用户自己滚动：暂停跟随 3 秒，显示定位线（需求 LY-3、LY-12）。</summary>
    void Manual() => _manualUntil = DateTime.Now.AddSeconds(3);

    void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_programmaticScroll || _found is not { Text.Synced: true }) return;
        Manual();
        if (CenterLine() is { } line)
        {
            _locator.IsVisible = true;
            _locatorTime.Text = Text.Duration(Math.Max(0, line.TimeMs + (_found?.OffsetMs ?? 0)));
            _hideLocator.Stop();
            _hideLocator.Start();
        }
    }

    LyricLine? CenterLine()
    {
        var center = _scroll.Offset.Y + _scroll.Viewport.Height / 2;
        LyricLine? best = null;
        var bestDistance = double.MaxValue;
        foreach (var c in _lines.Children)
        {
            var pos = c.TranslatePoint(new Point(0, c.Bounds.Height / 2), (Visual)_scroll.Content!);
            if (pos is null) continue;
            var d = Math.Abs(pos.Value.Y - center);
            if (d < bestDistance) { bestDistance = d; best = c.Tag as LyricLine; }
        }
        return best;
    }

    void SeekToCenter()
    {
        if (CenterLine() is not { } line || _found is null) return;
        _app.Player.Seek(TimeSpan.FromMilliseconds(Math.Max(0, line.TimeMs + _found.OffsetMs)));
        _locator.IsVisible = false;
        _manualUntil = DateTime.MinValue;
    }

    void ChangeOffset(int delta)
    {
        if (_track is null || _found is null) return;
        _lyrics.SetOffset(_track, _found.OffsetMs + delta);
    }
}
