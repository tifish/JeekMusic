using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;

namespace JeekMusic.UI.ViewModels;

/// <summary>
/// 播放栏、迷你播放器、歌词面板共用的播放状态（需求 W-5、PC-4）。
/// 进度每 250ms 读一次，只在播放中并且有可见的进度显示时（<see cref="ProgressVisible"/>）；窗口不可见时不刷新（设计 §13）。
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    readonly PlayerService _player;
    readonly SleepTimer _sleep;
    readonly SettingsStore _settings;
    readonly DispatcherTimer _progress;
    bool _progressVisible = true;
    bool _userSeeking;

    public PlayerViewModel(PlayerService player, SleepTimer sleep, SettingsStore settings)
    {
        _player = player;
        _sleep = sleep;
        _settings = settings;
        _progress = UiTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, () => UpdatePosition());
        player.StateChanged += OnState;
        player.TrackChanged += _ => OnTrack();
        player.Seeked += UpdatePosition;
        player.OutputChanged += () =>
        {
            var volumeChanged = SetProperty(ref _volume, player.Volume, nameof(Volume));
            var muteChanged = SetProperty(ref _muted, player.Muted, nameof(Muted));
            if (volumeChanged || muteChanged)
            {
                OnPropertyChanged(nameof(VolumeIcon));
            }
            OnPropertyChanged(nameof(FormatText));
            OnPropertyChanged(nameof(OutputModeTip));
            OnPropertyChanged(nameof(VolumeAvailable));
            OnPropertyChanged(nameof(VolumeTip));
            OnPropertyChanged(nameof(VolumeState));
        };
        sleep.Changed += () => { OnPropertyChanged(nameof(SleepActive)); OnPropertyChanged(nameof(SleepText)); };
        _volume = player.Volume;
        _muted = player.Muted;
        OnTrack();
        OnState();
    }

    public PlayerService Service => _player;

    public Track? Track => _player.CurrentTrack;
    public bool HasTrack => Track is not null;
    public string Title => Track?.Title ?? "JeekMusic";
    public string Artist => Track is { } t ? Text.Artist(t) : "";
    public string Album => Track?.Album ?? "";
    public string ArtistAlbum => Track is { } t ? (t.Album is { Length: > 0 } a ? $"{Text.Artist(t)} — {a}" : Text.Artist(t)) : "";

    [ObservableProperty] private Bitmap? _cover;

    public bool IsPlaying => _player.IsPlaying;
    public string PlayIcon => IsPlaying ? Icons.Pause : Icons.Play;
    public string PlayTip => IsPlaying ? L.T("暂停（空格）") : L.T("播放（空格）");

    public bool Shuffle => _player.Queue.Shuffle;
    public RepeatMode Repeat => _player.Queue.Repeat;
    public string RepeatIcon => Repeat == RepeatMode.One ? Icons.RepeatOne : Icons.RepeatAll;
    public bool RepeatOn => Repeat != RepeatMode.Off;
    public string RepeatTip => Repeat switch { RepeatMode.All => L.T("列表循环"), RepeatMode.One => L.T("单曲循环"), _ => L.T("不循环") };

    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";

    public bool SleepActive => _sleep.Active;
    public string SleepText => _sleep.Remaining is { } r ? Text.Duration(r) : _sleep.SongsLeft > 0 ? L.F($"{_sleep.SongsLeft} 首") : "";

    /// <summary>播放栏显示的格式，如"FLAC 24/96 → 96kHz 独占"（需求 O-3）。</summary>
    public string FormatText
    {
        get
        {
            var f = _player.Format;
            var mode = _player.Exclusive ? L.T("独占") : L.T("共享");
            if (Track is null) return mode;
            if (f.DeviceRate is null) return $"{Track.FormatLabel} {mode}";
            var device = $"{f.DeviceRate / 1000.0:0.#}kHz";
            return f.Resampled ? L.F($"{Track.FormatLabel} → {device} {mode}（已重采样）") : $"{Track.FormatLabel} → {device} {mode}";
        }
    }

    public string OutputModeTip => _player.Exclusive
        ? L.T("点击切换为共享模式，并从当前位置继续播放。")
        : L.T("点击切换为 WASAPI 独占模式，并从当前位置继续播放。");

    // ── 音量（需求 O-4）──

    private double _volume;
    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetProperty(ref _volume, Math.Clamp(value, 0, 100))) return;
            _player.SetVolume(_volume, Muted);
            OnPropertyChanged(nameof(VolumeIcon));
        }
    }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            if (!SetProperty(ref _muted, value)) return;
            _player.SetMuted(_muted);
            OnPropertyChanged(nameof(VolumeIcon));
        }
    }

    public string VolumeIcon => Muted ? Icons.Mute : Volume < 1 ? Icons.Volume0 : Icons.Volume;
    public bool VolumeAvailable => _player.VolumeAvailable;
    public string VolumeTip => _player.VolumeTip;
    public string VolumeState => _player.OutputPending && _player.OutputBlockReason is not null ? _player.OutputSafetyMessage
        : _player.EndpointVolumeValue is null ? L.T("设备音量读取失败")
        : _player.Exclusive && _player.HardwareVolume == false ? L.T("请使用硬件旋钮")
        : _player.EndpointMuted == true ? L.T("系统设备已静音")
        : _player.SessionMuted == true ? L.T("Windows 合成器已静音 JeekMusic")
        : _player.SessionVolume is < 99.999 ? L.T("Windows 合成器已调低 JeekMusic 音量") : "";

    // ── 命令 ──

    [RelayCommand] void PlayPause() => _player.PlayPause();
    [RelayCommand] void Next() => _player.Next();
    [RelayCommand] void Previous() => _player.Previous();
    [RelayCommand] void ToggleMute() => Muted = !Muted;

    [RelayCommand]
    void ToggleExclusive()
    {
        _settings.Current.Exclusive = !_player.Exclusive;
        _player.SetExclusive(_settings.Current.Exclusive);
        _settings.Save("playback");
    }

    [RelayCommand]
    void ToggleShuffle()
    {
        _player.SetShuffle(!_player.Queue.Shuffle);
        OnPropertyChanged(nameof(Shuffle));
    }

    [RelayCommand]
    void CycleRepeat()
    {
        _player.SetRepeat(Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off });
    }

    /// <summary>进度条：拖动中不随播放更新，松手时跳转（同移动端 T-7 的做法）。</summary>
    public void BeginSeek() => _userSeeking = true;

    public void EndSeek(double seconds)
    {
        _userSeeking = false;
        _player.Seek(TimeSpan.FromSeconds(seconds));
        UpdatePosition();
    }

    public void SeekTo(double seconds) => EndSeek(seconds);

    // ── 刷新 ──

    /// <summary>进度显示是否可见（主窗口和迷你播放器都不可见时为 false，停止刷新）。</summary>
    public bool ProgressVisible
    {
        get => _progressVisible;
        set
        {
            _progressVisible = value;
            UpdateTimer();
            if (value) UpdatePosition();
        }
    }

    void UpdateTimer()
    {
        var run = _progressVisible && _player.IsPlaying;
        if (run && !_progress.IsEnabled) _progress.Start();
        else if (!run && _progress.IsEnabled) _progress.Stop();
    }

    void OnState()
    {
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayIcon));
        OnPropertyChanged(nameof(PlayTip));
        OnPropertyChanged(nameof(Shuffle));
        OnPropertyChanged(nameof(Repeat));
        OnPropertyChanged(nameof(RepeatIcon));
        OnPropertyChanged(nameof(RepeatOn));
        OnPropertyChanged(nameof(RepeatTip));
        OnPropertyChanged(nameof(VolumeAvailable));
        OnPropertyChanged(nameof(VolumeTip));
        OnPropertyChanged(nameof(VolumeState));
        UpdateTimer();
        UpdatePosition();
    }

    void OnTrack()
    {
        OnPropertyChanged(nameof(Track));
        OnPropertyChanged(nameof(HasTrack));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(Album));
        OnPropertyChanged(nameof(ArtistAlbum));
        OnPropertyChanged(nameof(FormatText));
        var duration = _player.Duration.TotalSeconds;
        DurationSeconds = Math.Max(duration, 0.001);
        DurationText = Text.Duration(_player.Duration);
        UpdatePosition();
        TrackChanged?.Invoke(Track);
    }

    /// <summary>换歌（封面、歌词跟着换）。</summary>
    public event Action<Track?>? TrackChanged;

    void UpdatePosition()
    {
        if (_userSeeking) return;
        var p = _player.Position;
        PositionSeconds = Math.Min(p.TotalSeconds, DurationSeconds);
        PositionText = Text.Duration(p);
        PositionUpdated?.Invoke(p);
    }

    /// <summary>进度更新了（歌词面板跟随用）。</summary>
    public event Action<TimeSpan>? PositionUpdated;
}
