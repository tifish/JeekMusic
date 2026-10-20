using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using JeekMusic.Library;

namespace JeekMusic.UI;

/// <summary>迷你播放器、桌面歌词、打开文件（需求 PC-4、PC-5、PC-7）。</summary>
public sealed partial class Shell
{
    MiniPlayerWindow? _mini;
    DesktopLyricsWindow? _desktopLyrics;

    // ── 迷你播放器 ──

    public void ShowMiniPlayer()
    {
        if (_mini is null)
        {
            _mini = new MiniPlayerWindow(_main.ViewModel.Player, _app.Settings.Current.MiniPlayerTopmost);
            _mini.RestoreRequested += ShowMainWindow;
            _mini.TopmostChanged += v => { _app.Settings.Current.MiniPlayerTopmost = v; _app.Settings.Save("mini"); };
            _mini.Closing += (_, e) => { e.Cancel = true; ShowMainWindow(); };
            if (_ui.MiniX is { } x && _ui.MiniY is { } y)
            {
                _mini.WindowStartupLocation = WindowStartupLocation.Manual;
                _mini.Position = new PixelPoint((int)x, (int)y);
            }
            else _mini.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _mini.PositionChanged += (_, _) => { _ui.MiniX = _mini.Position.X; _ui.MiniY = _mini.Position.Y; };
        }
        _main.MiniPlayerVisible = true;
        _main.Hide();
        _mini.Show();
        _main.ViewModel.Player.ProgressVisible = true;
    }

    partial void HideMiniPlayer()
    {
        if (_mini is null || !_mini.IsVisible) return;
        _main.MiniPlayerVisible = false;
        _mini.Hide();
    }

    // ── 桌面歌词 ──

    public bool DesktopLyricsVisible => _desktopLyrics?.IsVisible == true;

    public void SetDesktopLyrics(bool visible)
    {
        _app.Settings.Current.DesktopLyricsVisible = visible;
        _app.Settings.Save("lyrics-window");
        if (!visible) { _desktopLyrics?.Hide(); RebuildTrayMenu(); return; }
        if (_desktopLyrics is null)
        {
            _desktopLyrics = new DesktopLyricsWindow(_app.Player, _app.Lyrics, _app.Settings);
            _desktopLyrics.VisibilityChanged += SetDesktopLyrics;
            var screen = _main.Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            var scale = _main.Screens.Primary?.Scaling ?? 1;
            _desktopLyrics.Width = _ui.LyricsBarWidth ?? Math.Min(1000, screen.Width / scale * 0.6);
            _desktopLyrics.WindowStartupLocation = WindowStartupLocation.Manual;
            _desktopLyrics.Position = _ui.LyricsX is { } x && _ui.LyricsY is { } y
                ? new PixelPoint((int)x, (int)y)
                : new PixelPoint(screen.X + (int)((screen.Width - _desktopLyrics.Width * scale) / 2), screen.Y + screen.Height - (int)(140 * scale));
            _desktopLyrics.PositionChanged += (_, _) => { _ui.LyricsX = _desktopLyrics.Position.X; _ui.LyricsY = _desktopLyrics.Position.Y; };
        }
        _desktopLyrics.Show();
        RebuildTrayMenu();
    }

    public void ToggleDesktopLyricsLock()
    {
        if (_desktopLyrics is null || !_desktopLyrics.IsVisible) { SetDesktopLyrics(true); return; }
        _desktopLyrics.SetLocked(!_desktopLyrics.Locked);
        RebuildTrayMenu();
    }

    partial void AddTrayExtras(NativeMenu menu)
    {
        var lyrics = new NativeMenuItem("桌面歌词") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = DesktopLyricsVisible };
        lyrics.Click += (_, _) => SetDesktopLyrics(!DesktopLyricsVisible);
        menu.Add(lyrics);
        if (DesktopLyricsVisible)
        {
            var locked = new NativeMenuItem("锁定桌面歌词") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = _desktopLyrics!.Locked };
            locked.Click += (_, _) => ToggleDesktopLyricsLock();
            menu.Add(locked);
        }
        var mini = new NativeMenuItem("迷你播放器");
        mini.Click += (_, _) => ShowMiniPlayer();
        menu.Add(mini);
        menu.Add(new NativeMenuItemSeparator());
    }

    partial void OnExtraHotkey(string action)
    {
        switch (action)
        {
            case "DesktopLyrics": SetDesktopLyrics(!DesktopLyricsVisible); break;
            case "LockLyrics": ToggleDesktopLyricsLock(); break;
            case "ShowWindow": ShowMainWindow(); break;
        }
    }

    // ── 打开文件（需求 PC-7、§3.7）──

    /// <summary>
    /// 播放外部的文件：在曲库里的直接按曲库里的歌播放；不在曲库里的提示先添加所在的文件夹。
    /// </summary>
    partial void OpenFiles(IReadOnlyList<string> files)
    {
        if (!_app.Args.Contains("--background")) ShowMainWindow();
        var snapshot = _app.Library.Snapshot;
        var ids = new List<long>();
        var outside = 0;
        foreach (var path in files)
        {
            if (Directory.Exists(path)) { _ = _main.AddFolder(path); continue; }
            var full = Path.GetFullPath(path);
            var track = snapshot.Tracks.FirstOrDefault(t => !t.IsCue && string.Equals(snapshot.PathOf(t), full, StringComparison.OrdinalIgnoreCase));
            if (track is not null) ids.Add(track.Id);
            else outside++;
        }
        if (ids.Count > 0) _app.Player.Play(null, ids, 0, shuffle: false);
        if (outside > 0) _main.ViewModel.ShowToast($"有 {outside} 个文件不在曲库里：先把它们所在的文件夹添加为音乐文件夹");
    }

    public void Open(IReadOnlyList<string> files) => OpenFiles(files);

    partial void BeforeExit()
    {
        _desktopLyrics?.Close();
        if (_desktopLyrics is not null) _ui.LyricsBarWidth = _desktopLyrics.Width;
    }

    partial void AfterStartup()
    {
        // 命令行里的文件和文件夹：文件直接播放，文件夹添加为音乐文件夹（需求 PC-7）
        var paths = _app.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && (File.Exists(a) || Directory.Exists(a))).ToList();
        if (paths.Count > 0) OpenFiles(paths);
        if (_app.Settings.Current.DesktopLyricsVisible) SetDesktopLyrics(true);
        StartBackgroundServices();
    }

    /// <summary>同步、智能标签、在线获取、检查更新（各功能的 partial 里实现）。</summary>
    partial void StartBackgroundServices();
}
