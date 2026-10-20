using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using JeekMusic.Infrastructure;
using JeekMusic.UI;

namespace JeekMusic.Update;

/// <summary>
/// 检查更新（需求 UP-2）：首帧之后的推迟任务最后再等 5 秒执行（同移动端）；发现新版本时提示更新说明，
/// "更新 / 跳过此版本 / 以后再说"。设置里的"关于"分区有手动检查和自动检查的开关。
/// </summary>
public sealed class UpdateFeature : IFeature
{
    UpdateService? _service;
    bool _checking;

    public void Start(AppServices app, Shell shell)
    {
        _service = new UpdateService(app.Settings);
        UpdateService.CleanUp();
        SettingsWindow.ExtraSections += () => SettingsWindow.Register("关于", 1000, c => AboutSection(c, shell));
        DispatcherTimer.RunOnce(() =>
        {
            if (_service.DueForAutoCheck && UpdateService.CurrentVersion > 0) _ = Check(shell, manual: false);
        }, TimeSpan.FromSeconds(5));
    }

    async Task Check(Shell shell, bool manual, Action<string>? status = null)
    {
        if (_checking || _service is null) return;
        _checking = true;
        try
        {
            status?.Invoke("正在检查…");
            var info = await _service.CheckAsync(manual);
            if (info is null) { status?.Invoke("已是最新版本"); if (manual) shell.Main.ViewModel.ShowToast("已是最新版本"); return; }
            status?.Invoke($"发现新版本 {info.VersionName}");
            await Prompt(shell, info);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Warn("update", "检查更新失败", e);
            status?.Invoke("检查更新失败");
            if (manual) shell.Main.ViewModel.ShowToast("检查更新失败：" + e.Message);
        }
        finally { _checking = false; }
    }

    async Task Prompt(Shell shell, UpdateInfo info)
    {
        var owner = shell.Main.IsVisible ? (Window)shell.Main : shell.Main;
        var choice = await Dialogs.Choose(owner, "发现新版本", $"JeekMusic {info.VersionName}（当前 {UpdateService.CurrentVersionName}）\n\n{Trim(info.Notes)}",
            ["更新", "跳过此版本", "以后再说"]);
        if (choice == 1) { _service!.Skip(info); return; }
        if (choice != 0) return;
        if (!UpdateService.AppDirWritable)
        {
            await Dialogs.Info(owner, "更新", "程序所在的文件夹不可写（比如放在了 Program Files 里），请到 GitHub Releases 下载新版本手动替换。");
            return;
        }
        var progressText = new TextBlock { Text = "正在下载…" };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var w = new Window
        {
            Title = "更新", Width = 400, SizeToContent = SizeToContent.Height, CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Children = { progressText, bar, cancel } },
        };
        var cts = new CancellationTokenSource();
        cancel.Click += (_, _) => { cts.Cancel(); w.Close(); };
        _ = w.ShowDialog(owner);
        try
        {
            var exe = await _service!.DownloadAsync(info, new Progress<double>(p => { bar.Value = p; progressText.Text = $"正在下载… {p:P0}"; }), cts.Token);
            w.Close();
            if (await Dialogs.Confirm(owner, "更新", $"新版本 {info.VersionName} 已下载，现在重新启动完成更新？", "重新启动", "稍后"))
            {
                UpdateService.LaunchInstaller(exe);
                shell.Exit();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException)
        {
            w.Close();
            Log.Warn("update", "下载更新失败", e);
            await Dialogs.Info(owner, "更新", "下载失败：" + e.Message);
        }
    }

    static string Trim(string notes) => notes.Length > 1200 ? notes[..1200] + "…" : notes;

    Control AboutSection(SettingsWindow.SettingsContext c, Shell shell)
    {
        var panel = new StackPanel { Spacing = 6 };
        var status = new TextBlock { Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = $"JeekMusic 桌面端 {UpdateService.CurrentVersionName}", FontSize = 16, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(SettingsWindow.Row(SettingsWindow.Action("检查更新", () => _ = Check(shell, manual: true, s => status.Text = s)), status));
        panel.Children.Add(SettingsWindow.Toggle("每天自动检查一次新版本", c.S.AutoUpdateCheck, v => { c.S.AutoUpdateCheck = v; c.Save("update"); }));
        panel.Children.Add(SettingsWindow.Note("检查更新只请求 GitHub 上的版本信息，不发送任何其他数据。除此之外，默认不联网（在线歌词、封面要自己开启）。"));
        panel.Children.Add(SettingsWindow.SubHeading("源代码"));
        var link = SettingsWindow.Action("https://github.com/tifish/JeekMusic", () => Open("https://github.com/tifish/JeekMusic"));
        panel.Children.Add(link);
        panel.Children.Add(SettingsWindow.SubHeading("开源许可"));
        panel.Children.Add(SettingsWindow.Note("JeekMusic：MIT\n" +
            "libmpv、FFmpeg：LGPL 2.1+（开发版附带的现成 libmpv 是 GPL）\n" +
            "Avalonia、SkiaSharp、HarfBuzzSharp、CommunityToolkit.Mvvm：MIT\n" +
            "ATL .NET（标签读取）：MIT\n" +
            "Microsoft.Data.Sqlite、SQLitePCLRaw：MIT / Apache 2.0；SQLite：公有领域\n" +
            "ONNX Runtime：MIT；YAMNet 模型：Apache 2.0\n" +
            "拼音、繁简对照表：由 ICU 数据生成（Unicode License）"));
        panel.Children.Add(SettingsWindow.SubHeading("数据"));
        panel.Children.Add(SettingsWindow.Row(SettingsWindow.Action("打开数据目录", () => Open(AppPaths.DataDir)),
            SettingsWindow.Action("打开日志目录", () => Open(AppPaths.LogsDir))));
        return panel;
    }

    static void Open(string target) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
}
