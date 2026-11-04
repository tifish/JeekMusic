using Avalonia.Threading;

namespace JeekMusic.UI;

public partial class MainWindow
{
    void InitSyncConflicts() {
        SyncConflictsWindow? window=null;
        void Show() { if(window!=null) { window.Activate();return; }window=new(_app);window.Closed+=(_,_)=>window=null;window.Show(this); }
        _app.ShowSyncConflicts=Show;
        SyncConflictNotice.Click+=(_,_)=>Show();
        void Refresh() { SyncConflictNotice.IsVisible=_app.LanSync.ConflictCount>0;SyncConflictNotice.Content=L.T("同步冲突")+" ("+_app.LanSync.ConflictCount+") · "+L.T("点击处理"); }
        _app.LanSync.Changed+=Refresh;
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(5) };bool reading=false;
        async Task Read() { if(reading)return;reading=true;try { await _app.LanSync.FileConflicts();Refresh(); }catch(Exception) { }finally { reading=false; } }
        timer.Tick+=async (_,_)=>await Read();Opened+=async (_,_)=> { timer.Start();await Read(); };
        Closed+=(_,_)=> { timer.Stop();_app.LanSync.Changed-=Refresh;_app.ShowSyncConflicts=null;window?.Close(); };
    }
}
