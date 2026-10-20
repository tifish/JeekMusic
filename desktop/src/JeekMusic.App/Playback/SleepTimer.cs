using Avalonia.Threading;
using JeekMusic.Infrastructure;
using JeekMusic.Platform;

namespace JeekMusic.Playback;

/// <summary>
/// 睡眠定时（需求 P-8、O-8；规则同移动端设计 §7.6）：
/// <list type="bullet">
/// <item>按时间：到时间前 30 秒线性降低音量（mpv 的软件音量），然后暂停并恢复音量；</item>
/// <item>按时间 + 播完整首：到时间后不渐弱，转为"播完 1 首"；</item>
/// <item>按歌曲数：只有自然播完才计数，手动切歌不计；减到 0 时暂停。</item>
/// </list>
/// 停止后可以让电脑睡眠。定时中每秒更新一次剩余时间（只在定时中才有定时器）。
/// </summary>
public sealed class SleepTimer
{
    readonly PlayerService _player;
    readonly DispatcherTimer _tick;
    DateTime _end;
    bool _fading;

    public bool Active => EndTime is not null || SongsLeft > 0;
    public DateTime? EndTime { get; private set; }
    public int SongsLeft { get; private set; }
    public bool FinishSong { get; private set; }
    public bool SleepComputer { get; private set; }

    /// <summary>状态或剩余时间变了。</summary>
    public event Action? Changed;

    public SleepTimer(PlayerService player)
    {
        _player = player;
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick());
        player.TrackCompleted += OnTrackCompleted;
    }

    public TimeSpan? Remaining => EndTime is { } e ? e - DateTime.Now is var r && r > TimeSpan.Zero ? r : TimeSpan.Zero : null;

    public void StartMinutes(int minutes, bool finishSong, bool sleepComputer)
    {
        Cancel(notify: false);
        _end = DateTime.Now.AddMinutes(minutes);
        EndTime = _end;
        FinishSong = finishSong;
        SleepComputer = sleepComputer;
        _tick.Start();
        Log.Playback($"睡眠定时 {minutes} 分钟{(finishSong ? "，播完整首" : "")}");
        Changed?.Invoke();
    }

    public void StartSongs(int count, bool sleepComputer)
    {
        Cancel(notify: false);
        SongsLeft = Math.Max(1, count);
        SleepComputer = sleepComputer;
        Log.Playback($"睡眠定时：播完 {SongsLeft} 首");
        Changed?.Invoke();
    }

    public void Cancel() => Cancel(notify: true);

    void Cancel(bool notify)
    {
        _tick.Stop();
        if (_fading) _player.SetFadeVolume(100);
        _fading = false;
        EndTime = null;
        SongsLeft = 0;
        FinishSong = false;
        if (notify) Changed?.Invoke();
    }

    void Tick()
    {
        var left = _end - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            if (FinishSong)
            {
                // 到时间后不渐弱，等这首自然播完
                _tick.Stop();
                EndTime = null;
                SongsLeft = 1;
                Changed?.Invoke();
                return;
            }
            Stop();
            return;
        }
        if (!FinishSong && left <= TimeSpan.FromSeconds(30) && _player.IsPlaying)
        {
            _fading = true;
            _player.SetFadeVolume(100 * left.TotalSeconds / 30);
        }
        Changed?.Invoke();
    }

    void OnTrackCompleted()
    {
        if (SongsLeft <= 0) return;
        SongsLeft--;
        if (SongsLeft == 0) Stop();
        else Changed?.Invoke();
    }

    void Stop()
    {
        var sleep = SleepComputer;
        _player.Pause();
        Cancel(notify: true);
        Log.Playback("睡眠定时到，暂停");
        if (sleep) DispatcherTimer.RunOnce(SystemState.Sleep, TimeSpan.FromSeconds(3));
    }
}
