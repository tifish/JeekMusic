namespace JeekMusic.UI;

/// <summary>
/// 首帧之后启动的功能（设计 §12）。每个功能一个类，实现 <see cref="IFeature"/>，在 <see cref="All"/> 里加一行。
/// 这样各功能的初始化（订阅事件、注册设置分区、启动后台任务）都在自己的文件里，互不干扰。
/// </summary>
public interface IFeature
{
    /// <summary>窗口显示、mpv 创建之后调用（UI 线程）。</summary>
    void Start(AppServices app, Shell shell);
}

public static class Features
{
    public static readonly List<IFeature> All =
    [
        // 每个功能一行
        new Update.UpdateFeature(),
        new Online.OnlineFeature(),
        new Analysis.AnalysisFeature(),
        new Sync.SyncFeature(),
        new Preferences.PreferenceFeature(),
    ];

    public static void StartAll(AppServices app, Shell shell)
    {
        foreach (var f in All)
        {
            try { f.Start(app, shell); }
            catch (Exception e) { Infrastructure.Log.Error("feature", $"{f.GetType().Name} 启动失败", e); }
        }
    }
}
