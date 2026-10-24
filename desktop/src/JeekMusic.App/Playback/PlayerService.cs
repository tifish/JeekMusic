using Avalonia.Threading;
using JeekMusic.Core.Queue;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.Platform;

namespace JeekMusic.Playback;

/// <summary>播放器需要从曲库拿到的东西。</summary>
public interface ITrackSource
{
    Track? ById(long id);
    Track? ByKey(string trackKey);
    /// <summary>音频文件的完整路径；所在的盘不可用时返回 null。</summary>
    string? PathOf(Track track);
}

/// <summary>音频输出的格式（播放栏显示"文件格式 → 设备格式"，需求 O-3）。</summary>
public sealed record AudioFormatInfo(string? Codec, int? FileRate, string? FileFormat, int? DeviceRate, string? DeviceFormat, int? DeviceChannels, string? Ao)
{
    public bool Resampled => FileRate is not null && DeviceRate is not null && FileRate != DeviceRate;
}

/// <summary>
/// 播放服务（设计 §7）：<see cref="PlayQueue"/> 管顺序，mpv 的播放列表里只放"当前这首 + 下一首"，
/// 这样 mpv 能提前打开下一首做到无缝（需求 O-6），队列又可以很长、随时重新生成。
/// 所有公开方法和事件都在 UI 线程上；mpv 的事件转到 UI 线程再处理。
/// </summary>
public sealed class PlayerService : IDisposable
{
    readonly ITrackSource _tracks;
    IPlaybackEngine? _mpv;
    readonly Func<IEnumerable<(string, string)>, IPlaybackEngine> _createEngine;
    readonly IEndpointVolume? _endpointVolume;
    readonly IExclusiveAudioFormat? _exclusiveAudioFormat;
    readonly DispatcherTimer _outputMonitor;
    EndpointVolumeSnapshot? _endpoint;
    string? _boundDeviceId;
    bool _hardwareVolumeConfirmed;
    public string? ActualDeviceId => _boundDeviceId;
    public double? EndpointVolumeValue => _endpoint?.Volume;
    public bool? EndpointMuted => _endpoint?.Muted;
    public bool? HardwareVolume => _endpoint?.HardwareVolume;
    public bool? HardwareMute => _endpoint?.HardwareMute;
    public double? SessionVolume => _endpoint?.SessionVolume;
    public bool? SessionMuted => _endpoint?.SessionMuted;
    public string? VolumeDiagnostic { get; private set; }
    public string? OutputBlockReason { get; private set; }
    public bool OutputPending => _restoringOutput && CurrentTrack is not null;
    public bool HardwareVolumeConfirmationRequired => Exclusive &&
        (_endpoint?.HardwareVolume == false || _endpoint is { Muted: true, HardwareMute: false }) && !_hardwareVolumeConfirmed;

    /// <summary>mpv 播放列表里的条目：条目 ID → 队列位置对应的歌曲。</summary>
    long _currentEntry = -1, _nextEntry = -1;
    long _nextTrackId = -1;
    int _consecutiveErrors;
    bool _restoringOutput, _outputFileLoaded, _outputRestoreFailed;
    bool _wantPlaying;
    double _fadeVolume = 100;

    public PlayQueue Queue { get; } = new();

    public Track? CurrentTrack { get; private set; }

    public string? TrackKeyOf(long id) => _tracks.ById(id)?.TrackKey;
    /// <summary>当前文件与已经预加载的下一首都不能被标签写入占用。</summary>
    public bool IsFileInUse(Track t)
    {
        var path = _tracks.PathOf(t);
        if (path is null || IsIdle) return false;
        return CurrentTrack is { } current && string.Equals(path, _tracks.PathOf(current), StringComparison.OrdinalIgnoreCase)
            || _nextEntry >= 0 && _tracks.ById(_nextTrackId) is { } next && string.Equals(path, _tracks.PathOf(next), StringComparison.OrdinalIgnoreCase);
    }
    public bool IsPlaying { get; private set; }
    /// <summary>mpv 还没加载任何歌（启动时、队列为空）。</summary>
    public bool IsIdle { get; private set; } = true;
    public AudioFormatInfo Format { get; private set; } = new(null, null, null, null, null, null, null);
    public IReadOnlyList<(string name, string description)> Devices { get; private set; } = [];

    // 设置（由外部从 AppSettings 同步过来）
    public bool Exclusive { get; private set; }
    public string? Device { get; private set; }
    // Volume is a compatibility/UI number only. Unknown readings use 0 and are explicitly unavailable.
    public double Volume { get; private set; }
    public bool Muted { get; private set; }

    internal void RestoreVolumeSettings(bool exclusive, bool muted)
    {
        Exclusive = exclusive;
        Muted = muted;
    }

