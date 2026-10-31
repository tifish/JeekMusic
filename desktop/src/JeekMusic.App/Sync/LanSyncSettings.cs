using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
        panel.Children.Add(Note(L.T("电脑提供音乐，手机选择音乐文件夹或子文件夹及保存位置。开启局域网服务后，所有已添加的曲库音乐文件夹都会自动共享；取消同步不删除音乐。")));
        var problem = Note(service.Problem); panel.Children.Add(problem);
        var activity = Note(""); panel.Children.Add(activity);
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
            try { if (enabledSwitch is not null) enabledSwitch.IsChecked = service.Port != 0; }
            finally { refreshing = false; }
        }
        service.Changed += Refresh;
        c.App.Library.SnapshotChanged += Refresh;
        panel.DetachedFromVisualTree += (_, _) => { service.Changed -= Refresh; c.App.Library.SnapshotChanged -= Refresh; };
        enabledSwitch = Toggle(L.T("启用局域网服务"), service.Port != 0, enabled =>
        {
            if (refreshing) return;
            try { if (enabled) service.Start(); else service.Stop(); c.Window.Build(); }
            catch (Exception) { Refresh(); problem.Text = L.T("无法监听端口。检查端口占用、网络权限和防火墙；程序不会修改系统防火墙。"); }
        });
        panel.Children.Add(enabledSwitch);
        Refresh();
        var diagnostics = service.Diagnostics();
        panel.Children.Add(SubHeading(L.T("共享音乐文件夹")));
        panel.Children.Add(sharing); panel.Children.Add(guidance); panel.Children.Add(folders); panel.Children.Add(addFolders);
        panel.Children.Add(Note(L.T("手机扫码配对，或手动输入邀请。两端须在同一局域网。邀请 5 分钟有效且只能使用一次；请勿截图或公开。")));
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
                current = service.Invite(address);
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
            panel.Children.Add(Action(L.T("撤销设备") + " — " + device["name"]!.GetValue<string>(), () => { service.Revoke(id); c.Window.Build(); }));
        }
        panel.Children.Add(Note(L.T("手机同步时完整核验所选内容，可检测大小和修改时间均未变化的文件。首次核验大目录可能较慢；权限或防火墙阻止连接时可重试，不会自动更改系统规则。")));
    }
}
