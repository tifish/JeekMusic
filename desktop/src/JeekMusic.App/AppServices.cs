using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.Lyrics;
using JeekMusic.Platform;
using JeekMusic.Playback;
using JeekMusic.Settings;

namespace JeekMusic;

/// <summary>
/// 组合根（设计 §2）：不用依赖注入容器，启动时按顺序创建各个服务。重的东西（mpv、ONNX Runtime、HttpClient）在第一次用到时才创建。
/// </summary>
public sealed class AppServices : IDisposable
{
    public static AppServices Current { get; private set; } = null!;

    public SettingsStore Settings { get; }
    public LibraryDb Db { get; }
    public LibraryService Library { get; }
    public PlaylistService Playlists { get; }
    public PlayerService Player { get; }
    public QueuePersistence QueuePersistence { get; }
    public SleepTimer SleepTimer { get; }
    public LyricsService Lyrics { get; }
    public WindowMessages Messages { get; } = new();
    public Taskbar Taskbar { get; } = new();
    public Hotkeys Hotkeys { get; } = new();
    public SystemState System { get; } = new();
    public Smtc? Smtc { get; set; }
    public SingleInstance? Instance { get; init; }

    /// <summary>启动参数：--tray 表示开机启动时直接隐藏到托盘；其余是要打开的文件。</summary>
    public IReadOnlyList<string> Args { get; set; } = [];

    public AppServices()
    {
        Current = this;
        TagReader.Configure();
        Settings = new SettingsStore();
        L.Initialize(Settings.Current.Language);
        Db = new LibraryDb(AppPaths.Database);
        Library = new LibraryService(Db, Settings);
        Playlists = new PlaylistService(Library);
        Player = new PlayerService(Library);
        var s = Settings.Current;
        Player.SetVolume(s.Volume, s.Muted);
        Player.SetExclusive(s.Exclusive);
        Player.SetDevice(s.OutputDevice);
        QueuePersistence = new QueuePersistence(Db, Player);
        SleepTimer = new SleepTimer(Player);
        Lyrics = new LyricsService(Library);
    }

    public void Dispose()
    {
        if (OnExit is { } handlers)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch (Exception e) { Log.Warn("app", "停止后台服务失败", e); }
        try { QueuePersistence.SaveQueue(); } catch (Exception e) { Log.Warn("app", "保存队列失败", e); }
        Settings.WriteNow();
        Player.Dispose();
        Library.Dispose();
        Db.Dispose();
        Instance?.Dispose();
        Log.Flush();
    }

    public event Action? OnExit;
}
