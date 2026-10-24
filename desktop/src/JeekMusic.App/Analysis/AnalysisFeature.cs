using Avalonia.Controls;
using Avalonia.Threading;
using JeekMusic.Core.Analysis;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.Platform;
using JeekMusic.Settings;
using JeekMusic.UI;
using static JeekMusic.UI.SettingsWindow;

namespace JeekMusic.Analysis;

/// <summary>低优先级、事件驱动的本地分析；已完成的结果立即保存，断电或全屏后暂停。</summary>
public sealed class AnalysisFeature : IFeature, IDisposable
{
    public const int AnalyzerVersion = 4;
    public static AnalysisFeature Current { get; private set; } = null!;
    AppServices _app = null!;
    readonly AutoResetEvent _wake = new(false);
    readonly CancellationTokenSource _stop = new();
    Thread? _worker;
    ForegroundEvents? _foreground;
    int _dirty = 1;
    List<Track>? _pending;
    volatile bool _manual;
    volatile bool _paused;
    public string Status { get; private set; } = L.T("等待曲库");
    public event Action? Changed;
    public event Action? ResultsChanged;

    public void Start(AppServices app, Shell shell)
    {
        Current = this; _app = app;
        app.SmartTags.ResultsChanged += OnCorrectionsChanged;
        app.Library.SnapshotChanged += Invalidate;
        app.System.PowerChanged += Wake;
        app.System.Resumed += Wake;
        app.Settings.Changed += category => { if (category is "analysis" or "rules") Invalidate(); };
        _foreground = new ForegroundEvents();
        _foreground.Changed += Wake;
        app.OnExit += Dispose;
        SettingsWindow.Register(L.T("智能标签"), 70, BuildSettings);
        _worker = new Thread(Run) { IsBackground = true, Name = "music-analysis", Priority = ThreadPriority.Lowest };
        _worker.Start(); Wake();
    }

    public void Invalidate() { Interlocked.Exchange(ref _dirty, 1); Wake(); }
    public void Wake() { if (!_stop.IsCancellationRequested) _wake.Set(); }
    bool Allowed => !_paused && !SystemState.FullscreenAppRunning &&
        (_manual || _app.Settings.Current.AnalysisMode != AnalysisMode.Manual &&
            (_app.Settings.Current.AnalysisMode == AnalysisMode.Always || SystemState.OnAcPower));
    public void RunNow() { _paused = false; _manual = true; Wake(); }
    public void Pause() { _paused = true; Wake(); }
    void SetStatus(string status) => Dispatcher.UIThread.Post(() => { Status = status; Changed?.Invoke(); });

