using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using JeekMusic.Infrastructure;
using JeekMusic.Platform;
using JeekMusic.Settings;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

/// <summary>
/// 把服务和界面连起来（设计 §8、§12）：主窗口、托盘、任务栏、SMTC、全局快捷键、单实例、退出。
/// 启动顺序：读快照 → 后台读曲库 → 显示窗口 → 首帧之后创建 mpv、SMTC、任务栏按钮 → 曲库到了恢复上次的队列。
/// </summary>
public sealed partial class Shell
{
    readonly AppServices _app;
    readonly IClassicDesktopStyleApplicationLifetime _desktop;
    readonly UiState _ui = UiState.Load();
    MainWindow _main = null!;
    TrayIcon? _tray;
    bool _restored;
    bool _exiting;

    public Shell(AppServices app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _app = app;
        _desktop = desktop;
    }

    public static Shell Current { get; private set; } = null!;
    public MainWindow Main => _main;
    public UiState Ui => _ui;

    public void Start()
    {
        Current = this;
        ApplyTheme();
        _app.Settings.Changed += c => { if (c == "theme") ApplyTheme(); };

        // 曲库在后台读，窗口先显示出来
        _ = _app.Library.StartAsync();

        _main = new MainWindow(_app);
        RestoreWindow();
        _main.ViewModel.InitialTarget = InitialTarget();
        _main.Opened += (_, _) => OnFirstShown();
        _main.ViewModel.SnapshotApplied += OnSnapshotApplied;
        _main.TrayHint += () => _main.ViewModel.ShowToast(L.T("JeekMusic 仍在后台播放，可以从任务栏右下角的托盘图标退出"));
        CreateTray();

        _app.Instance?.Received += args => Dispatcher.UIThread.Post(() => OnInstanceArgs(args));
        _app.Instance?.Listen();

        var startHidden = _app.Args.Contains("--tray");
        // 自动测试时不打扰正在用电脑的人：不激活、不进任务栏、放到所有窗口后面（tech-spike.md"用法"一节）
        if (_app.Args.Contains("--background"))
        {
            _main.ShowActivated = false;
            _main.ShowInTaskbar = false;
            _main.Opened += (_, _) =>
            {
                if (_main.TryGetPlatformHandle()?.Handle is { } hwnd)
                    Win32.SetWindowPos(hwnd, 1 /* HWND_BOTTOM */, 0, 0, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE);
            };
        }
        if (!startHidden) _main.Show();
        else Dispatcher.UIThread.Post(OnFirstShown, DispatcherPriority.Background);
    }

    void ApplyTheme()
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = _app.Settings.Current.Theme switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    // ── 窗口位置（设计 §6.1）──

    void RestoreWindow()
    {
        _main.Width = _ui.Width;
        _main.Height = _ui.Height;
        if (_ui.X is { } x && _ui.Y is { } y)
        {
            var pos = new PixelPoint((int)x, (int)y);
            // 保存的位置不在任何一块显示器上（显示器拔掉了）时放回主显示器
            var screens = _main.Screens.All;
            if (screens.Any(s => s.WorkingArea.Contains(pos + new PixelPoint(40, 40))))
            {
                _main.WindowStartupLocation = WindowStartupLocation.Manual;
                _main.Position = pos;
            }
            else _main.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else _main.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_ui.Maximized) _main.WindowState = WindowState.Maximized;
        _main.RestoreLayout(_ui);
    }

    void SaveWindow()
    {
        if (_main.WindowState == WindowState.Normal)
        {
            _ui.X = _main.Position.X;
            _ui.Y = _main.Position.Y;
            _ui.Width = _main.Width;
            _ui.Height = _main.Height;
        }
        _ui.Maximized = _main.WindowState == WindowState.Maximized;
        _main.SaveLayout(_ui);
        switch (_main.ViewModel.Current)
        {
            case NavTarget.Folder f: _ui.ViewKind = "folder"; _ui.ViewId = f.FolderId; _ui.ViewKey = f.Dir; break;
            case NavTarget.Artists a: _ui.ViewKind = "artist"; _ui.ViewKey = a.ArtistKey; break;
            case NavTarget.PinnedArtist p: _ui.ViewKind = "pinned"; _ui.ViewKey = p.ArtistKey; break;
            case NavTarget.Playlist p: _ui.ViewKind = "playlist"; _ui.ViewId = p.PlaylistId; break;
            case NavTarget.Album a: _ui.ViewKind = "album"; _ui.ViewKey = a.AlbumKey; break;
        }
        _ui.Save();
    }

