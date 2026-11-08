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
public sealed class AppServices : IDisposable, IAsyncDisposable
{
    public static AppServices Current { get; private set; } = null!;

    public SettingsStore Settings { get; }
    public LibraryDb Db { get; }
    public LibraryService Library { get; }
    public Analysis.SmartTagService SmartTags { get; }
    public Analysis.SingingLanguageService SingingLanguages { get; }
    public PlaylistService Playlists { get; }
    public PlayerService Player { get; }
    public QueuePersistence QueuePersistence { get; }
    public SleepTimer SleepTimer { get; }
    public LyricsService Lyrics { get; }
    internal UI.LyricsPanel? LyricsPanel { get; set; }
    internal UI.ArtistListBox? ArtistList { get; set; }
    internal UI.PlayingRowVisibility? SongVisibility { get; set; }
    internal UI.PlayingRowVisibility? QueueVisibility { get; set; }
    internal UI.FolderExpansionState FolderExpansion { get; } = new();
    internal UI.ViewModels.MainViewModel? Navigation { get; set; }
    internal UI.ExternalFileOpener? ExternalFiles { get; set; }
    internal Online.OnlineFeature? Online { get; set; }
    internal Action? CheckpointWindow { get; set; }
    internal Action? ShowSyncConflicts { get; set; }
    internal Func<System.Text.Json.Nodes.JsonObject>? MainLayoutDiagnostics { get; set; }
    internal Func<System.Text.Json.Nodes.JsonObject,System.Text.Json.Nodes.JsonObject>? SongColumnsControl { get; set; }
    internal Func<IReadOnlyList<Track>, IReadOnlyDictionary<Core.Analysis.TagDimension, Core.Analysis.Tag?>, string?, Task>? CorrectSmartTags { get; set; }
    public Automation.McpServer Mcp { get; }
    public Sync.LanSyncService LanSync { get; }
    public Preferences.PreferenceService Preferences { get; }
    public WindowMessages Messages { get; } = new();
    public Taskbar Taskbar { get; } = new();
    public Hotkeys Hotkeys { get; } = new();
    public SystemState System { get; } = new();
    public Smtc? Smtc { get; set; }
    public SingleInstance? Instance { get; init; }

    /// <summary>启动参数：--tray 表示开机启动时直接隐藏到托盘；其余是要打开的文件。</summary>
    public IReadOnlyList<string> Args { get; set; } = [];

    public AppServices() : this(null, null, null) { }

    // 窗口集成测试注入临时数据库和模拟音频设备，避免修改真实曲库或系统音量。
    internal AppServices(SettingsStore? settings, LibraryDb? db, Func<ITrackSource, PlayerService>? createPlayer, Func<AppServices, Sync.LanSyncService>? createLan = null)
    {
        Current = this;
        using (LifecycleTimings.Measure("startupWork.tagReader")) TagReader.Configure();
        using (LifecycleTimings.Measure("startupWork.settings"))
        {
            Settings = settings ?? new SettingsStore();
            L.Initialize(Settings.Current.Language);
        }
        using (LifecycleTimings.Measure("startupWork.libraryDb")) Db = db ?? new LibraryDb(AppPaths.Database);
        Library = new LibraryService(Db, Settings);
        SmartTags = new Analysis.SmartTagService(Library);
        SingingLanguages = new Analysis.SingingLanguageService(Library);
        OnExit += SingingLanguages.Dispose;
        Playlists = new PlaylistService(Library);
        using (LifecycleTimings.Measure("startupWork.player")) Player = createPlayer?.Invoke(Library) ?? new PlayerService(Library);
        var s = Settings.Current;
        using (LifecycleTimings.Measure("startupWork.outputSettings"))
        {
            OutputSettingsBinding.Attach(Player, Settings);
            if (Player.EngineReady || Player.ActualDeviceId is not null) Player.SetDevice(s.OutputDevice);
            else Player.RestoreDevice(s.OutputDevice);
        }
        QueuePersistence = new QueuePersistence(Db, Player);
        SleepTimer = new SleepTimer(Player);
        Lyrics = new LyricsService(Library);
        using (LifecycleTimings.Measure("startupWork.preferences")) Preferences = new Preferences.PreferenceService(this,db is null ? null : ":memory:");
        using (LifecycleTimings.Measure("startupWork.lan")) LanSync = createLan?.Invoke(this) ?? new Sync.LanSyncService(this);
        using (LifecycleTimings.Measure("startupWork.mcp")) Mcp = new Automation.McpServer(new Automation.McpTools(this));
        LifecycleTimings.Mark("startup.servicesReady");
    }

    Task StopServices()
    {
        Mcp.Dispose();
        // Close the listener now and overlap verified rule cleanup with local persistence.
        var lanStopped = LanSync.StopForExit();
        using (LifecycleTimings.Measure("shutdown.preferences")) Preferences.Dispose();
        if (OnExit is { } handlers)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch (Exception e) { Log.Warn("app", "停止后台服务失败", e); }
        try { QueuePersistence.SaveQueue(); } catch (Exception e) { Log.Warn("app", "保存队列失败", e); }
        using (LifecycleTimings.Measure("shutdown.player")) Player.Dispose();
        Settings.WriteNow();
        using (LifecycleTimings.Measure("shutdown.library")) Library.Dispose();
        Db.Dispose();
        return lanStopped;
    }

    void FinishShutdown()
    {
        Instance?.Dispose();
        Log.Flush();
    }

    public void Dispose()
    {
        using var total = LifecycleTimings.Measure("shutdown.total");
        var lanStopped = StopServices();
        using (LifecycleTimings.Measure("shutdown.firewallWait")) lanStopped.GetAwaiter().GetResult();
        FinishShutdown();
    }

    public async ValueTask DisposeAsync()
    {
        using var total = LifecycleTimings.Measure("shutdown.total");
        var lanStopped = StopServices();
        // Keep dispatcher/menus responsive; the single-instance mutex is released back on its UI owner.
        using (LifecycleTimings.Measure("shutdown.firewallWait")) await lanStopped;
        FinishShutdown();
    }

    public event Action? OnExit;
}
