using Avalonia.Controls;
using JeekMusic.Infrastructure;
using JeekMusic.UI;
using static JeekMusic.UI.SettingsWindow;

namespace JeekMusic.Sync;

internal static class LanSyncSettings
{
    internal static void Build(StackPanel panel, SettingsContext c)
    {
        var service = c.App.LanSync;
        panel.Children.Add(SubHeading(L.T("局域网音乐同步")));
        panel.Children.Add(Note(L.T("PC 是音乐源。手机选择根或子文件夹与保存位置。仅共享下面启用的曲库根；取消同步不删除音乐。请勿让 Syncthing 同时管理手机的同一目标；无法自动识别全部 Syncthing 配置。")));
        var problem = Note(service.Problem); panel.Children.Add(problem);
        var activity = Note(""); panel.Children.Add(activity);
        void Refresh()
        {
            var status = service.Diagnostics();
            activity.Text = L.T("已传输") + " " + status["bytesServed"] + " bytes · " + status["lastOperation"] + " · " + status["devices"]!.AsArray().Count + " " + L.T("已配对设备");
            problem.Text = service.Problem;
        }
        service.Changed += Refresh; activity.DetachedFromVisualTree += (_, _) => service.Changed -= Refresh; Refresh();
        panel.Children.Add(Toggle(L.T("启用局域网服务"), service.Port != 0, enabled =>
        {
            try { if (enabled) service.Start(); else service.Stop(); c.Window.Build(); }
            catch (Exception) { problem.Text = L.T("无法监听端口。检查端口占用、网络权限和防火墙；程序不会修改系统防火墙。" ); }
        }));
        var diagnostics = service.Diagnostics();
        foreach (var folder in c.App.Library.Snapshot.Folders)
        {
            var id = folder.Id;
            var shared = diagnostics["roots"]!.AsArray().Any(r => r!["folderId"]!.GetValue<long>() == id && r["enabled"]!.GetValue<bool>());
            panel.Children.Add(Toggle(folder.DisplayName + " — " + folder.Root, shared, value =>
            {
                try { service.EnableRoot(id, value); } catch (Exception) { problem.Text = L.T("无法共享此根：路径不可用、权限不足或包含链接。" ); }
            }));
        }
        panel.Children.Add(Note(L.T("手动连接：两端连接同一局域网，将下面的一次性邀请填入手机。邀请 5 分钟有效，只能使用一次；请勿截图或公开。")));
        var host = new TextBox { Text = diagnostics["addresses"]!.AsArray().FirstOrDefault()?.GetValue<string>() ?? "", PlaceholderText = L.T("PC 局域网 IP 地址"), Width = 300 };
        panel.Children.Add(host);
        var invitation = new TextBox { IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, PlaceholderText = L.T("一次性配对邀请"), MinHeight = 65 };
        panel.Children.Add(Action(L.T("生成配对邀请"), () => { try { invitation.Text = service.Invite(host.Text?.Trim() ?? ""); } catch (Exception) { problem.Text = L.T("请输入有效的局域网地址，并启用服务。" ); } }));
        panel.Children.Add(invitation);
        foreach (var device in diagnostics["devices"]!.AsArray())
        {
            var id = device!["id"]!.GetValue<string>();
            panel.Children.Add(Action(L.T("撤销设备") + " — " + device["name"]!.GetValue<string>(), () => { service.Revoke(id); c.Window.Build(); }));
        }
        panel.Children.Add(Note(L.T("手机同步时完整核验所选内容，可检测大小和修改时间均未变化的文件。首次核验大目录可能较慢；权限或防火墙阻止连接时可重试，不会自动更改系统规则。")));
    }
}
