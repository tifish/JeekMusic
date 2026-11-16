using Avalonia.Controls;
using Avalonia.Threading;
using JeekMusic.Infrastructure;
using JeekMusic.UI;
using System.Text.Json.Nodes;
using static JeekMusic.UI.SettingsWindow;

namespace JeekMusic.Sync;

internal static class GroupSyncSettings
{
    internal static void Build(StackPanel panel,SettingsContext context)
    {
        var service=context.App.GroupSync;
        panel.Children.Add(SubHeading(L.T("多设备同步组")));
        panel.Children.Add(Note(L.T("选择一台常用 PC 作为主 PC。其他 PC 和手机显式加入同一个组；离线编辑会在连接后合并。加入后停用旧格式个人数据及目录同步。")));
        var status=Note("");panel.Children.Add(status);
        var error=Note("");
        var memberPanel=new StackPanel { Spacing=4 };string memberVersion="";
        void Refresh()
        {
            var state=service.Status();
            status.Text=state["joined"]?.GetValue<bool>()==true
                ? $"{L.T("同步组")}: {state["groupId"]}\n{L.T("角色")}: {state["role"]} · generation {state["generation"]} · {L.T("待上传")}: {state["pendingUploads"]}\n{L.T("状态")}: {(state["paused"]?.GetValue<bool>()==true?L.T("已暂停"):L.T("已启用"))} · {state["lastError"]}"
                : L.T("尚未加入同步组。先在主 PC 创建组，再配对并加入。 ");
            if(state["memberDetails"] is JsonArray members && members.ToJsonString()!=memberVersion)
            {
                memberVersion=members.ToJsonString();memberPanel.Children.Clear();
                foreach(var member in members.OfType<JsonObject>().Where(m=>m["actorId"]?.ToString()!=state["actorId"]?.ToString()))
                {
                    var actor=member["actorId"]!.GetValue<string>();memberPanel.Children.Add(Note(member["name"]+" · "+actor[..Math.Min(8,actor.Length)]+" · ack "+member["ackCursor"]));
                    memberPanel.Children.Add(Action(L.T("允许此成员恢复检查点"),async ()=>await Run(()=> { service.AllowRecovery(actor);return Task.CompletedTask; })));
                    if(member["active"]?.GetValue<bool>()==true)memberPanel.Children.Add(Action(L.T("移除此成员"),async ()=>await Run(()=> { service.RemoveMember(actor);return Task.CompletedTask; })));
                }
            }
        }
        async Task Run(Func<Task> operation)
        {
            try { await operation();error.Text="";Refresh();context.Window.Build(); }
            catch(Exception exception) when(exception is ArgumentException or IOException or InvalidOperationException or System.Net.Sockets.SocketException)
            { error.Text=L.T("操作未完成")+": "+(exception is JeekMusic.Core.Sync.GroupSyncException group?group.Code:exception is LanPeerException peer?peer.Code:exception.GetType().Name); }
        }
        if(!service.Active)panel.Children.Add(Action(L.T("在本机创建同步组"),async ()=>await Run(()=> { service.Create();return Task.CompletedTask; })));
        else
        {
            panel.Children.Add(Action(L.T("立即同步组数据"),async ()=>await Run(async ()=> { await service.Synchronize(); })));
            panel.Children.Add(Action(service.Status()["paused"]?.GetValue<bool>()==true?L.T("恢复组同步"):L.T("暂停组同步"),async ()=>await Run(()=> { service.Pause(service.Status()["paused"]?.GetValue<bool>()!=true);return Task.CompletedTask; })));
            if(service.Coordinator)panel.Children.Add(memberPanel);
        }
        if(service.Active && !service.Coordinator)
        {
            panel.Children.Add(Note(L.T("检查点或成员身份失效时，先在主 PC 允许本成员恢复，再执行恢复。只重新提交尚未确认的本地修改，保留原编辑时间。")));
            panel.Children.Add(Action(L.T("恢复组检查点"),async ()=>await Run(async ()=> { await service.Recover();await service.Synchronize(); })));
        }
        var invitation=new TextBox { PasswordChar='●',PlaceholderText=L.T("另一台 PC 的一次性配对邀请"),MinWidth=300 };
        panel.Children.Add(invitation);
        panel.Children.Add(Action(L.T("添加 PC 连接"),async ()=>await Run(async ()=> { await service.Pair(invitation.Text??"");invitation.Text=""; })));
        foreach(var peer in service.Peers().OfType<JsonObject>())
        {
            var peerId=peer["peerId"]!.GetValue<string>();panel.Children.Add(Note($"{peer["name"]} · {peer["host"]}:{peer["port"]}"));
            if(!service.Active)panel.Children.Add(Action(L.T("加入此 PC 的同步组"),async ()=>await Run(async ()=> { await service.Join(peerId);await service.Synchronize(); })));
        }
        panel.Children.Add(error);Refresh();
        void Updated()=>Dispatcher.UIThread.Post(Refresh);
        service.Changed+=Updated;
        panel.DetachedFromVisualTree+=(_,_)=>service.Changed-=Updated;
    }
}
