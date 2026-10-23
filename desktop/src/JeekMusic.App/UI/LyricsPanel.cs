using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using System.Text.Json.Nodes;
using JeekMusic.Infrastructure;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    readonly Button _searchButton = new() { Content = L.T("搜索歌词"), HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _offsetText = new() { FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
    Track? _track;
    FoundLyrics? _found;
    int _current = -1;
    readonly Func<Track, Task<FoundLyrics?>> _loadLyrics;
    readonly TimeProvider _clock;
    readonly Border _topSpace = new(), _bottomSpace = new();
    readonly HashSet<IPointer> _pressed = [];
    readonly HashSet<InputElement> _captureOwners = [];
    readonly List<Visual> _visibilityAncestors = [];
    readonly DispatcherTimer _resumeFollow;
    long _lastInteraction;
    long _seekRevision;
    bool _manual, _centerPending;
    int _loadVersion;
    Size _lastViewport, _lastLinesSize;

    /// <summary>"搜索歌词"按钮（在线歌词做好后接上）。</summary>
    public event Action<Track>? SearchRequested;
    /// <summary>点了专辑 / 艺术家。</summary>
    public event Action<Track>? AlbumRequested;
    public event Action? FullPageToggled;

    public LyricsPanel(AppServices app, PlayerViewModel player, LyricsService lyrics)
        : this(player, lyrics)
    {
        app.LyricsPanel = this;
    }

    // The same control can be exercised with a simulated player and delayed lyric loads.
    internal LyricsPanel(PlayerViewModel player, LyricsService lyrics,
        Func<Track, Task<FoundLyrics?>>? loadLyrics = null, TimeProvider? clock = null)
    {
        _loadLyrics = loadLyrics ?? (t => Task.Run(() => lyrics.Load(t)));
        _clock = clock ?? TimeProvider.System;
        _player = player;
        _seekRevision = player.Service.SeekRevision;
        _lyrics = lyrics;
        _coverBox = new Border
        {
            Width = 220, Height = 220, CornerRadius = new CornerRadius(8), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)), Child = _cover,
        };
        _subtitle.PointerPressed += (_, _) => { if (_track is not null) AlbumRequested?.Invoke(_track); };
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Children = { _topSpace, _lines, _bottomSpace } },
        };
        _scroll.ScrollChanged += OnScrollChanged;
        _scroll.AddHandler(PointerWheelChangedEvent, (_, _) => Manual(), RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(ScrollGestureEvent, (_, _) => Manual(), RoutingStrategies.Bubble, handledEventsToo: true);
        // A touch recognizer can consume PointerReleased after taking capture.
        // Its inertia/end events still mark the physical contact as released.
        _scroll.AddHandler(ScrollGestureInertiaStartingEvent, (_, _) => ReleaseTouch(), RoutingStrategies.Bubble, handledEventsToo: true);
        _scroll.AddHandler(ScrollGestureEndedEvent, (_, _) => ReleaseTouch(), RoutingStrategies.Bubble, handledEventsToo: true);
        _scroll.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!CanFollow) return;
            _pressed.Add(e.Pointer);
            // CaptureLost is direct, so observe the pressed template ancestry
            // (including a scrollbar Thumb), rather than expecting it to bubble.
            for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
            {
                if (visual is InputElement input && _captureOwners.Add(input))
                    input.AddHandler(PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Direct);
                if (visual == _scroll) break;
            }
            Manual();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(PointerMovedEvent, (_, e) => { if (_pressed.Contains(e.Pointer)) Manual(); }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(PointerReleasedEvent, (_, e) => Release(e.Pointer), RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) Manual();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        var play = new Button { Content = Icons.Play, Classes = { "icon" }, Padding = new Thickness(6, 2) };
        ToolTip.SetTip(play, L.T("跳转到此句并恢复跟随"));
        play.Click += (_, _) => SeekToCenter();
        _locator = new Border
        {
            IsVisible = false, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = true, Margin = new Thickness(8, 0),
            BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)),
            Child = new DockPanel { Children = { Dock(play, Avalonia.Controls.Dock.Right), _locatorTime } },
        };
        _resumeFollow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _resumeFollow.Tick += (_, _) => ResumeFollowIfDue();
        ToolTip.SetTip(_scroll, L.T("手动滚动后 3 秒恢复歌词跟随"));
        LayoutUpdated += (_, _) => FollowLayout();
        AttachedToVisualTree += (_, _) =>
        {
            _visibilityAncestors.Add(this);
            _visibilityAncestors.AddRange(this.GetVisualAncestors());
            foreach (var visual in _visibilityAncestors) visual.PropertyChanged += OnVisibilityProperty;
            VisibilityChanged();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            foreach (var visual in _visibilityAncestors) visual.PropertyChanged -= OnVisibilityProperty;
            _visibilityAncestors.Clear();
            ResetManual();
        };

        _searchButton.Click += (_, _) => { if (_track is not null) SearchRequested?.Invoke(_track); };
        _empty = new StackPanel
        {
            Spacing = 12, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
            Children = { new TextBlock { Text = L.T("暂无歌词"), Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center }, _searchButton },
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
                Small("−0.5s", L.T("歌词提前 0.5 秒"), () => ChangeOffset(-500)), _offsetText, Small("+0.5s", L.T("歌词推后 0.5 秒"), () => ChangeOffset(500)),
                Small(L.T("整页"), L.T("切换整页 / 侧栏"), () => FullPageToggled?.Invoke()),
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
        _player.Service.Seeked += OnSeek;
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

    async void Load(Track? t)
    {
        var version = ++_loadVersion;
        ResetManual();
        _track = t;
        _title.Text = t?.Title ?? "";
        _subtitle.Text = t is null ? "" : (t.Album is { Length: > 0 } a ? $"{Text.Artist(t)} — {a}" : Text.Artist(t));
        _cover.Source = _player.Cover;
        _found = null;
        _current = -1;
        _lines.Children.Clear();
        _scroll.Offset = default;
        _empty.IsVisible = false;
        if (t is null) return;
        try
        {
            var found = await _loadLyrics(t);
            // Version also protects reloads of the same song (offset/search changes).
            if (version != _loadVersion) return;
            _found = found;
            Render();
        }
        catch (Exception e)
        {
            if (version != _loadVersion) return;
            Log.Warn("lyrics", "读取歌词失败", e);
            Render();
        }
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
            var panel = new StackPanel { Spacing = 2, Tag = line, HorizontalAlignment = HorizontalAlignment.Stretch };
            panel.Children.Add(new TextBlock { Text = line.Text, FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.7 });
            if (line.Translation is { Length: > 0 } tr)
                panel.Children.Add(new TextBlock { Text = tr, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.55 });
            _lines.Children.Add(panel);
        }
        OnPosition(_player.Service.Position);
    }

    void OnPosition(TimeSpan position)
    {
        if (_found is not { Text.Synced: true } f || !CanFollow) return;
        var index = LrcParser.CurrentIndex(f.Text.Lines, (long)position.TotalMilliseconds - f.OffsetMs);
        if (index == _current) return;
        SetCurrent(index);
    }

    void SetCurrent(int index)
    {
        if (_current >= 0 && _current < _lines.Children.Count) Style(_lines.Children[_current], false);
        _current = index;
        if (index >= 0 && index < _lines.Children.Count) Style(_lines.Children[index], true);
        QueueCenter();
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

    bool DisplayVisible => VisualRoot is not null && IsEffectivelyVisible && VisualRoot is not Window { WindowState: WindowState.Minimized };
    bool CanFollow => DisplayVisible && _found is { Text.Synced: true };

    void OnVisibilityProperty(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty && e.Property != Window.WindowStateProperty) return;
        ResetManual();
        Dispatcher.UIThread.Post(VisibilityChanged, DispatcherPriority.Loaded);
    }

    void QueueCenter()
    {
        if (!CanFollow || _manual || _centerPending) return;
        _centerPending = true;
        // Coalesce rapid positions/layouts. Resolve the current row at execution time,
        // never retain a row from a previous song or an obsolete seek target.
        Dispatcher.UIThread.Post(() =>
        {
            _centerPending = false;
            if (!CanFollow || _manual || _lines.Children.Count == 0 || _current >= _lines.Children.Count || _scroll.Viewport.Height <= 0) return;
            var line = _lines.Children[Math.Max(0, _current)];
            var pos = line.TranslatePoint(new Point(0, line.Bounds.Height / 2), (Visual)_scroll.Content!);
            if (pos is null) return;
            var target = Math.Clamp(pos.Value.Y - _scroll.Viewport.Height / 2, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height));
            if (Math.Abs(_scroll.Offset.Y - target) > 0.5) _scroll.Offset = new Vector(0, target);
        }, DispatcherPriority.Loaded);
    }

    void FollowLayout()
    {
        if (!DisplayVisible) return;
        var viewport = _scroll.Viewport;
        var size = _lines.Bounds.Size;
        var space = _found is { Text.Synced: true } ? viewport.Height / 2 : 0;
        if (viewport == _lastViewport && size == _lastLinesSize && _topSpace.Height == space) return;
        _lastViewport = viewport;
        _lastLinesSize = size;
        // Enough room for the first and last bilingual/wrapped row to reach center.
        _topSpace.Height = _bottomSpace.Height = space;
        QueueCenter();
    }

    void VisibilityChanged()
    {
        ResetManual();
        if (!DisplayVisible) return;
        OnPosition(_player.Service.Position);
        QueueCenter();
    }

    void OnSeek()
    {
        // An explicit external seek wins over manual browsing, including within one row.
        // A delayed decoder acknowledgement/resume must not cancel newer user input.
        if (_seekRevision != _player.Service.SeekRevision)
        {
            _seekRevision = _player.Service.SeekRevision;
            ResetManual();
        }
        OnPosition(_player.Service.Position);
        QueueCenter();
    }

    void ResetManual()
    {
        _manual = false;
        _pressed.Clear();
        ClearCaptureOwners();
        _resumeFollow.Stop();
        _locator.IsVisible = false;
    }

    void Release(IPointer pointer)
    {
        if (_pressed.Remove(pointer) && _manual) Manual();
        if (_pressed.Count == 0) ClearCaptureOwners();
    }

    void ReleaseTouch()
    {
        if (_pressed.RemoveWhere(p => p.Type == PointerType.Touch) > 0 && _manual) Manual();
        if (_pressed.Count == 0) ClearCaptureOwners();
    }

    void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Touch release is reported by gesture end/inertia after the recognizer
        // takes capture; losing capture to that recognizer is not a release.
        if (e.Pointer.Type != PointerType.Touch && e.Pointer.Captured is null) Release(e.Pointer);
    }

    void ClearCaptureOwners()
    {
        foreach (var input in _captureOwners) input.RemoveHandler(PointerCaptureLostEvent, OnCaptureLost);
        _captureOwners.Clear();
    }

    /// <summary>Only input starts manual browsing; deferred program/layout scroll events never do.</summary>
    void Manual()
    {
        if (!CanFollow) return;
        _manual = true;
        _lastInteraction = _clock.GetTimestamp();
        _resumeFollow.Stop();
        _resumeFollow.Interval = TimeSpan.FromSeconds(3);
        _resumeFollow.Start();
        UpdateLocator();
    }

    internal void ResumeFollowIfDue()
    {
        _resumeFollow.Stop();
        if (!_manual || !CanFollow) return;
        if (_pressed.Count > 0) return; // Release starts a fresh three second interval.
        var remaining = TimeSpan.FromSeconds(3) - _clock.GetElapsedTime(_lastInteraction);
        if (remaining > TimeSpan.Zero)
        {
            _resumeFollow.Interval = remaining;
            _resumeFollow.Start();
            return;
        }
        ResetManual();
        OnPosition(_player.Service.Position);
        QueueCenter();
    }

    void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Offset changes may arrive after Offset was assigned or after a layout pass.
        // They update the locator only while input has already suspended following.
        if (!_manual || !CanFollow) return;
        UpdateLocator();
    }

    void UpdateLocator()
    {
        if (CenterLine() is not { } line) return;
        _locator.IsVisible = true;
        _locatorTime.Text = Text.Duration(Math.Max(0, line.TimeMs + (_found?.OffsetMs ?? 0)));
    }

    internal JsonObject Diagnostics() => new()
    {
        ["created"] = true, ["visible"] = DisplayVisible,
        ["trackKey"] = _track?.TrackKey, ["loaded"] = _found is not null,
        ["synced"] = _found?.Text.Synced ?? false, ["lineCount"] = _lines.Children.Count,
        ["currentIndex"] = _current, ["offsetMs"] = _found?.OffsetMs,
        ["following"] = CanFollow && !_manual, ["manualBrowsing"] = _manual,
        ["pointerHeld"] = _pressed.Count > 0, ["locatorVisible"] = _locator.IsVisible,
        ["resumeAfterMs"] = _manual ? Math.Max(0, 3000 - _clock.GetElapsedTime(_lastInteraction).TotalMilliseconds) : 0,
        ["scrollOffsetY"] = _scroll.Offset.Y, ["viewportHeight"] = _scroll.Viewport.Height,
        ["centerLineTimeMs"] = CenterLine()?.TimeMs,
    };

    LyricLine? CenterLine()
    {
        if (_found is not { Text.Synced: true } || _lines.Children.Count == 0) return null;
        var center = _scroll.Offset.Y + _scroll.Viewport.Height / 2;
        double? RowCenter(int index)
        {
            var c = _lines.Children[index];
            var pos = c.TranslatePoint(new Point(0, c.Bounds.Height / 2), (Visual)_scroll.Content!);
            return pos?.Y;
        }
        // StackPanel row centers are ordered even with wrapping/translations.
        // Avoid scanning thousands of rows on every wheel/touch notification.
        int low = 0, high = _lines.Children.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (RowCenter(mid) is not { } y) return null;
            if (y < center) low = mid + 1; else high = mid;
        }
        var best = Math.Min(low, _lines.Children.Count - 1);
        if (best > 0 && RowCenter(best - 1) is { } previous && RowCenter(best) is { } next &&
            Math.Abs(previous - center) <= Math.Abs(next - center)) best--;
        return _lines.Children[best].Tag as LyricLine;
    }

    void SeekToCenter()
    {
        if (CenterLine() is not { } line || _found is null) return;
        ResetManual();
        _player.Service.Seek(TimeSpan.FromMilliseconds(Math.Max(0, line.TimeMs + _found.OffsetMs)));
        OnSeek();
    }

    void ChangeOffset(int delta)
    {
        if (_track is null || _found is null) return;
        _lyrics.SetOffset(_track, _found.OffsetMs + delta);
    }
}
