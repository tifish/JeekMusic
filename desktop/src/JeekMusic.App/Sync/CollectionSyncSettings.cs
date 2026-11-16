using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using JeekMusic.Infrastructure;
using JeekMusic.UI;
using static JeekMusic.UI.SettingsWindow;

namespace JeekMusic.Sync;

internal static class CollectionSyncSettings
{
    static string Phase(string? value)=>L.T(value switch { "idle"=>"已同步","scanning"=>"正在检查文件","reconciling"=>"正在同步文件","paused"=>"已暂停","cancelled"=>"已取消","binding"=>"正在绑定","waiting"=>"等待重试",_=>"等待同步" });
    static string Problem(string? value)=>value switch {
        null or ""=>"","unconfirmed_files"=>L.T("有文件等待确认或处理冲突"),"local_directory_identity_changed"=>L.T("目录已被替换，已停止写入。请恢复原目录或重新绑定。"),
        "local_directory_unavailable"=>L.T("本机目录不可用，稍后重试。"),"group_paused"=>L.T("同步组已暂停"),
        "cancelled"=>L.T("同步已中断，检查点已保留"),_=>L.T("文件或连接暂不可用，检查点已保留，稍后重试。") };
    static string Version(JsonNode? version)
    {
        if(version is null)return L.T("已删除");
        var pieces=version.ToString().Split(':');
        return pieces.Length==3 && long.TryParse(pieces[1],out var size) && long.TryParse(pieces[2],out var ns)?$"{size} bytes · {DateTime.UnixEpoch.AddTicks(ns/100).ToLocalTime():yyyy-MM-dd HH:mm:ss.fffffff}":L.T("文件状态未知");
    }
    internal static void Build(StackPanel panel,SettingsContext context)
    {
        var group=context.App.GroupSync;var replicas=context.App.WindowsReplicas;
        if(!group.Active)return;
        panel.Children.Add(SubHeading(L.T("曲库集合同步")));
        panel.Children.Add(Note(L.T("显式选择同一集合与本机曲库目录。绑定后先保持暂停；启动后双向同步文件，取消或解绑保留音乐。文件按修改时间、大小和最近编辑时间比较；同步前保留被替换文件的备份。")));
        var error=Note("");
        async Task Run(Func<Task> operation)
        {
            try { await operation();context.Window.Build(); }
            catch(Exception exception) when(exception is IOException or ArgumentException or InvalidOperationException)
            { error.Text=L.T("操作未完成")+": "+(exception is JeekMusic.Core.Sync.GroupSyncException sync?sync.Code:exception is LanFileDeferred deferred?deferred.Reason:exception is LanFileConflict conflict?conflict.Reason:exception.GetType().Name); }
        }
        var folders=context.App.Db.Folders().ToArray();
        var folderChoice=new ComboBox { ItemsSource=folders.Select(f=>$"{f.DisplayName} · {f.Root}").ToArray(),SelectedIndex=folders.Length>0?0:-1,MinWidth=300,MaxWidth=520 };
        AutomationProperties.SetAutomationId(folderChoice,"collection-folder");
        if(group.Coordinator)
        {
            panel.Children.Add(folderChoice);
            var name=new TextBox { PlaceholderText=L.T("集合名称"),MaxLength=200,MinWidth=300 };AutomationProperties.SetAutomationId(name,"collection-name");panel.Children.Add(name);
            var create=Action(L.T("将此曲库创建为集合"),async ()=>await Run(()=> { if(folderChoice.SelectedIndex<0)throw new ArgumentException("Select library folder");group.CreateCollection(folders[folderChoice.SelectedIndex].Id,name.Text??"");return Task.CompletedTask; }));
            AutomationProperties.SetAutomationId(create,"collection-create");panel.Children.Add(create);
            foreach(var collection in group.Collections()["collections"]!.AsArray().OfType<JsonObject>())panel.Children.Add(Note($"{collection["name"]} · {collection["collectionId"]!.ToString()[..8]}"));
        }
        else
        {
            var collections=group.Collections()["collections"]!.AsArray().OfType<JsonObject>().ToArray();
            var collectionChoice=new ComboBox { ItemsSource=collections.Select(c=>$"{c["name"]} · {c["collectionId"]!.ToString()[..8]}").ToArray(),SelectedIndex=collections.Length>0?0:-1,MinWidth=300,MaxWidth=520 };
            AutomationProperties.SetAutomationId(collectionChoice,"collection-choice");panel.Children.Add(collectionChoice);panel.Children.Add(folderChoice);
            var bind=Action(L.T("绑定本机曲库副本"),async ()=>await Run(()=> { if(folderChoice.SelectedIndex<0 || collectionChoice.SelectedIndex<0)throw new ArgumentException("Select collection and folder");replicas.Bind(collections[collectionChoice.SelectedIndex]["collectionId"]!.GetValue<string>(),folders[folderChoice.SelectedIndex].Id);return Task.CompletedTask; }));
            AutomationProperties.SetAutomationId(bind,"replica-bind");panel.Children.Add(bind);
            foreach(var mapping in replicas.Status()["mappings"]!.AsArray().OfType<JsonObject>().Where(m=>m["state"]?.ToString()!="unbound"))
            {
                var id=mapping["mappingId"]!.GetValue<string>();var status=Note("");panel.Children.Add(status);
                void Refresh()
                {
                    var state=replicas.Status(id)["mappings"]![0]!;
                    status.Text=$"{state["name"]} → {state["localRoot"]}\n{L.T("状态")}: {Phase(state["state"]?.ToString())} · {Problem(state["lastError"]?.ToString())}\n{L.T("待确认")}: {state["pendingTransactions"]} · {L.T("冲突")}: {state["conflicts"]}";
                }
                Refresh();void Changed()=>Dispatcher.UIThread.Post(Refresh);replicas.Changed+=Changed;panel.DetachedFromVisualTree+=(_,_)=>replicas.Changed-=Changed;
                var paused=mapping["paused"]?.GetValue<bool>()==true;
                var toggle=Action(paused?L.T("启动此曲库同步"):L.T("暂停此曲库同步"),async ()=>await Run(async ()=> { replicas.Control(id,paused?"resume":"pause");context.Window.Build();if(paused)await replicas.Synchronize(id); }));AutomationProperties.SetAutomationId(toggle,"replica-toggle-"+id);panel.Children.Add(toggle);
                panel.Children.Add(Action(L.T("立即同步此曲库"),async ()=>await Run(async ()=> { await replicas.Synchronize(id); })));
                panel.Children.Add(Action(L.T("取消此曲库当前同步"),async ()=>await Run(()=> { replicas.Control(id,"cancel");return Task.CompletedTask; })));
                if(paused)panel.Children.Add(Action(L.T("解除此曲库文件绑定"),async ()=>await Run(()=> { replicas.Control(id,"unbind");return Task.CompletedTask; })));
                var backups=new StackPanel { Spacing=4 };panel.Children.Add(backups);
                panel.Children.Add(Action(L.T("查看此曲库备份"),async ()=> {
                    try
                    {
                        var page=await replicas.Backups(id);backups.Children.Clear();
                        foreach(var backup in page["backups"]!.AsArray().OfType<JsonObject>())
                        {
                            backups.Children.Add(Note($"{backup["path"]} · {backup["bytes"]} bytes · {backup["state"]}"));
                            if(paused)foreach(var action in new[] { "restore","purge" })
                            {
                                var selectedAction=action;backups.Children.Add(Action(action=="restore"?L.T("恢复此文件备份"):L.T("清理此文件备份"),async ()=>await Run(async ()=> { await replicas.Backups(id,selectedAction,backup["receipt"]!.GetValue<string>()); })));
                            }
                        }
                        if(page["backups"]!.AsArray().Count==0)backups.Children.Add(Note(L.T("本页没有备份")));
                    }
                    catch(Exception exception) when(exception is IOException or ArgumentException) { error.Text=L.T("操作未完成")+": "+exception.GetType().Name; }
                }));
                foreach(var conflict in replicas.Conflicts(id)["rows"]!.AsArray().OfType<JsonObject>())
                {
                    panel.Children.Add(Note($"{L.T("文件冲突")}: {conflict["path"]}\n{L.T("本机")}: {Version(conflict["localVersion"])}\n{L.T("主 PC")}: {Version(conflict["remoteVersion"])}"));
                    foreach(var side in new[] { "local","remote" })
                    {
                        var selectedSide=side;
                        panel.Children.Add(Action(side=="local"?L.T("保留此版本：本机"):L.T("保留此版本：主 PC"),async ()=>await Run(async ()=> { replicas.Conflicts(id,conflict["id"]!.GetValue<string>(),conflict[selectedSide+"ReplicaId"]!.GetValue<string>(),conflict[selectedSide+"Version"]?.GetValue<string>());await replicas.Synchronize(id); })));
                    }
                }
            }
        }
        panel.Children.Add(error);
    }
}
