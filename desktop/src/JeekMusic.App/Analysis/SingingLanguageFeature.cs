using Avalonia.Threading;
using JeekMusic.Infrastructure;
using JeekMusic.Platform;
using JeekMusic.UI;

namespace JeekMusic.Analysis;

/// <summary>Desktop-only event-driven completion of missing vocal-language results.</summary>
public sealed class SingingLanguageFeature : IFeature,IDisposable
{
    AppServices app=null!;
    ForegroundEvents? foreground;
    readonly DispatcherTimer timer=new();
    bool disposed;
    public void Start(AppServices app,Shell shell)
    {
        this.app=app;
        timer.Tick+=Tick;
        app.Library.SnapshotChanged+=Wake;
        app.Settings.Changed+=SettingsChanged;
        app.System.PowerChanged+=Wake; app.System.Resumed+=Wake;
        app.SingingLanguages.Changed+=Wake;
        foreground=new(); foreground.Changed+=Wake;
        app.OnExit+=Dispose;
        Wake();
    }
    void SettingsChanged(string category) { if(category=="analysis") Wake(); }
    void Wake()=>Dispatcher.UIThread.Post(()=> {
        if(disposed) return;
        timer.Stop(); timer.Interval=TimeSpan.FromMilliseconds(250); timer.Start();
    },DispatcherPriority.Background);
    void Tick(object? sender,EventArgs args)
    {
        timer.Stop();
        if(disposed) return;
        try {
            var retry=app.SingingLanguages.ReconcileAutomatic(app.Settings.Current.AnalysisMode,SystemState.OnAcPower,SystemState.FullscreenAppRunning);
            if(retry is { } delay) { timer.Interval=delay>TimeSpan.Zero?delay:TimeSpan.FromMilliseconds(250);timer.Start(); }
        } catch(Exception ex) {
            Log.Warn("singing-language","自动语种识别调度失败",ex);
            timer.Interval=SingingLanguageService.AutomaticRetryDelay;timer.Start();
        }
    }
    public void Dispose()
    {
        if(disposed) return;
        disposed=true;timer.Stop();timer.Tick-=Tick;
        app.Library.SnapshotChanged-=Wake;app.Settings.Changed-=SettingsChanged;
        app.System.PowerChanged-=Wake;app.System.Resumed-=Wake;
        app.SingingLanguages.Changed-=Wake;
        foreground?.Dispose();app.OnExit-=Dispose;
    }
}
