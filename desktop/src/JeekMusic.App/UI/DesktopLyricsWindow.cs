using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using JeekMusic.Core.Lyrics;
using JeekMusic.Library;
using JeekMusic.Lyrics;
using JeekMusic.Platform;
using JeekMusic.Playback;
using JeekMusic.Settings;

namespace JeekMusic.UI;

/// <summary>
/// 桌面歌词（需求 PC-5，设计 §8.4）：无边框、透明、总在最前、不进任务栏、显示时不激活。文字带描边，任何背景上都看得清。
/// 锁定后鼠标点击穿透（WS_EX_TRANSPARENT），从托盘或快捷键解锁。只在换行时重绘：按下一行的时间设一次性定时器，不按帧刷新。
/// </summary>
public sealed class DesktopLyricsWindow : Window
{
    readonly PlayerService _player;
    readonly LyricsService _lyrics;
    readonly SettingsStore _settings;
    readonly OutlinedText _line1 = new();
    readonly OutlinedText _line2 = new();
    readonly Border _toolbar;
    readonly DispatcherTimer _next = new();
    FoundLyrics? _found;
    Track? _track;

    public DesktopLyricsWindow(PlayerService player, LyricsService lyrics, SettingsStore settings)
    {
        _player = player;
        _lyrics = lyrics;
        _settings = settings;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        Width = 900;
        Title = L.T("JeekMusic 桌面歌词");

        Button Tool(string glyph, string tip, Action action)
        {
            var b = new Button { Content = glyph, Classes = { "icon" }, Foreground = Brushes.White };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => action();
            return b;
        }
        _toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), CornerRadius = new CornerRadius(6), Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(6, 2),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 2,
                Children =
                {
                    Tool(Icons.Previous, L.T("上一首"), player.Previous), Tool(Icons.Play, L.T("播放 / 暂停"), player.PlayPause), Tool(Icons.Next, L.T("下一首"), player.Next),
                    Tool("A−", L.T("缩小字号"), () => Resize(-2)), Tool("A+", L.T("放大字号"), () => Resize(2)),
                    Tool(Icons.Lock, L.T("锁定（鼠标穿透；从托盘或快捷键解锁）"), () => SetLocked(true)),
                    Tool(Icons.Close, L.T("关闭桌面歌词"), () => VisibilityChanged?.Invoke(false)),
                },
            },
        };
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), // 几乎透明但能接收鼠标（未锁定时拖动）
            Padding = new Thickness(12, 4),
            Child = new StackPanel { Spacing = 2, Children = { _toolbar, _line1, _line2 } },
        };
        root.PointerEntered += (_, _) => { if (!Locked) _toolbar.Opacity = 1; root.Background = new SolidColorBrush(Color.FromArgb(Locked ? (byte)1 : (byte)60, 0, 0, 0)); };
        root.PointerExited += (_, _) => { _toolbar.Opacity = 0; root.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); };
        root.PointerPressed += (_, e) => { if (!Locked && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        Content = root;
        ApplyStyle();
        settings.Changed += category =>
        {
            if (category != "lyrics-window") return;
            ApplyStyle();
            if (Locked != settings.Current.DesktopLyricsLocked) SetLocked(settings.Current.DesktopLyricsLocked);
        };

        _next.Tick += (_, _) => { _next.Stop(); Update(); };
        player.TrackChanged += t => Load(t);
        player.StateChanged += () => { if (!player.IsPlaying) _next.Stop(); else Update(); };
        player.Seeked += Update;
        lyrics.Changed += t => { if (t.TrackKey == _track?.TrackKey) Load(t); };
        Opened += (_, _) =>
        {
            SetLocked(_settings.Current.DesktopLyricsLocked);
            Load(player.CurrentTrack);
        };
    }

    public event Action<bool>? VisibilityChanged;

    public bool Locked { get; private set; }

    /// <summary>锁定：鼠标点击穿透到下面的窗口。</summary>
    public void SetLocked(bool locked)
    {
        Locked = locked;
        _settings.Current.DesktopLyricsLocked = locked;
        _settings.Save("lyrics-window");
        _toolbar.Opacity = 0;
        if (TryGetPlatformHandle()?.Handle is not { } hwnd) return;
        var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        style |= (nint)(Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        style = locked ? style | (nint)Win32.WS_EX_TRANSPARENT : style & ~(nint)Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, style);
    }

    void Resize(double delta)
    {
        _settings.Current.DesktopLyricsFontSize = Math.Clamp(_settings.Current.DesktopLyricsFontSize + delta, 14, 72);
        _settings.Save("lyrics-window");
        ApplyStyle();
    }

    public void ApplyStyle()
    {
        var color = Color.TryParse(_settings.Current.DesktopLyricsColor, out var c) ? c : Colors.Gold;
        _line1.Fill = new SolidColorBrush(color);
        _line1.FontSize = _settings.Current.DesktopLyricsFontSize;
        _line2.Fill = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        _line2.FontSize = _settings.Current.DesktopLyricsFontSize * 0.7;
    }

    void Load(Track? t)
    {
        _track = t;
        _found = null;
        _next.Stop();
        Show("", "");
        if (t is null) return;
        _ = Task.Run(() => _lyrics.Load(t)).ContinueWith(task =>
        {
            if (_track != t) return;
            _found = task.Result;
            Update();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Show(string first, string second)
    {
        _line1.Text = first;
        _line2.Text = second;
        _line2.IsVisible = second.Length > 0;
        // 没有歌词时窗口内容是空的（看不见），不隐藏窗口以免抢焦点
        Opacity = first.Length == 0 && second.Length == 0 ? 0 : 1;
    }

    /// <summary>显示当前行，并把定时器设到下一行开始的时刻（设计 §8.4）。</summary>
    void Update()
    {
        _next.Stop();
        if (_found is null) { Show(_track?.Title ?? "", ""); return; }
        var lines = _found.Text.Lines;
        if (!_found.Text.Synced) { Show(_track?.Title ?? "", Text.Artist(_track!)); return; }
        var pos = (long)_player.Position.TotalMilliseconds - _found.OffsetMs;
        var i = LrcParser.CurrentIndex(lines, pos);
        if (i < 0) Show(_track?.Title ?? "", lines.Count > 0 ? lines[0].Text : "");
        else
        {
            var line = lines[i];
            // 双语歌词两行；否则第二行是下一句
            Show(line.Text, line.Translation ?? (i + 1 < lines.Count ? lines[i + 1].Text : ""));
        }
        if (!_player.IsPlaying) return;
        var nextIndex = i + 1;
        if (nextIndex >= lines.Count) return;
        var wait = lines[nextIndex].TimeMs - pos;
        _next.Interval = TimeSpan.FromMilliseconds(Math.Clamp(wait, 30, 60_000));
        _next.Start();
    }
}

/// <summary>带描边的文字（桌面歌词在任何背景上都看得清）。</summary>
public sealed class OutlinedText : Control
{
    public string Text { get => _text; set { _text = value; InvalidateMeasure(); InvalidateVisual(); } }
    public IBrush Fill { get; set; } = Brushes.White;
    public double FontSize { get => _fontSize; set { _fontSize = value; InvalidateMeasure(); InvalidateVisual(); } }
    string _text = "";
    double _fontSize = 30;

    FormattedText Format() => new(_text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), _fontSize, Fill) { TextAlignment = TextAlignment.Center, MaxTextWidth = 2000 };

    protected override Size MeasureOverride(Size availableSize)
    {
        var f = Format();
        f.MaxTextWidth = double.IsInfinity(availableSize.Width) ? 2000 : availableSize.Width;
        return new Size(availableSize.Width is var w && double.IsInfinity(w) ? f.Width : w, Math.Max(f.Height, _fontSize * 1.3) + 4);
    }

    public override void Render(DrawingContext context)
    {
        if (_text.Length == 0) return;
        var f = Format();
        f.MaxTextWidth = Bounds.Width;
        var origin = new Point(0, 2);
        var geometry = f.BuildGeometry(origin);
        if (geometry is not null)
        {
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), Math.Max(2, _fontSize / 9), lineJoin: PenLineJoin.Round), geometry);
            context.DrawGeometry(Fill, null, geometry);
        }
    }
}
