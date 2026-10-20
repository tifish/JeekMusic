using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace JeekMusic.Spike;

public partial class MainWindow : Window
{
    readonly List<Track> _all;
    List<Track> _view;
    double _startupMs;
    double _filterMs;
    string _smtcState = "未打开";
    Smtc? _smtc;
    bool _playing;

    public MainWindow()
    {
        InitializeComponent();

        var options = Program.Options;
        _all = options.Library is { } dir ? TrackSource.FromLibrary(dir) : TrackSource.Synthetic(options.Rows);
        _view = _all;
        Grid.ItemsSource = _view;

        Search.TextChanged += (_, _) => ApplyFilter();
        ThemeBox.SelectionChanged += (_, _) => Application.Current!.RequestedThemeVariant = ThemeBox.SelectedIndex switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        FontBox.SelectionChanged += (_, _) =>
        {
            if (FontBox.SelectedIndex == 1) FontFamily = new FontFamily("Microsoft YaHei UI");
            else ClearValue(FontFamilyProperty);
        };
        MicaBox.IsCheckedChanged += (_, _) =>
        {
            if (MicaBox.IsChecked == true)
            {
                TransparencyLevelHint = [WindowTransparencyLevel.Mica];
                Background = Brushes.Transparent;
            }
            else
            {
                TransparencyLevelHint = [WindowTransparencyLevel.None];
                ClearValue(BackgroundProperty);
            }
            UpdateStatus();
        };
        PlayButton.Click += (_, _) => PlaySelected();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 两次 RequestAnimationFrame：第二次回调时第一帧已经画出来了
        RequestAnimationFrame(t1 => RequestAnimationFrame(t2 =>
        {
            _startupMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            UpdateStatus();
            _ = RunAutomatedAsync();
        }));
    }

    void ApplyFilter()
    {
        var sw = Stopwatch.StartNew();
        var query = Search.Text?.Trim().ToLowerInvariant() ?? "";
        _view = query.Length == 0 ? _all : _all.Where(t => t.SearchKey.Contains(query, StringComparison.Ordinal)).ToList();
        Grid.ItemsSource = _view;
        _filterMs = sw.Elapsed.TotalMilliseconds;
        UpdateStatus();
    }

    void PlaySelected()
    {
        var track = Grid.SelectedItem as Track ?? _view.FirstOrDefault();
        if (track is null) return;
        try
        {
            if (_smtc is null)
            {
                var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                _smtc = new Smtc(hwnd);
                _smtc.ButtonPressed += button => Dispatcher.UIThread.Post(() =>
                {
                    _playing = button switch { "Play" => true, "Pause" => false, _ => _playing };
                    _smtc?.SetPlaying(_playing);
                    _smtcState = $"收到 {button}";
                    UpdateStatus();
                });
            }
            _playing = true;
            _smtc.Show(track, playing: true);
            _smtcState = $"正在播放 {track.Title}";
        }
        catch (Exception ex)
        {
            _smtcState = "失败：" + ex.Message;
        }
        UpdateStatus();
    }

    void UpdateStatus()
    {
        using var process = Process.GetCurrentProcess();
        Status.Text = $"{_view.Count:N0} / {_all.Count:N0} 首 · 缩放 {RenderScaling:P0} · 启动 {_startupMs:F0} ms · "
            + $"内存 {process.WorkingSet64 / 1048576} MB · 筛选 {_filterMs:F1} ms · 背景 {ActualTransparencyLevel} · SMTC {_smtcState}";
    }

    async Task RunAutomatedAsync()
    {
        var options = Program.Options;
        if (!options.Automated) return;

        Report.Add("rows", _all.Count);
        Report.Add("render_scaling", RenderScaling);
        Report.Add("startup_ms", Math.Round(_startupMs));
        Report.Add("transparency", ActualTransparencyLevel);

        if (options.MeasureStartup)
        {
            await Task.Delay(2000);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using var process = Process.GetCurrentProcess();
            Report.Add("working_set_mb", process.WorkingSet64 / 1048576);
            Report.Add("private_mb", process.PrivateMemorySize64 / 1048576);
            Report.Add("managed_heap_mb", GC.GetTotalMemory(false) / 1048576);
        }

        if (options.ScrollBench) await ScrollBenchAsync();

        // DataGrid 按属性名反射排序，AOT 裁剪后是否还能用
        Grid.Columns[1].Sort(ListSortDirection.Ascending);
        await Task.Delay(300);
        var sorted = Grid.CollectionView?.Cast<Track>().Take(3).Select(t => t.Artist) ?? [];
        Report.Add("sort_artist_first3", string.Join(" | ", sorted));

        if (options.SmtcTest) await SmtcTestAsync();

        Report.Flush();
        (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown();
    }

    /// <summary>每帧往下滚 3 行，记录 UI 线程的帧间隔：间隔变长说明这一帧的布局 / 渲染没跟上刷新率。</summary>
    Task ScrollBenchAsync()
    {
        var done = new TaskCompletionSource();
        var intervals = new List<double>();
        var sw = Stopwatch.StartNew();
        var last = 0.0;
        var index = 0;

        void Tick(TimeSpan _)
        {
            var now = sw.Elapsed.TotalMilliseconds;
            if (last > 0) intervals.Add(now - last);
            last = now;
            index = Math.Min(index + 3, _view.Count - 1);
            Grid.ScrollIntoView(_view[index], null);
            if (now < 6000 && index < _view.Count - 1) RequestAnimationFrame(Tick);
            else done.SetResult();
        }

        RequestAnimationFrame(Tick);
        return done.Task.ContinueWith(_ =>
        {
            intervals.Sort();
            var median = intervals[intervals.Count / 2];
            Report.Add("scroll_frames", intervals.Count);
            Report.Add("scroll_rows", index);
            Report.Add("frame_median_ms", median.ToString("F1"));
            Report.Add("frame_p95_ms", intervals[(int)(intervals.Count * 0.95)].ToString("F1"));
            Report.Add("frame_p99_ms", intervals[(int)(intervals.Count * 0.99)].ToString("F1"));
            Report.Add("frame_max_ms", intervals[^1].ToString("F1"));
            Report.Add("frames_over_2x_median", intervals.Count(i => i > median * 2));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    async Task SmtcTestAsync()
    {
        Grid.SelectedIndex = 0;
        PlaySelected();
        Report.Add("smtc_open", _smtc is not null);
        if (_smtc is null) return;

        await Task.Delay(1000);
        var expected = _view[0].Title;
        var (found, title, appId, toggled) = await Smtc.ProbeAsync(expected);
        Report.Add("smtc_session_found", found);
        Report.Add("smtc_title", title);
        Report.Add("smtc_app_id", appId);
        Report.Add("smtc_toggle_sent", toggled);

        await Task.Delay(1000);
        Report.Add("smtc_after_toggle", _smtcState);
    }
}
