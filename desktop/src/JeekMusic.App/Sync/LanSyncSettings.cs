using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using JeekMusic.Infrastructure;
using JeekMusic.UI;
using System.Text.Json.Nodes;
using static JeekMusic.UI.SettingsWindow;

namespace JeekMusic.Sync;

internal static class LanSyncSettings
{
    internal static void Build(StackPanel panel, SettingsContext c)
    {
        var service = c.App.LanSync;
        panel.Children.Add(SubHeading(L.T("局域网音乐同步")));
        panel.Children.Add(Action(L.T("同步冲突"),()=>new SyncConflictsWindow(c.App).Show(c.Window)));
        panel.Children.Add(Note(L.T("手机选择音乐文件夹及直接保存目录。按双方确认的内容基线双向同步；单边修改/删除自动传播，真实冲突才需要选择。配对即可使用全部同步能力。开启服务自动共享全部曲库；取消同步不删除音乐。")));
        var problem = Note(service.Problem); panel.Children.Add(problem);
        var activity = Note(""); panel.Children.Add(activity);
        var firewall = Note(""); panel.Children.Add(firewall);
        var sharing = Note("");
        var guidance = Note("");
        var folders = new StackPanel { Spacing = 4 };
        var addFolders = Action(L.T("前往曲库添加音乐文件夹"), () => c.Window.Select(L.T("曲库")));
        CheckBox? enabledSwitch = null;
        Button? inviteButton = null;
        bool refreshing = false;
        void Refresh()
        {
            var status = service.Diagnostics();
            activity.Text = L.T("已传输") + " " + status["bytesServed"] + " bytes · " + status["lastOperation"] + " · " + status["devices"]!.AsArray().Count + " " + L.T("已配对设备");
            problem.Text = service.Problem;
            var fw = status["firewall"]!;
            firewall.Text = L.T("Windows 防火墙") + ": " + L.T(fw["state"]?.GetValue<string>() switch {
                "configured" => "已配置当前程序的精确 TCP 入站规则；外网可达性尚未验证。",
                "not_checked" or "not_required" => "服务关闭或仅回环监听，无需入站放行。",
                "pending" or "configuring" => "正在配置；Windows 可能请求管理员确认。",
                "uac_cancelled" => "管理员确认已取消，点击修复后重试。",
                "policy_blocked" => "组织策略禁止本地入站规则，请联系管理员。",
                _ => "防火墙未就绪。点击修复；若仍失败，请检查管理员权限、系统策略或第三方防火墙。"
            });
            sharing.Text = L.T("已共享音乐文件夹") + ": " + status["sharedFolderCount"]!.GetValue<int>();
            guidance.Text = service.Port == 0 ? L.T("局域网服务已关闭。开启后会自动共享所有已添加的音乐文件夹。")
                : status["libraryFolderCount"]!.GetValue<int>() == 0 ? L.T("尚未添加音乐文件夹。请前往曲库添加，手机再点击“刷新共享文件夹”，无需重新扫码。")
                : L.T("已添加的音乐文件夹会自动共享，新增文件夹也会自动纳入。手机点击“刷新共享文件夹”即可看到更改，无需重新配对。");
            folders.Children.Clear();
            foreach (var folder in c.App.Db.Folders())
                folders.Children.Add(Note(folder.DisplayName + " — " + folder.Root + (folder.Available && Directory.Exists(folder.Root) ? "" : " · " + L.T("离线"))));
            addFolders.IsVisible = status["libraryFolderCount"]!.GetValue<int>() == 0;
            if (inviteButton is not null) inviteButton.IsEnabled = service.Port != 0;
            refreshing = true;
            try { if (enabledSwitch is not null) enabledSwitch.IsChecked = service.Port != 0 || service.Starting; }
            finally { refreshing = false; }
        }
        service.Changed += Refresh;
        c.App.Library.SnapshotChanged += Refresh;
        panel.DetachedFromVisualTree += (_, _) => { service.Changed -= Refresh; c.App.Library.SnapshotChanged -= Refresh; };
        enabledSwitch = Toggle(L.T("启用局域网服务"), service.Port != 0 || service.Starting, async enabled =>
        {
            if (refreshing) return;
            try { if (enabled) await service.StartAsync(); else service.Stop(); c.Window.Build(); }
            catch (Exception) { Refresh(); problem.Text = L.T("无法监听端口。请检查端口占用及网络权限；防火墙结果单独显示。"); }
        });
        panel.Children.Add(enabledSwitch);
        Refresh();
        var diagnostics = service.Diagnostics();
        panel.Children.Add(Note(L.T("连接采用 TLS TCP 透传。自动维护当前程序路径、实际 TCP 端口及绑定地址的 Windows 入站规则（所有网络配置文件、任意来源）；回环监听无需放行。不会修改路由、公网映射或 UPnP。")));
        panel.Children.Add(Note(L.T("安全预算") + $": TLS/auth 5s · 24 connections / 8 unauthenticated / 16 authenticated · {diagnostics["authenticationFailures"]} auth failures · {diagnostics["connectionRejected"]} rejected · {diagnostics["rateLimited"]} limited"));
        panel.Children.Add(Action(L.T("修复 Windows 防火墙"), async () => { await service.RepairFirewall(); Refresh(); }));
        var bind = new TextBox { Text = diagnostics["bindAddress"]!.GetValue<string>(), PlaceholderText = "IPv4 bind", Width = 300 };
        var listenPort = new TextBox { Text = diagnostics["configuredPort"]!.ToString(), PlaceholderText = "TCP port", Width = 300 };
        panel.Children.Add(Note(L.T("高级监听配置（先停服务；IPv4，新安装默认仅回环 127.0.0.1:39843）")));
        panel.Children.Add(bind); panel.Children.Add(listenPort);
        panel.Children.Add(Note(L.T("绑定 127.0.0.1 时只允许本机。手机访问需先停服并绑定指定 IPv4 网卡或 0.0.0.0；现有安装保持原绑定范围。")));
        panel.Children.Add(Action(L.T("保存监听配置"), () => {
            try { if (!int.TryParse(listenPort.Text, out var port)) throw new ArgumentException(); service.Configure(bind.Text?.Trim() ?? "", port); c.Window.Build(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { problem.Text = L.T("先停止服务，再填写有效 IPv4 和 1–65535 端口。"); }
        }));
        panel.Children.Add(Note(L.T("文件恢复备份最多 32 GiB；达到预算时延期，不自动删除。恢复/清除前先停服务；恢复不改同步基线。")));
        var backupPanel=new StackPanel { Spacing=4 };panel.Children.Add(backupPanel);int backupOffset=0;
        var backupStatus=Note("");panel.Children.Add(backupStatus);
        panel.Children.Add(Action(L.T("查看 / 下一页文件恢复备份"),async ()=> {
            try {
                var result=await Task.Run(()=>service.FileBackups("list",offset:backupOffset));backupPanel.Children.Clear();
                foreach(var row in result["backups"]!.AsArray().OfType<JsonObject>()) {
                    backupPanel.Children.Add(Note(row["path"]+" · "+row["bytes"]+" bytes · "+row["state"]));
                    foreach(var (action,title) in new[] { ("restore","恢复原文件"),("purge","删除此备份") }) {
                        var receipt=row["receipt"]!.GetValue<string>();
                        backupPanel.Children.Add(Action(L.T(title),async ()=> {
                            if(!await Dialogs.Confirm(c.Window,L.T(title),L.T("只处理此已核验回执；其他文件保留。当前文件有后续修改时拒绝恢复。"),danger:action=="purge"))return;
                            try { await Task.Run(()=>service.FileBackups(action,receipt));backupStatus.Text=L.T("已完成；刷新列表查看。"); }
                            catch(Exception e) when(e is IOException or InvalidOperationException or ArgumentException) { backupStatus.Text=L.T("未处理：请停服务，并确认文件未改变且备份已确认。")+" "+e.GetType().Name; }
                        }));
                    }
                }
                backupOffset=result["hasMore"]!.GetValue<bool>()?result["next"]!.GetValue<int>():0;
                backupStatus.Text=L.T("恢复备份保留字节")+": "+result["retainedBytes"];
            }catch(Exception e) when(e is IOException or InvalidOperationException or ArgumentException) { backupStatus.Text=L.T("文件任务忙或存储不可用，请稍后重试。"); }
        }));
        panel.Children.Add(Note(L.T("配对设备可访问并双向同步全部已登记曲库及管理学习。配对即拥有全部同步权限，旧设备无需重新配对；可随时撤销设备。凭证长期有效。")));
        panel.Children.Add(SubHeading(L.T("共享音乐文件夹")));
        panel.Children.Add(sharing); panel.Children.Add(guidance); panel.Children.Add(folders); panel.Children.Add(addFolders);
        panel.Children.Add(Note(L.T("手机扫码配对，或手动输入邀请。使用手机可达的 IPv4 或域名，可填写公网映射端口。邀请 5 分钟有效且只能使用一次；请勿截图或公开。")));
        var addresses = diagnostics["addresses"]!.AsArray().Select(n => n!.GetValue<string>()).Distinct().ToArray();
        var host = new TextBox { Text = addresses.FirstOrDefault() ?? "", PlaceholderText = L.T("PC 局域网 IP 地址"), Width = 300 };
        if (addresses.Length > 0)
        {
            panel.Children.Add(Note(L.T("选择手机可达的 PC 地址（多网卡请核对），也可在下方手动输入。")));
            var choices = new ComboBox { ItemsSource = addresses, SelectedIndex = 0, MinWidth = 300 };
            choices.SelectionChanged += (_, _) => host.Text = choices.SelectedItem as string;
            panel.Children.Add(choices);
        }
        panel.Children.Add(host);
        var externalPort = new TextBox { Text = service.Port.ToString(), PlaceholderText = L.T("手机可达的 TCP 端口"), Width = 300 };
        panel.Children.Add(externalPort);
        var invitation = new TextBox { IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, PlaceholderText = L.T("一次性配对邀请"), MinHeight = 65 };
        var qr = new Image { Width = 300, Height = 300, IsVisible = false };
        var lifetime = Note("");
        bool detached = false, generating = false;
        string? current = null;
        Bitmap? bitmap = null;
        void ClearInvitation()
        {
            current = null; invitation.Text = ""; qr.Source = null; qr.IsVisible = false;
            bitmap?.Dispose(); bitmap = null;
        }
        void CheckInvitation()
        {
            if (current is not null && !service.IsInvitationCurrent(current))
            { ClearInvitation(); lifetime.Text = L.T("邀请已使用、过期或被替换。需要时重新生成。"); }
        }
        var generate = Action(L.T("生成 / 刷新配对二维码"), async () =>
        {
            if (generating) return;
            generating = true;
            ClearInvitation(); lifetime.Text = "";
            try
            {
                var address = host.Text?.Trim() ?? "";
                await LanPairingQr.ValidatePhoneHost(address);
                if (detached) return;
                if (!int.TryParse(externalPort.Text, out var reachablePort)) throw new ArgumentException();
                current = service.Invite(address, reachablePort);
                bitmap = new Bitmap(new MemoryStream(LanPairingQr.Encode(current)));
                invitation.Text = current; qr.Source = bitmap; qr.IsVisible = true;
                lifetime.Text = L.T("用手机扫码，或复制下方邀请手动配对。刷新会使旧邀请失效。");
            }
            catch (Exception) { ClearInvitation(); problem.Text = L.T("请输入手机可达的局域网地址；不能使用 localhost、回环、广播或未指定地址。检查服务和网络。"); }
            finally { generating = false; }
        });
        inviteButton = generate; generate.IsEnabled = service.Port != 0;
        panel.Children.Add(generate); panel.Children.Add(qr); panel.Children.Add(lifetime); panel.Children.Add(invitation);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => CheckInvitation();
        service.Changed += CheckInvitation;
        panel.AttachedToVisualTree += (_, _) => { detached = false; timer.Start(); };
        panel.DetachedFromVisualTree += (_, _) => { detached = true; timer.Stop(); service.Changed -= CheckInvitation; ClearInvitation(); };
        foreach (var device in diagnostics["devices"]!.AsArray())
        {
            var id = device!["id"]!.GetValue<string>();
            panel.Children.Add(SubHeading(device["name"]!.GetValue<string>() + " · " + id[..Math.Min(8, id.Length)]));
            panel.Children.Add(Note(L.T("同步凭证长期有效，直到手动撤销或重新配对。")));
            panel.Children.Add(Action(L.T("撤销设备") + " — " + device["name"]!.GetValue<string>(), () => { service.Revoke(id); c.Window.Build(); }));
        }
        panel.Children.Add(Note(L.T("手机同步时完整核验所选内容，可检测大小和修改时间均未变化的文件。首次核验大目录可能较慢；连接失败时检查服务、网络及防火墙状态。")));
    }
}
