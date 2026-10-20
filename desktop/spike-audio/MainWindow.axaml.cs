using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace JeekMusic.AudioSpike;

/// <summary>手动测试窗口：用耳朵听无缝播放和独占模式，检查媒体键和音量弹窗里的媒体卡片。</summary>
public partial class MainWindow : Window
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);

    static readonly HashSet<string> AudioExtensions =
        new([".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav", ".wma", ".ape"], StringComparer.OrdinalIgnoreCase);

    readonly Mpv _mpv;
    readonly Dictionary<string, string?> _props = new();
    List<string> _files = new();
    Smtc? _smtc;
    bool _updatingPosition;
    TimeSpan _lastCpu;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    public MainWindow()
    {
        InitializeComponent();

        // 自动测试时不打扰正在用电脑的人：不抢焦点、不进任务栏，打开后放到所有窗口后面
        if (HasArg("--background"))
        {
            ShowActivated = false;
            ShowInTaskbar = false;
        }

        _mpv = new Mpv(Mpv.BaseOptions().Concat([("ao", "wasapi"), ("gapless-audio", "weak")]));
        foreach (var name in new[]
                 {
                     "pause", "idle-active", "duration", "playlist-pos", "media-title",
                     "metadata/by-key/artist", "metadata/by-key/album", "audio-codec-name",
                     "audio-params/samplerate", "audio-params/format", "audio-params/channel-count",
                     "audio-out-params/samplerate", "audio-out-params/format", "audio-out-params/channel-count",
                     "current-ao", "audio-device", "audio-exclusive", "gapless-audio", "volume",
                 })
            _mpv.Observe(name);
        _mpv.Notified += (id, name, value) =>
        {
            if (id != Mpv.EventId.PropertyChange || name is null) return;
            Dispatcher.UIThread.Post(() => OnProperty(name, value));
        };

        OpenButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择一张专辑或一个音乐文件夹" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) LoadFolder(path);
        };
        TrackList.DoubleTapped += (_, _) => PlayIndex(TrackList.SelectedIndex);
        TrackList.KeyDown += (_, e) => { if (e.Key == Key.Enter) PlayIndex(TrackList.SelectedIndex); };
        PlayButton.Click += (_, _) => TogglePause();
        PrevButton.Click += (_, _) => _mpv.TryRun("playlist-prev");
        NextButton.Click += (_, _) => _mpv.TryRun("playlist-next");

        // 独占模式要重新打开音频设备才生效（ao-reload），正在播放时会停顿一下
        ExclusiveBox.IsCheckedChanged += (_, _) =>
        {
            _mpv.Set("audio-exclusive", ExclusiveBox.IsChecked == true ? "yes" : "no");
            _mpv.TryRun("ao-reload");
        };
        GaplessBox.SelectionChanged += (_, _) => _mpv.Set("gapless-audio", GaplessBox.SelectedIndex switch { 1 => "yes", 2 => "no", _ => "weak" });
        VolumeSlider.ValueChanged += (_, e) => _mpv.Set("volume", ((int)e.NewValue).ToString(CultureInfo.InvariantCulture));
        PositionSlider.ValueChanged += (_, e) =>
        {
            if (!_updatingPosition) _mpv.TryRun("seek", e.NewValue.ToString("F3", CultureInfo.InvariantCulture), "absolute");
        };

        // 排查耗电用：--no-timers 不刷新播放位置和统计
        if (!Environment.GetCommandLineArgs().Contains("--no-timers"))
        {
            new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStats()).Start();
            // 播放位置每秒变化几十次，观察它会让界面一直重绘；每 250ms 读一次就够了
            new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => OnProperty("time-pos", _mpv.Get("time-pos"))).Start();
        }

        var args = Environment.GetCommandLineArgs();
        var folderArg = Array.IndexOf(args, "--folder");
        if (folderArg >= 0 && folderArg + 1 < args.Length) LoadFolder(args[folderArg + 1]);
        if (args.Contains("--exclusive")) ExclusiveBox.IsChecked = true;
        if (args.Contains("--autoplay")) PlayIndex(0);
    }

    static bool HasArg(string name) => Environment.GetCommandLineArgs().Contains(name);

    static string? ArgValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var hwnd = TryGetPlatformHandle()!.Handle;
        if (HasArg("--background"))
        {
            const uint noSize = 0x1, noMove = 0x2, noActivate = 0x10;
            SetWindowPos(hwnd, 1 /* HWND_BOTTOM */, 0, 0, 0, 0, noSize | noMove | noActivate);
        }
        if (HasArg("--minimized")) ShowWindow(hwnd, 7 /* SW_SHOWMINNOACTIVE：最小化但不激活 */);
        if (ArgValue("--measure-cpu") is { } seconds) MeasureCpu(double.Parse(seconds, CultureInfo.InvariantCulture));
        try
        {
            _smtc = new Smtc(TryGetPlatformHandle()!.Handle);
            _smtc.ButtonPressed += button => Dispatcher.UIThread.Post(() =>
            {
                switch (button)
                {
                    case "Play": _mpv.Set("pause", "no"); break;
                    case "Pause": _mpv.Set("pause", "yes"); break;
                    case "Next": _mpv.TryRun("playlist-next"); break;
                    case "Previous": _mpv.TryRun("playlist-prev"); break;
                }
            });
        }
        catch (Exception ex)
        {
            StatsText.Text = "SMTC 失败：" + ex.Message;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _mpv.Dispose();
        base.OnClosed(e);
    }

    void LoadFolder(string path)
    {
        _files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        FolderText.Text = $"{path}（{_files.Count} 首）";
        TrackList.ItemsSource = _files.Select(f => Path.GetRelativePath(path, f)).ToList();
    }

    /// <summary>整个文件夹作为播放列表交给 mpv，从选中的那首开始：换歌由 mpv 自己做，才能无缝。</summary>
    void PlayIndex(int index)
    {
        if (index < 0 || index >= _files.Count) return;
        _mpv.TryRun("stop");
        _mpv.TryRun("playlist-clear");
        foreach (var file in _files) _mpv.TryRun("loadfile", file, "append");
        _mpv.TryRun("playlist-play-index", index.ToString(CultureInfo.InvariantCulture));
        _mpv.Set("pause", "no");
    }

    void TogglePause()
    {
        if (_props.GetValueOrDefault("idle-active") == "yes") PlayIndex(Math.Max(TrackList.SelectedIndex, 0));
        else _mpv.TryRun("cycle", "pause");
    }

    string P(string name) => _props.GetValueOrDefault(name) ?? "";

    void OnProperty(string name, string? value)
    {
        _props[name] = value;
        switch (name)
        {
            case "time-pos":
            case "duration":
                var pos = double.TryParse(P("time-pos"), CultureInfo.InvariantCulture, out var p) ? p : 0;
                var dur = double.TryParse(P("duration"), CultureInfo.InvariantCulture, out var d) ? d : 0;
                _updatingPosition = true;
                PositionSlider.Maximum = Math.Max(dur, 0.001);
                PositionSlider.Value = Math.Clamp(pos, 0, PositionSlider.Maximum);
                _updatingPosition = false;
                TimeText.Text = $"{TimeSpan.FromSeconds(pos):m\\:ss} / {TimeSpan.FromSeconds(dur):m\\:ss}";
                return;
            case "playlist-pos":
                if (int.TryParse(value, out var i) && i >= 0 && i < _files.Count) TrackList.SelectedIndex = i;
                break;
            case "volume":
                VolumeText.Text = P("volume") is "100" or "100.000000" ? "100（逐位一致）" : $"{P("volume").Split('.')[0]}（软件音量，不再逐位一致）";
                break;
        }

        var playing = P("idle-active") != "yes" && P("pause") != "yes";
        PlayButton.Content = playing ? "暂停" : "播放";
        var artist = P("metadata/by-key/artist");
        NowPlaying.Text = P("idle-active") == "yes" ? "（未播放）" : string.IsNullOrEmpty(artist) ? P("media-title") : $"{P("media-title")} — {artist}";

        var decoded = $"{P("audio-codec-name")} {P("audio-params/samplerate")}Hz {P("audio-params/format")} {P("audio-params/channel-count")}ch";
        var device = $"{P("audio-out-params/samplerate")}Hz {P("audio-out-params/format")} {P("audio-out-params/channel-count")}ch";
        var resampled = P("audio-params/samplerate") != P("audio-out-params/samplerate") && P("audio-out-params/samplerate") != "";
        FormatText.Text = $"文件：{decoded}\n设备：{device} · {P("current-ao")} {(P("audio-exclusive") == "yes" ? "独占" : "共享")}"
            + (resampled ? " · 采样率不同：被重采样了（共享模式下由系统混音器做）" : "")
            + $" · gapless-audio={P("gapless-audio")}";

        if (_smtc is not null && name is "media-title" or "metadata/by-key/artist" or "metadata/by-key/album" or "pause" or "idle-active")
        {
            _smtc.Show(P("media-title"), artist, P("metadata/by-key/album"));
            _smtc.SetPlaying(playing);
        }
    }

    /// <summary>启动 4 秒后开始计时，统计本进程这段时间的 CPU 占用，写到 --out 后退出。</summary>
    void MeasureCpu(double seconds)
    {
        TimeSpan cpu0 = default;
        var sw = new Stopwatch();
        DispatcherTimer.RunOnce(() =>
        {
            using var self = Process.GetCurrentProcess();
            cpu0 = self.TotalProcessorTime;
            sw.Start();
            DispatcherTimer.RunOnce(() =>
            {
                using var p = Process.GetCurrentProcess();
                var percent = (p.TotalProcessorTime - cpu0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds * 100;
                var line = $"cpu={percent:F2}% working_set={p.WorkingSet64 / 1048576}MB state={WindowState} playing={P("idle-active") != "yes"}";
                if (ArgValue("--out") is { } path) File.WriteAllText(path, line);
                (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown();
            }, TimeSpan.FromSeconds(seconds));
        }, TimeSpan.FromSeconds(4));
    }

    void UpdateStats()
    {
        using var self = Process.GetCurrentProcess();
        var cpu = self.TotalProcessorTime;
        var elapsed = _clock.Elapsed.TotalMilliseconds;
        _clock.Restart();
        var percent = _lastCpu == TimeSpan.Zero ? 0 : (cpu - _lastCpu).TotalMilliseconds / elapsed * 100;
        _lastCpu = cpu;
        StatsText.Text = $"CPU {percent:F1}%（单核）· 内存 {self.WorkingSet64 / 1048576} MB";
    }
}
