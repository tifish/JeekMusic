namespace JeekMusic.Playback;

/// <summary>播放服务使用的引擎接口，允许在不连接音频设备的情况下验证切换时序。</summary>
internal interface IPlaybackEngine : IDisposable
{
    event Action<string, string?>? PropertyChanged;
    event Action<long>? FileStarted;
    event Action<long>? FileLoaded;
    event Action<long, Mpv.EndReason, string?>? FileEnded;
    event Action<Mpv.EventId>? Notified;
    event Action<string, string, string>? Log;
    void Observe(string name);
    int TryRun(params string[] args);
    int TrySet(string name, string value);
    string? Get(string name);
    double? GetDouble(string name);
    long? GetLong(string name);
}