    void Run()
    {
        SystemState.BeginBackgroundThread();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                _wake.WaitOne();
                if (_stop.IsCancellationRequested) break;
                try
                {
                    if (Interlocked.Exchange(ref _dirty, 0) != 0) { Recompute(); _pending = null; }
                    if (!Allowed) { SetStatus(L.T("已暂停（分析条件尚未满足）")); continue; }
                    if (_pending is null)
                    {
                        var rows = _app.Db.AllFeatures().ToDictionary(f => f.TrackKey);
                        _pending = _app.Library.Snapshot.Tracks.Where(t => _app.Library.PathOf(t) is not null)
                            .Where(t => !rows.TryGetValue(t.TrackKey, out var f) || f.Signature != Signature(t) || f.AnalyzerVersion != AnalyzerVersion).ToList();
                    }
                    var pending = _pending;
                    if (pending.Count == 0) { _manual = false; SetStatus(L.T("所有歌曲已分析")); continue; }
                    using var model = new AudioTagger();
                    int done = 0;
                    foreach (var t in pending)
                    {
                        if (_stop.IsCancellationRequested || !Allowed) break;
                        SetStatus(L.F($"正在分析 {++done}/{pending.Count}：{t.Title}"));
                        try
                        {
                            var pcm = PcmDecoder.Decode(_app.Library.PathOf(t)!, t, _stop.Token).GetAwaiter().GetResult();
                            var features = FeatureExtractor.Extract(PcmMath.Downsample(pcm, 16000, FeatureExtractor.SampleRate));
                            var scores = model.Analyze(pcm, _stop.Token);
                            var data = FeaturesData.Of(features, scores);
                            if (_app.Library.ByKey(t.TrackKey) is not { } current || Signature(current) != Signature(t)) continue;
                            _stop.Token.ThrowIfCancellationRequested();
                            _app.Db.SaveFeatures(new FeaturesRow(t.TrackKey, Signature(t), AnalyzerVersion, data.ToJson(), pcm.Length == 0));
                        }
                        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                        catch (Exception e)
                        {
                            Log.Warn("analysis", $"分析失败：{t.FileName}", e);
                            _app.Db.SaveFeatures(new FeaturesRow(t.TrackKey, Signature(t), AnalyzerVersion, null, true));
                        }
                        if (done % 25 == 0) Recompute();
                    }
                    if (_stop.IsCancellationRequested) break;
                    _pending = pending.Skip(done).ToList();
                    Recompute();
                    if (done == pending.Count) { _manual = false; SetStatus(L.F($"分析完成（{pending.Count} 首）")); }
                    else SetStatus(L.T("已暂停，条件满足后继续"));
                    Dispatcher.UIThread.Post(() => ResultsChanged?.Invoke());
                }
                catch (Exception e) { Log.Error("analysis", "分析任务失败", e); SetStatus(L.T("分析失败：") + e.Message); }
            }
        }
        finally { SystemState.EndBackgroundThread(); }
    }

    public static string Signature(Track t) => $"{t.Mtime}:{t.Size}";
    public void Recompute() => _app.SmartTags.Recompute();
    void OnCorrectionsChanged() => ResultsChanged?.Invoke();

    public Task Override(IReadOnlyList<Track> tracks, TagDimension dimension, Tag? tag) =>
        Override(tracks, new Dictionary<TagDimension, Tag?> { [dimension] = tag });

    public Task Override(IReadOnlyList<Track> tracks, IReadOnlyDictionary<TagDimension, Tag?> changes) =>
        _app.SmartTags.Override(tracks, changes);

    Control BuildSettings(SettingsContext c)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Note(L.T("分析完全在本地进行。接通电源且没有全屏程序时自动分析；完成后释放模型，不再轮询。")));
        panel.Children.Add(Choice([(AnalysisMode.PluggedIn, L.T("仅接通电源")), (AnalysisMode.Always, L.T("随时")), (AnalysisMode.Manual, L.T("手动"))], c.S.AnalysisMode, v => { c.S.AnalysisMode = v; c.Save("analysis"); }));
        panel.Children.Add(Labeled(L.T("运动识别灵敏度"), Choice([("LOOSE", L.T("宽松")), ("NORMAL", L.T("正常")), ("STRICT", L.T("严格"))], c.S.Sensitivity, v => { c.S.Sensitivity = v; c.Save("analysis"); })));
        var status = Note(Status); panel.Children.Add(status);
        void Refresh() => status.Text = Status;
        Changed += Refresh;
        status.DetachedFromVisualTree += (_, _) => Changed -= Refresh;
        panel.Children.Add(Row(SettingsWindow.Action(L.T("立即分析"), RunNow), SettingsWindow.Action(L.T("暂停"), Pause), SettingsWindow.Action(L.T("重新分析全部…"), async () =>
        {
            if (await Dialogs.Confirm(c.Window, L.T("重新分析"), L.T("清除自动分析结果并重新分析全部歌曲？手动修正保留。"))) { _app.Db.ClearFeatures(); Invalidate(); RunNow(); }
        })));
        return panel;
    }

    public void Dispose()
    {
        _app.SmartTags.ResultsChanged -= OnCorrectionsChanged;
        _stop.Cancel(); _wake.Set(); _foreground?.Dispose();
        _worker?.Join();
    }
}