    NavTarget? InitialTarget() => _ui.ViewKind switch
    {
        "folder" => new NavTarget.Folder(_ui.ViewId, _ui.ViewKey ?? ""),
        "artist" => new NavTarget.Artists(_ui.ViewKey),
        "pinned" when _ui.ViewKey is { } k => new NavTarget.PinnedArtist(k),
        "playlist" => new NavTarget.Playlist(_ui.ViewId),
        "album" when _ui.ViewKey is { } k => new NavTarget.Album(k),
        _ => null,
    };

    // ── 首帧之后（设计 §12）──

    bool _firstShown;

    void OnFirstShown()
    {
        if (_firstShown) return;
        _firstShown = true;
        Log.Info("startup", $"Window opened after {(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds:F0} ms");
        // 窗口句柄：隐藏启动时也要有（快捷键、任务栏消息都发给它）
        _app.Messages.Attach(_main);
        _app.System.Attach(_app.Messages);
        _app.System.VolumesChanged += () => _ = _app.Library.RefreshVolumesAsync();
        _app.Hotkeys.Attach(_app.Messages);
        _app.Hotkeys.Pressed += OnHotkey;
        ApplyHotkeys();
        _app.Settings.Changed += c => { if (c == "hotkeys") ApplyHotkeys(); };
        DispatcherTimer.RunOnce(() =>
        {
            _app.Player.EnsureEngine();
            AttachSystemMedia();
            _app.Taskbar.Attach(_app.Messages.Hwnd, _app.Messages);
            _app.Taskbar.PlayPauseClicked += _app.Player.PlayPause;
            _app.Taskbar.PreviousClicked += _app.Player.Previous;
            _app.Taskbar.NextClicked += _app.Player.Next;
            UpdateSystemMedia();
            if (_restored) _app.Player.PrepareRestored();
            AfterStartup();
        }, TimeSpan.FromMilliseconds(300));
    }

    /// <summary>首帧之后的其他任务（同步、分析、检查更新），各功能在 partial 里实现。</summary>
    partial void AfterStartup();

    void OnSnapshotApplied()
    {
        // 自动测试：曲库到了之后播放当前视图的第一首
        if (_app.Args.Contains("--autoplay") && _main.ViewModel.Rows.FirstOrDefault(r => r.Track is not null)?.Track is { } first)
        {
            _app.Args = [.. _app.Args.Where(a => a != "--autoplay")];
            _main.PlayFrom(first);
        }
        if (_restored) return;
        _restored = true;
        _app.QueuePersistence.Restore(_app.Library.Snapshot);
        if (_app.Player.EngineReady) _app.Player.PrepareRestored();
        // 只有曲库和保存的队列就绪后才接受外部控制，避免启动恢复覆盖 MCP 命令。
        _app.Mcp.Start();
    }

    // ── SMTC、任务栏、托盘提示（需求 O-1、PC-6、W-5）──