    static double? ValidVolume(double? value) => value is { } v && double.IsFinite(v) ? Math.Clamp(v, 0, 100) : null;

    /// <summary>播放状态变了（播放 / 暂停、换歌）。</summary>
    public event Action? StateChanged;
    /// <summary>换了一首歌（包括自动播到下一首）。</summary>
    public event Action<Track?>? TrackChanged;
    /// <summary>跳转了（用户拖动、换歌后从头开始），进度显示要立即刷新。</summary>
    public event Action? Seeked;
    /// <summary>格式、设备列表或输出模式变了。</summary>
    public event Action? OutputChanged;
    /// <summary>出错的提示（"无法播放 xxx"）。</summary>
    public event Action<string>? Error;
    /// <summary>一首歌自然播完（睡眠定时"播完 N 首"计数用，需求 P-8）。</summary>
    public event Action? TrackCompleted;

    public PlayerService(ITrackSource tracks) : this(tracks, options => new Mpv(options, "warn"), new EndpointVolume(), new ExclusiveAudioFormat()) { }

    internal PlayerService(ITrackSource tracks, Func<IEnumerable<(string, string)>, IPlaybackEngine> createEngine,
        IEndpointVolume? endpointVolume = null, IExclusiveAudioFormat? exclusiveAudioFormat = null)
    {
        _tracks = tracks;
        _createEngine = createEngine;
        _endpointVolume = endpointVolume;
        _exclusiveAudioFormat = exclusiveAudioFormat;
        Queue.Changed += OnQueueChanged;
        _outputMonitor = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => RefreshOutput());
        _outputMonitor.Start();
    }

    // ── mpv 的创建：首帧之后（设计 §12）──

    public bool EngineReady => _mpv is not null;

    public void EnsureEngine()
    {
        if (_mpv is not null) return;
        if (!ReadEndpoint(Device, bind: true)) { BlockOutputRestore("endpoint-read-failed"); return; }
        _restoringOutput = true;
        _outputFileLoaded = _outputRestoreFailed = false;
        try
        {
            _mpv = _createEngine(Mpv.BaseOptions().Concat([
                ("ao", "wasapi"), ("gapless-audio", "weak"), ("prefetch-playlist", "yes"),
                ("audio-exclusive", Exclusive ? "yes" : "no"), ("audio-device", "wasapi/" + _boundDeviceId),
                ("keep-open", "no"), ("pause", "yes"),
                ("mute", _restoringOutput || Muted ? "yes" : "no"),
                ("volume", Mpv.Num(_fadeVolume)),
            ]));
        }
        catch (Exception e) when (e is DllNotFoundException or InvalidOperationException or EntryPointNotFoundException)
        {
            Log.Error("player", "libmpv 初始化失败", e);
            Error?.Invoke(L.T("播放内核加载失败：") + e.Message);
            return;
        }
        foreach (var p in new[]
                 {
                     "pause", "idle-active", "audio-codec-name", "audio-params/samplerate", "audio-params/format",
                     "audio-out-params/samplerate", "audio-out-params/format", "audio-out-params/channel-count", "current-ao",
                     "audio-device-list",
                 })
            _mpv.Observe(p);
        var engine = _mpv;
        // 被替换的引擎可能已有事件排进 UI 队列，不能让它们恢复新引擎的播放。
        void Dispatch(Action action) => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_mpv, engine)) action(); });
        engine.PropertyChanged += (name, value) => Dispatch(() => OnProperty(name, value));
        engine.FileStarted += id => Dispatch(() => OnFileStarted(id));
        engine.FileLoaded += id => Dispatch(() =>
        {
            if (id != _currentEntry || id < 0) return;
            _outputFileLoaded = true;
            TryFinishOutputRestore();
        });
        engine.FileEnded += (id, reason, error) => Dispatch(() => OnFileEnded(id, reason, error));
        engine.Notified += e => Dispatch(() => OnEvent(e));
        _mpv.Log += (prefix, level, text) => Log.Playback($"mpv [{prefix}] {level}: {text}");
    }

    // ── 命令 ──

    /// <summary>播放一个列表（需求 G-3、P-15）。</summary>
    public void Play(QueueContext? context, IReadOnlyList<long> ids, int? startIndex, bool? shuffle = null)
    {
        Queue.Play(context, ids, startIndex, shuffle);
        LoadCurrent(play: true);
    }

    public void PlayNext(IReadOnlyList<long> ids)
    {
        var wasEmpty = Queue.Items.Count == 0;
        Queue.PlayNext(ids);
        if (wasEmpty) LoadCurrent(play: true);
    }

    public void AddToQueue(IReadOnlyList<long> ids)
    {
        var wasEmpty = Queue.Items.Count == 0;
        Queue.AddToQueue(ids);
        if (wasEmpty) LoadCurrent(play: true);
    }

    public void PlayPause()
    {
        if (_restoringOutput) { if (_wantPlaying) Pause(); else Resume(); return; }
        if (CurrentTrack is null)
        {
            if (Queue.Items.Count > 0) LoadCurrent(play: true);
            return;
        }
        EnsureEngine();
        if (IsIdle) { LoadCurrent(play: true, start: _restoringOutput ? _restorePosition : null); return; }
        if (IsPlaying) Pause(); else Resume();
    }

    public void Pause()
    {
        _wantPlaying = false;
        _mpv?.TrySet("pause", "yes");
    }

    public void Resume() => Resume(confirmHardwareVolume: true);

    void Resume(bool confirmHardwareVolume)
    {
        if (CurrentTrack is null) return;
        EnsureEngine();
        if (IsIdle) { LoadCurrent(play: true, start: _restoringOutput ? _restorePosition : null); return; }
        _wantPlaying = true;
        // Explicit Play after the hardware warning acknowledges the physical knob. Never
        // acknowledge read failures, mixer attenuation, or automatic mode/device changes.
        if (confirmHardwareVolume && OutputBlockReason == "hardware-volume-confirmation") _hardwareVolumeConfirmed = true;
        if (_restoringOutput)
        {
            _outputRestoreFailed = false;
            TryFinishOutputRestore();
            return;
        }
        if (!CheckOutputSafety()) { BlockOutputRestore(OutputBlockReason); return; }
        _mpv?.TrySet("pause", "no");
    }

    public void Next()
    {
        if (Queue.NextIndex(user: true) is not { } i) return;
        NavigateTo(i);
    }

    /// <summary>上一首并播放：超过 3 秒时回到这首的开头；切歌不代替物理音量确认。</summary>
    public void Previous()
    {
        if (Position > TimeSpan.FromSeconds(3))
        {
            var revision = SeekRevision;
            Seek(TimeSpan.Zero);
            if (SeekRevision != revision) Resume(confirmHardwareVolume: false);
            return;
        }
        if (Queue.PreviousIndex() is not { } i) return;
        NavigateTo(i);
    }

    bool _navigating;

    void NavigateTo(int index)
    {
        // MoveTo raises Changed synchronously. Load once with the navigation intent,
        // rather than first loading with the previous paused/playing state.
        _navigating = true;
        try
        {
            Queue.MoveTo(index);
            LoadCurrent(play: true);
        }
        finally { _navigating = false; }
    }

    /// <summary>在队列里点了一首。</summary>
    public void JumpTo(int queueIndex)
    {
        Queue.MoveTo(queueIndex);
        LoadCurrent(play: true);
    }

    public void SetRepeat(RepeatMode mode)
    {
        Queue.Repeat = mode;
        _mpv?.TrySet("loop-file", mode == RepeatMode.One ? "inf" : "no");
        SyncNext();
        StateChanged?.Invoke();
    }

    public void SetShuffle(bool enabled) => Queue.SetShuffle(enabled);

    TimeSpan? _pendingSeek;
    long _pendingSeekDeadline;
    // Distinguish an accepted seek from its later PlaybackRestart acknowledgement
    // (also raised for resume/buffering) without changing the existing Seeked event.
    internal long SeekRevision { get; private set; }

    /// <summary>当前这首里的位置（CUE 虚拟歌曲从这首的开头算）。</summary>
    public TimeSpan Position
    {
        get
        {
            if (_mpv is null || IsIdle || CurrentTrack is null || _restoringOutput) return _restorePosition;
            var pos = _mpv.GetDouble("time-pos") ?? 0;
            var position = TimeSpan.FromSeconds(Math.Max(0, pos - (CurrentTrack.StartMs ?? 0) / 1000.0));
            // mpv 的 seek 异步完成。连续按键从已接受的目标累加，而不是旧的 time-pos。
            if (_pendingSeek is { } pending)
            {
                if (Environment.TickCount64 >= _pendingSeekDeadline ||
                    _mpv.Get("seeking") == "no" && Math.Abs((position - pending).TotalSeconds) < 0.1)
                    _pendingSeek = null;
                else return pending;
            }
            return position;
        }
    }

    public TimeSpan Duration => TimeSpan.FromMilliseconds(CurrentTrack?.DurationMs ?? 0);

    public bool CanSeek => CurrentTrack is not null && Duration > TimeSpan.Zero && _mpv is not null &&
        !IsIdle && !_restoringOutput && _mpv.Get("seekable") == "yes";

    public void SeekBy(TimeSpan offset) => Seek(Position + offset);

    public void Seek(TimeSpan position)
    {
        if (!CanSeek) return;
        var start = (CurrentTrack!.StartMs ?? 0) / 1000.0;
        var duration = Duration.TotalSeconds;
        if (_mpv!.GetDouble("duration") is { } fileDuration && fileDuration > 0)
            duration = Math.Min(duration, Math.Max(0, fileDuration - start));
        // 留出 1s，避免直接跳到 EOF / CUE end 导致换歌；自然播放到尾仍按队列规则结束。
        position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Math.Max(0, duration - 1)));
        var target = position.TotalSeconds + start;
        if (_mpv.TryRun("seek", Mpv.Num(target), "absolute+exact") < 0) return;
        _pendingSeek = position;
        _pendingSeekDeadline = Environment.TickCount64 + 2000;
        SeekRevision++;
        Seeked?.Invoke();
    }

    // ── 输出设置（需求 O-2、O-3、O-4）──

    public void SetDevice(string? device)
    {
        device = device is "auto" ? null : device;
        if (Device == device && _boundDeviceId is not null) return;
        Device = device;
        RebuildOutput();
    }

    public void SetExclusive(bool exclusive)
    {
        if (Exclusive == exclusive) return;
        // Refuse a bypass before closing the shared stream, including mixer changes made
        // while paused or during a pending reload. The same check also runs on startup.
        if (exclusive && (!ReadEndpoint(_boundDeviceId ?? Device) || !SessionAllowsExclusive()))
        {
            OutputBlockReason = "session-attenuated-or-unreadable";
            Error?.Invoke(L.T("Windows 音量合成器中的 JeekMusic 音量低于 100%、已静音或无法读取。请先在合成器中检查并解除，再切换独占。"));
            OutputChanged?.Invoke();
            return;
        }
        Exclusive = exclusive;
        RebuildOutput();
    }

    /// <summary>绑定明确的物理端点后重建，保留队列、CUE 位置与播放意图；不向新端点写旧值。</summary>
    void RebuildOutput()
    {
        var position = Position;
        var play = _restoringOutput ? _wantPlaying : _mpv?.Get("pause") == "no";
        var oldEngine = _mpv;
        _restorePosition = position;
        _restoringOutput = oldEngine is not null && CurrentTrack is not null;
        _outputFileLoaded = _outputRestoreFailed = false;
        _hardwareVolumeConfirmed = false;
        OutputBlockReason = null;
        if (oldEngine is not null)
        {
            var muted = oldEngine.TrySet("mute", "yes") >= 0;
            var paused = oldEngine.TrySet("pause", "yes") >= 0;
            _mpv = null;
            oldEngine.Dispose();
            if (!muted || !paused)
            {
                _wantPlaying = false;
                IsPlaying = false;
                IsIdle = true;
                OutputBlockReason = "quiesce-failed";
                Error?.Invoke(L.T("无法安全切换输出模式，播放已暂停。"));
                OutputChanged?.Invoke();
                StateChanged?.Invoke();
                return;
            }
        }
        _currentEntry = _nextEntry = _nextTrackId = -1;
        IsIdle = true;
        IsPlaying = false;
        Format = new(null, null, null, null, null, null, null);
        _boundDeviceId = null;
        ReadEndpoint(Device, bind: true);
        if (oldEngine is not null && CurrentTrack is not null) LoadCurrent(play, position);
        Log.Playback($"重建输出：{(Exclusive ? "独占" : "共享")}，设备 {_boundDeviceId}，保留位置 {position.TotalSeconds:F3}s");
        OutputChanged?.Invoke();
        StateChanged?.Invoke();
    }

    void TryFinishOutputRestore()
    {
        if (!_restoringOutput || !_outputFileLoaded || _outputRestoreFailed || _mpv is null) return;
        if (string.IsNullOrEmpty(_mpv.Get("current-ao"))) return;
        if (_mpv.Get("current-ao") != "wasapi") { BlockOutputRestore("unexpected-audio-backend"); return; }
        if (!CheckOutputSafety()) { BlockOutputRestore(OutputBlockReason); return; }
        if (_mpv.TrySet("mute", Muted ? "yes" : "no") < 0 ||
            _mpv.TrySet("pause", _wantPlaying ? "no" : "yes") < 0)
        {
            BlockOutputRestore();
            return;
        }
        _restoringOutput = false;
        IsPlaying = _wantPlaying;
        Log.Playback($"输出模式切换完成，音量 {Volume:F1}%，{(_wantPlaying ? "继续播放" : "保持暂停")}");
        OutputChanged?.Invoke();
        StateChanged?.Invoke();
        Seeked?.Invoke();
    }

    void BlockOutputRestore(string? reason = null)
    {
        _restorePosition = Position;
        OutputBlockReason = reason ?? "output-restore-failed";
        Log.Playback($"输出保护：{OutputBlockReason}；设备 {_boundDeviceId}；{VolumeDiagnostic}");
        _restoringOutput = CurrentTrack is not null;
        if (_mpv is { } engine)
        {
            var muted = engine.TrySet("mute", "yes") >= 0;
            var paused = engine.TrySet("pause", "yes") >= 0;
            if (!muted || !paused)
            {
                _mpv = null;
                engine.Dispose();
                IsIdle = true;
            }
        }
        _wantPlaying = false;
        IsPlaying = false;
        _outputRestoreFailed = true;
        Error?.Invoke(OutputSafetyMessage);
        OutputChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public string OutputSafetyMessage => OutputBlockReason switch
    {
        "hardware-volume-confirmation" => L.T("独占模式无法使用设备的软件音量或静音。请先用硬件旋钮确认安全音量，再按播放继续。"),
        "session-attenuated-or-unreadable" => L.T("Windows 音量合成器中的 JeekMusic 音量低于 100%、已静音或无法读取。请先在合成器中检查并解除，再切换独占。"),
        _ => L.T("无法确认输出设备或音量，已保持暂停和本播放器静音。请检查设备后按播放重试。")
    };

    public string VolumeTip => VolumeDiagnostic is not null && _endpoint?.Volume is null
        ? L.T("无法读取当前设备音量，请检查设备连接。")
        : Exclusive && HardwareVolume == false
            ? L.T("独占模式音量不可调，请使用设备硬件旋钮；显示的端点软件音量不影响独占输出。")
            : L.T("当前输出设备的主音量；共享模式下也影响同设备的其他程序。");

    public bool VolumeAvailable => _endpoint?.Volume is not null && (!Exclusive || HardwareVolume == true) &&
        !OutputPending && _boundDeviceId is not null;

    /// <summary>相对调节先读取设备实时值；不可调时无操作，始终保留本播放器和系统静音。</summary>
    public void AdjustVolume(double percentagePoints)
    {
        RefreshOutput();
        if (!VolumeAvailable || !double.IsFinite(percentagePoints)) return;
        SetVolume(Math.Clamp(Volume + percentagePoints, 0, 100), Muted);
    }

    /// <summary>显式音量操作写当前绑定端点；muted 始终只控制 mpv 软件静音。</summary>
    public bool SetVolume(double volume, bool muted)
    {
        SetMuted(muted);
        if (ValidVolume(volume) is not { } valid) return false;
        RefreshOutput();
        if (!VolumeAvailable || _boundDeviceId is null || _endpointVolume is null)
        {
            Error?.Invoke(VolumeTip);
            OutputChanged?.Invoke();
            return false;
        }
        var ok = _endpointVolume.SetVolume(_boundDeviceId, valid);
        var error = _endpointVolume.LastError;
        var readable = ReadEndpoint(_boundDeviceId);
        if (!ok || !readable)
        {
            VolumeDiagnostic = error ?? VolumeDiagnostic ?? "endpoint write/readback failed";
            BlockOutputRestore("endpoint-write-failed");
        }
        OutputChanged?.Invoke();
        return ok && readable;
    }

    public void SetMuted(bool muted)
    {
        Muted = muted;
        if (_mpv is not null && _mpv.TrySet("mute", _restoringOutput || muted ? "yes" : "no") < 0)
            BlockOutputRestore("app-mute-failed");
        OutputChanged?.Invoke();
    }

    bool ReadEndpoint(string? device, bool bind = false)
    {
        _endpoint = _endpointVolume?.Capture(device);
        if (bind) _boundDeviceId = _endpoint?.DeviceId;
        Volume = _endpoint?.Volume ?? 0;
        VolumeDiagnostic = _endpoint?.Error ?? _endpointVolume?.LastError;
        if (_endpoint?.Volume is null || _endpoint.Muted is null)
        {
            VolumeDiagnostic ??= "endpoint volume/mute unavailable";
            return false;
        }
        return true;
    }

    bool SessionAllowsExclusive() => _endpoint is { SessionVolume: >= 99.999, SessionMuted: false };

    bool CheckOutputSafety()
    {
        if (!ReadEndpoint(_boundDeviceId ?? Device)) { OutputBlockReason = "endpoint-read-failed"; return false; }
        if (Exclusive)
        {
            if (!SessionAllowsExclusive()) { OutputBlockReason = "session-attenuated-or-unreadable"; return false; }
            if (HardwareVolume is null || HardwareMute is null) { OutputBlockReason = "hardware-support-unreadable"; return false; }
            if (HardwareVolumeConfirmationRequired) { OutputBlockReason = "hardware-volume-confirmation"; return false; }
        }
        OutputBlockReason = null;
        return true;
    }

    /// <summary>250ms 只读同步（含托盘状态）；从不因通知写回。auto 的实际输出固定为解析后的端点。</summary>
    internal void RefreshOutput()
    {
        var previous = _endpoint;
        var diagnostic = VolumeDiagnostic;
        if (Device is null)
        {
            var desired = _endpointVolume?.Capture(null);
            if (desired is not null && desired.DeviceId != _boundDeviceId)
            {
                RebuildOutput();
                return;
            }
            if (desired is null)
            {
                _endpoint = null;
                Volume = 0;
                VolumeDiagnostic = _endpointVolume?.LastError ?? "default endpoint unavailable";
                if (!_outputRestoreFailed && _mpv is not null) BlockOutputRestore("endpoint-read-failed");
                if (previous != _endpoint || diagnostic != VolumeDiagnostic) OutputChanged?.Invoke();
                return;
            }
        }
        var readable = ReadEndpoint(_boundDeviceId ?? Device, bind: _boundDeviceId is null);
        if (_mpv is not null && !_restoringOutput && (!readable || !CheckOutputSafety()))
            BlockOutputRestore(OutputBlockReason ?? "endpoint-read-failed");
        if (previous != _endpoint || diagnostic != VolumeDiagnostic) OutputChanged?.Invoke();
    }

    /// <summary>睡眠定时渐弱用的软件音量（唯一允许改动样本的情况，需求 §4.1）。</summary>
    public void SetFadeVolume(double percent)
    {
        _fadeVolume = Math.Clamp(percent, 0, 100);
        _mpv?.TrySet("volume", Mpv.Num(_fadeVolume));
    }

    // ── 断点续播 ──

    TimeSpan _restorePosition;

    /// <summary>恢复保存的队列（启动时，mpv 还没创建）。</summary>
    public void Restore(IReadOnlyList<QueueEntry> items, int index, bool shuffle, RepeatMode repeat, QueueContext? context, TimeSpan position)
    {
        Queue.Restore(items, index, shuffle, repeat, context);
        _restorePosition = position;
        CurrentTrack = Queue.Current is { } c ? _tracks.ById(c.TrackId) : null;
        TrackChanged?.Invoke(CurrentTrack);
        StateChanged?.Invoke();
    }

    /// <summary>mpv 创建好之后，以暂停状态加载上次的歌并跳到上次的位置，按播放就接着放（设计 §7.6）。</summary>
    public void PrepareRestored()
    {
        if (CurrentTrack is null || !IsIdle) return;
        LoadCurrent(play: false, start: _restorePosition);
    }

    // ── mpv 播放列表：当前这首 + 下一首 ──

    void LoadCurrent(bool play, TimeSpan? start = null)
    {
        _pendingSeek = null;
        _wantPlaying = play;
        var entry = Queue.Current;
        var track = entry is { } e ? _tracks.ById(e.TrackId) : null;
        if (track is null)
        {
            _restoringOutput = _outputRestoreFailed = false;
            CurrentTrack = null;
            _mpv?.TryRun("stop");
            IsIdle = true;
            IsPlaying = false;
            TrackChanged?.Invoke(null);
            StateChanged?.Invoke();
            return;
        }
        EnsureEngine();
        if (_mpv is null) return;
        var path = _tracks.PathOf(track);
        if (path is null || !File.Exists(path))
        {
            SkipBroken(track, L.T("文件不存在或所在的盘不可用"));
            return;
        }
        CurrentTrack = track;
        _restorePosition = start ?? TimeSpan.Zero;
        _restoringOutput = true;
        _outputFileLoaded = _outputRestoreFailed = false;
        var muted = _mpv.TrySet("mute", "yes") >= 0;
        var paused = _mpv.TrySet("pause", "yes") >= 0;
        if (!muted || !paused) { BlockOutputRestore(); return; }
        _mpv.TrySet("loop-file", Queue.Repeat == RepeatMode.One ? "inf" : "no");
        // A rejected replacement must not accept late FileLoaded for the old song.
        _currentEntry = _nextEntry = _nextTrackId = -1;
        _mpv.TryRun("playlist-clear");
        if (_mpv.TryRun("loadfile", path, "replace", "-1", FileOptions(track, start)) < 0)
        {
            BlockOutputRestore();
            return;
        }
        _currentEntry = LastEntryId();
        IsIdle = false;
        if (_restoringOutput)
        {
            var engine = _mpv;
            var entryId = _currentEntry;
            DispatcherTimer.RunOnce(() =>
            {
                if (ReferenceEquals(_mpv, engine) && _currentEntry == entryId && _restoringOutput && !_outputRestoreFailed)
                    BlockOutputRestore();
            }, TimeSpan.FromSeconds(5));
        }
        Log.Playback($"播放 {track.RelativePath}{(track.IsCue ? $" #{track.CueTrackNo}" : "")} 从 {(start ?? TimeSpan.Zero).TotalSeconds:F1}s{(play ? "" : "（暂停）")}");
        SyncNext();
        TrackChanged?.Invoke(track);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// loadfile 的选项：CUE 虚拟歌曲用 start / end 截取。<b>end 比分轨点早 1 微秒</b>，
    /// 否则分轨点上的那个样本会在前后两首里各播一次（验证结论，设计 §7.3）。
    /// </summary>
    string FileOptions(Track track, TimeSpan? start)
    {
        var opts = new List<string>();
        var begin = (track.StartMs ?? 0) / 1000m + (decimal)(start ?? TimeSpan.Zero).TotalSeconds;
        if (begin > 0 || track.IsCue) opts.Add("start=" + begin.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (track.EndMs is { } end) opts.Add("end=" + (end / 1000m - 0.000001m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Exclusive && track.SampleRate is > 0 && track.Channels is > 0 &&
            _exclusiveAudioFormat?.Select(Device, track.SampleRate.Value, track.Channels.Value, track.BitsPerSample) is { } format)
        {
            // 文件级选项同样用于预加载的下一首，切回共享或切换歌曲时不会遗留固定的位深 / 采样率。
            opts.Add("audio-format=" + format);
            opts.Add("audio-samplerate=" + track.SampleRate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Log.Playback($"独占输出优先源采样率 {track.SampleRate}Hz，样本格式 {format}，文件 {track.RelativePath}");
        }
        return string.Join(',', opts);
    }

    long LastEntryId()
    {
        var count = _mpv!.GetLong("playlist-count") ?? 0;
        return count == 0 ? -1 : _mpv.GetLong($"playlist/{count - 1}/id") ?? -1;
    }

    /// <summary>让 mpv 播放列表里的"下一首"和队列一致（队列变了、循环模式变了时调用）。</summary>
    void SyncNext()
    {
        if (_mpv is null || IsIdle || CurrentTrack is null) return;
        var nextIndex = Queue.NextIndex(user: false);
        // 单曲循环由 mpv 的 loop-file 处理，不放下一首
        var next = nextIndex is { } i && Queue.Repeat != RepeatMode.One && i != Queue.CurrentIndex ? _tracks.ById(Queue.Items[i].TrackId) : null;
        // 列表循环且只剩自己时 NextIndex 是自己：也交给 loop
        if (next?.Id == _nextTrackId && _nextEntry >= 0) return;
        if (_nextEntry >= 0)
        {
            var index = IndexOfEntry(_nextEntry);
            if (index >= 0) _mpv.TryRun("playlist-remove", index.ToString());
            _nextEntry = -1;
            _nextTrackId = -1;
        }
        if (next is null) return;
        var path = _tracks.PathOf(next);
        if (path is null || !File.Exists(path)) return; // 换到它时再处理
        _mpv.TryRun("loadfile", path, "append", "-1", FileOptions(next, null));
        _nextEntry = LastEntryId();
        _nextTrackId = next.Id;
    }

    int IndexOfEntry(long entryId)
    {
        var count = _mpv!.GetLong("playlist-count") ?? 0;
        for (var i = 0; i < count; i++)
            if (_mpv.GetLong($"playlist/{i}/id") == entryId) return i;
        return -1;
    }

    void OnQueueChanged()
    {
        // 自动换歌时由 OnFileStarted 自己处理
        if (_inFileStarted || _navigating) return;
        // 当前这首被移除了（队列界面里删掉、或曲库里不存在了）：换到新的当前歌曲
        var current = Queue.Current is { } c ? c.TrackId : -1;
        if (CurrentTrack is not null && current != CurrentTrack.Id)
        {
            if (current < 0) { LoadCurrent(play: false); return; }
            LoadCurrent(play: IsPlaying || _wantPlaying);
            return;
        }
        SyncNext();
        StateChanged?.Invoke();
    }

    bool _inFileStarted;

    void OnFileStarted(long entryId)
    {
        if (entryId != _nextEntry || entryId < 0) return;
        _pendingSeek = null;
        // mpv 自动播到了下一首：队列前进一首，删掉播完的那条，再放新的下一首
        _inFileStarted = true;
        try
        {
            var previous = _currentEntry;
            var startedTrack = _nextTrackId;
            _currentEntry = _nextEntry;
            _nextEntry = -1;
            _nextTrackId = -1;
            if (Queue.NextIndex(user: false) is { } i) Queue.MoveTo(i);
            CurrentTrack = _tracks.ById(startedTrack);
            var oldIndex = IndexOfEntry(previous);
            if (oldIndex >= 0) _mpv!.TryRun("playlist-remove", oldIndex.ToString());
            _consecutiveErrors = 0;
            Log.Playback($"自动换到 {CurrentTrack?.RelativePath}");
        }
        finally { _inFileStarted = false; }
        SyncNext();
        TrackChanged?.Invoke(CurrentTrack);
        StateChanged?.Invoke();
        TrackCompleted?.Invoke();
    }

    void OnFileEnded(long entryId, Mpv.EndReason reason, string? error)
    {
        if (reason == Mpv.EndReason.Error)
        {
            Log.Playback($"出错：条目 {entryId} {error}");
            if (entryId == _currentEntry && CurrentTrack is { } t) SkipBroken(t, error ?? L.T("无法解码"));
            else if (entryId == _nextEntry) { _nextEntry = -1; _nextTrackId = -1; }
            return;
        }
        if (reason == Mpv.EndReason.Eof && entryId == _currentEntry && _nextEntry < 0 && Queue.Repeat != RepeatMode.One)
        {
            // 播完了最后一首（不循环）：停在这首的开头，暂停（按播放从头再放）
            Log.Playback("队列播完");
            TrackCompleted?.Invoke();
            _wantPlaying = false;
            if (Queue.NextIndex(user: false) is { } next) { Queue.MoveTo(next); LoadCurrent(play: true); }
            else LoadCurrent(play: false);
        }
    }

    /// <summary>某首无法播放：提示并跳到下一首；连续 3 首都失败时停止（需求 O-7）。</summary>
    void SkipBroken(Track track, string reason)
    {
        _consecutiveErrors++;
        Error?.Invoke(L.F($"无法播放「{track.Title}」：{reason}"));
        Log.Playback($"无法播放 {track.RelativePath}：{reason}（连续第 {_consecutiveErrors} 首）");
        if (_consecutiveErrors >= 3 || Queue.NextIndex(user: false) is not { } next || next == Queue.CurrentIndex)
        {
            _consecutiveErrors = 0;
            _wantPlaying = false;
            CurrentTrack = track;
            _mpv?.TryRun("stop");
            IsIdle = true;
            IsPlaying = false;
            TrackChanged?.Invoke(track);
            StateChanged?.Invoke();
            return;
        }
        Queue.MoveTo(next);
        LoadCurrent(play: _wantPlaying || IsPlaying);
    }

    void OnProperty(string name, string? value)
    {
        switch (name)
        {
            case "pause":
                IsPlaying = value == "no" && !IsIdle && !_restoringOutput;
                StateChanged?.Invoke();
                break;
            case "idle-active":
                if (value == "yes" && CurrentTrack is null) { IsIdle = true; IsPlaying = false; StateChanged?.Invoke(); }
                break;
            case "audio-device-list":
                Devices = ParseDevices();
                OutputChanged?.Invoke();
                // 指定的设备不见了（拔掉了）：暂停并提示（需求 O-2）
                if (Device is not null && Devices.Count > 0 && Devices.All(d => d.name != Device) && IsPlaying)
                {
                    Pause();
                    Error?.Invoke(L.T("指定的输出设备已断开，播放已暂停"));
                }
                break;
            default:
                Format = new AudioFormatInfo(
                    _mpv?.Get("audio-codec-name"),
                    (int?)_mpv?.GetLong("audio-params/samplerate"), _mpv?.Get("audio-params/format"),
                    (int?)_mpv?.GetLong("audio-out-params/samplerate"), _mpv?.Get("audio-out-params/format"),
                    (int?)_mpv?.GetLong("audio-out-params/channel-count"), _mpv?.Get("current-ao"));
                OutputChanged?.Invoke();
                break;
        }
    }

    void OnEvent(Mpv.EventId e)
    {
        switch (e)
        {
            case Mpv.EventId.AudioReconfig:
                if (_restoringOutput) TryFinishOutputRestore();
                else RefreshOutput();
                break;
            case Mpv.EventId.PlaybackRestart:
                Seeked?.Invoke();
                break;
        }
    }

    List<(string, string)> ParseDevices()
    {
        var list = new List<(string, string)>();
        var count = _mpv?.GetLong("audio-device-list/count") ?? 0;
        for (var i = 0; i < count; i++)
        {
            var name = _mpv!.Get($"audio-device-list/{i}/name");
            if (name is null || !name.StartsWith("wasapi/", StringComparison.Ordinal) && name != "auto") continue;
            list.Add((name, _mpv.Get($"audio-device-list/{i}/description") ?? name));
        }
        return list;
    }

    public void Dispose()
    {
        _outputMonitor.Stop();
        _mpv?.Dispose();
        _mpv = null;
        OutputChanged?.Invoke();
    }
}
