using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Text.Json.Nodes;

namespace JeekMusic.UI;

internal sealed class SyncConflictsWindow : Window
{
    readonly AppServices app;
    readonly StackPanel rows=new() { Spacing=12 };
    readonly TextBlock message=new() { TextWrapping=TextWrapping.Wrap };
    readonly Button previous=new() { Content=L.T("上一页") };
    readonly Button next=new() { Content=L.T("下一页") };
    int offset;bool busy;bool refreshPending;bool closed;
    async void OnSyncChanged() => await Refresh();
    public SyncConflictsWindow(AppServices app) {
        this.app=app;Title=L.T("同步冲突");Width=760;Height=620;MinWidth=480;MinHeight=400;
        var panel=new DockPanel { Margin=new Thickness(20) };
        var heading=new StackPanel { Spacing=8,Margin=new Thickness(0,0,0,12) };
        heading.Children.Add(new TextBlock { Text=Title,FontSize=22 });
        heading.Children.Add(new TextBlock { Text=L.T("选择要保留的版本；若该版本已删除，另一端也会删除。手机下次同步时会重新校验双方版本后执行。"),TextWrapping=TextWrapping.Wrap });
        heading.Children.Add(message);DockPanel.SetDock(heading,Dock.Top);panel.Children.Add(heading);
        var nav=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12,Margin=new Thickness(0,12,0,0) };
        nav.Children.Add(previous);nav.Children.Add(next);var refresh=new Button { Content=L.T("刷新") };nav.Children.Add(refresh);DockPanel.SetDock(nav,Dock.Bottom);panel.Children.Add(nav);
        panel.Children.Add(new ScrollViewer { Content=rows });Content=panel;
        previous.Click+=async (_,_)=> { offset=Math.Max(0,offset-100);await Refresh(); };
        next.Click+=async (_,_)=> { offset+=100;await Refresh(); };refresh.Click+=async (_,_)=>await Refresh();
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(5) };timer.Tick+=async (_,_)=>await Refresh();
        Opened+=async (_,_)=> { app.LanSync.Changed+=OnSyncChanged;await Refresh();timer.Start(); };
        Closed+=(_,_)=> { closed=true;app.LanSync.Changed-=OnSyncChanged;timer.Stop(); };
    }
    async Task Refresh() {
        if(closed)return;
        if(busy) { refreshPending=true;return; }busy=true;refreshPending=false;
        try {
            var page=await app.LanSync.FileConflicts(offset:offset);
            if(offset>0 && page["total"]!.GetValue<long>()<=offset) { offset=Math.Max(0,((int)page["total"]!.GetValue<long>()-1)/100*100);page=await app.LanSync.FileConflicts(offset:offset); }
            if(closed)return;
            var items=page["rows"]!.AsArray();
            rows.Children.Clear();previous.IsEnabled=offset>0;next.IsEnabled=page["hasMore"]!.GetValue<bool>();
            if(items.Count==0)rows.Children.Add(new TextBlock { Text=L.T("没有同步冲突") });
            foreach(var item in items) {
                var row=item!.AsObject();var body=new StackPanel { Spacing=8,Margin=new Thickness(12) };
                body.Children.Add(new TextBlock { Text=row["path"]!.GetValue<string>(),FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap });
                body.Children.Add(new TextBlock { Text=L.T("设备")+": "+row["deviceName"],FontSize=12,TextWrapping=TextWrapping.Wrap });
                foreach(var (field,label) in new[] { ("local",L.T("手机版本")),("remote",L.T("电脑版本")) }) {
                    var v=row[field]!.AsObject();string details;
                    if(v["exists"]?.GetValue<bool>()!=true)details=L.T("已删除／不存在");
                    else {
                        var ns=v["modifiedNs"]!.GetValue<long>();string time;
                        try { time=DateTimeOffset.FromUnixTimeMilliseconds(ns/1000000).AddTicks((ns%1000000)/100).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz"); }catch(ArgumentOutOfRangeException) { time="—"; }
                        var sizeHint=v["sizeComparison"]?.GetValue<string>() switch { "larger"=>L.T("较大"),"smaller"=>L.T("较小"),"equal"=>L.T("大小相同"),_=>null };
                        var timeHint=v["modifiedComparison"]?.GetValue<string>() switch { "newer"=>L.T("较新"),"older"=>L.T("较旧"),"equal"=>L.T("时间相同"),_=>null };
                        details=v["size"]+" "+L.T("字节")+(sizeHint is null ? "" : " （"+sizeHint+"）")+"\n"+L.T("修改时间")+": "+time+(timeHint is null ? "" : " （"+timeHint+"）");
                    }
                    body.Children.Add(new TextBlock { Text=label+"\n"+details,TextWrapping=TextWrapping.Wrap });
                }
                var choice=row["choice"]?.GetValue<string>();
                if(choice!=null)body.Children.Add(new TextBlock { Text=L.T("已选择")+": "+L.T(choice=="local"?"手机版本":"电脑版本")+" · "+L.T("等待手机同步并校验"),TextWrapping=TextWrapping.Wrap });
                var buttons=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
                foreach(var (value,label) in new[] { ("local",L.T("保留手机版本")),("remote",L.T("保留电脑版本")) }) {
                    var button=new Button { Content=label,IsEnabled=choice==null };buttons.Children.Add(button);
                    button.Click+=async (_,_)=> {
                        if(busy)return;busy=true;message.Text="";
                        try { await app.LanSync.FileConflicts("choose",row["id"]!.GetValue<string>(),value); }
                        catch(Exception) { message.Text=L.T("版本已变化或另一端已先选择，请刷新后重试。"); }
                        finally { busy=false;await Refresh(); }
                    };
                }
                body.Children.Add(buttons);rows.Children.Add(new Border { BorderThickness=new Thickness(1),BorderBrush=Brushes.Gray,CornerRadius=new CornerRadius(6),Child=body });
            }
        }catch(Exception) { message.Text=L.T("暂时无法读取冲突，请稍后刷新。"); }
        finally { busy=false;if(refreshPending && !closed)Dispatcher.UIThread.Post(async ()=>await Refresh()); }
    }
}