    void AttachSystemMedia()
    {
        try
        {
            _app.Smtc = new Smtc(_app.Messages.Hwnd);
            _app.Smtc.ButtonPressed += button => Dispatcher.UIThread.Post(() =>
            {
                switch (button)
                {
                    case "Play": _app.Player.Resume(); break;
                    case "Pause": _app.Player.Pause(); break;
                    case "Next": _app.Player.Next(); break;
                    case "Previous": _app.Player.Previous(); break;
                    case "Stop": _app.Player.Pause(); break;
                }
            });
            _app.Smtc.PositionRequested += p => Dispatcher.UIThread.Post(() => _app.Player.Seek(p));
        }
        catch (Exception e)
        {
            Log.Warn("smtc", "SMTC 不可用", e);
        }
        _app.Player.StateChanged += UpdateSystemMedia;
        _app.Player.TrackChanged += _ => UpdateSystemMedia(trackChanged: true);
        _app.Player.Seeked += () => UpdateSystemMedia();
        _progressTimer = UiTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, () => UpdateTaskbarProgress());
    }

    DispatcherTimer? _progressTimer;

    void UpdateSystemMedia() => UpdateSystemMedia(false);

    void UpdateSystemMedia(bool trackChanged)
    {
        var p = _app.Player;
        var t = p.CurrentTrack;
        if (_app.Smtc is { } smtc)
        {
            if (t is null) smtc.Clear();
            else
            {
                if (trackChanged || !_smtcShown) smtc.Show(t.Title, Text.Artist(t), t.Album ?? "", CoverPathFor(t));
                _smtcShown = true;
                smtc.SetState(p.IsPlaying, p.Position, p.Duration);
            }
        }
        _app.Taskbar.SetPlaying(p.IsPlaying);
        if (_tray is not null) _tray.ToolTipText = t is null ? "JeekMusic" : $"{t.Title} — {Text.Artist(t)}";
        UpdateTaskbarProgress();
        // 任务栏进度每 2 秒更新一次，只在播放中（窗口最小化时也要更新）
        if (p.IsPlaying && _app.Settings.Current.TaskbarProgress) _progressTimer?.Start(); else _progressTimer?.Stop();
    }

    bool _smtcShown;

    /// <summary>SMTC 卡片上的封面文件（封面缓存做好后提供）。</summary>
    partial void CoverPathOf(Library.Track track, ref string? path);

    string? CoverPathFor(Library.Track t)
    {
        string? path = null;
        CoverPathOf(t, ref path);
        return path;
    }

    void UpdateTaskbarProgress()
    {
        var p = _app.Player;
        if (!_app.Settings.Current.TaskbarProgress || p.CurrentTrack is null || p.Duration.TotalSeconds <= 0) { _app.Taskbar.SetProgress(null, false); return; }
        _app.Taskbar.SetProgress(p.Position.TotalSeconds / p.Duration.TotalSeconds, !p.IsPlaying);
    }

    // ── 托盘（需求 PC-1）──

    void CreateTray()
    {
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://JeekMusic/Assets/JeekMusic.ico"))),
            ToolTipText = "JeekMusic",
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ToggleMainWindow();
        RebuildTrayMenu();
        _app.Player.StateChanged += RebuildTrayMenu;
        _app.Player.TrackChanged += _ => RebuildTrayMenu();
        TrayIcon.SetIcons(Application.Current!, [_tray]);
    }

    void RebuildTrayMenu()
    {
        if (_tray is null) return;
        var p = _app.Player;
        var menu = new NativeMenu();
        if (p.CurrentTrack is { } t) menu.Add(new NativeMenuItem($"{t.Title} — {Text.Artist(t)}") { IsEnabled = false });
        menu.Add(Item(p.IsPlaying ? L.T("暂停") : L.T("播放"), p.PlayPause));
        menu.Add(Item(L.T("上一首"), p.Previous));
        menu.Add(Item(L.T("下一首"), p.Next));
        menu.Add(new NativeMenuItemSeparator());
        AddTrayExtras(menu);
        menu.Add(Item(L.T("显示主窗口"), ShowMainWindow));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(L.T("退出"), Exit));
        _tray.Menu = menu;
    }

    /// <summary>桌面歌词、迷你播放器的托盘菜单项。</summary>
    partial void AddTrayExtras(NativeMenu menu);

    static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    public void ShowMainWindow()
    {
        HideMiniPlayer();
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    void ToggleMainWindow()
    {
        if (_main.IsVisible && _main.WindowState != WindowState.Minimized && _main.IsActive) _main.Hide();
        else ShowMainWindow();
    }

    /// <summary>迷你播放器（做好后实现）。</summary>
    partial void HideMiniPlayer();

    // ── 全局快捷键（需求 PC-3）──

    void ApplyHotkeys() => _app.Hotkeys.Apply(_app.Settings.Current.GlobalHotkeysEnabled, _app.Settings.Current.GlobalHotkeys);

    void OnHotkey(string action)
    {
        switch (action)
        {
            case "PlayPause": _app.Player.PlayPause(); break;
            case "Previous": _app.Player.Previous(); break;
            case "Next": _app.Player.Next(); break;
            default: OnExtraHotkey(action); break;
        }
    }

    partial void OnExtraHotkey(string action);

    // ── 单实例收到的参数（需求 PC-7）──

    void OnInstanceArgs(string[] args)
    {
        var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (files.Count > 0) OpenFiles(files);
        else if (!args.Contains("--background")) ShowMainWindow();
    }

    /// <summary>双击音乐文件、拖进窗口：直接播放（需求 PC-7、§3.7）。</summary>
    partial void OpenFiles(IReadOnlyList<string> files);

    // ── 退出 ──

    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _main.ExitRequested = true;
        SaveWindow();
        BeforeExit();
        _tray?.Dispose();
        _app.Dispose();
        _desktop.Shutdown();
    }

    partial void BeforeExit();
}
