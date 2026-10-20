using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using JeekMusic.Core.Library;
using JeekMusic.Library;
using JeekMusic.Platform;
using JeekMusic.Settings;

namespace JeekMusic.UI;

/// <summary>
/// 设置（需求 §3.11，同移动端 §3.14）。左边是分区列表，右边是分区内容；各功能用 <see cref="Register"/> 加入自己的分区。
/// 改动立即生效并保存（不需要"确定"按钮）。
/// </summary>
public sealed class SettingsWindow : Window
{
    public sealed record Section(string Title, Func<SettingsContext, Control> Build, int Order);

    /// <summary>构建分区时可以用的东西。</summary>
    public sealed record SettingsContext(AppServices App, SettingsWindow Window, MainWindow Main)
    {
        public AppSettings S => App.Settings.Current;
        public void Save(string category = "general") => App.Settings.Save(category);
    }

    static readonly List<Section> Sections = new();

    public static void Register(string title, int order, Func<SettingsContext, Control> build)
    {
        if (Sections.All(s => s.Title != title)) Sections.Add(new Section(title, build, order));
    }

    static SettingsWindow? _open;

    public static void ShowFor(MainWindow main, string? section = null)
    {
        if (_open is not null) { _open.Activate(); if (section is not null) _open.Select(section); return; }
        RegisterBuiltins();
        _open = new SettingsWindow(AppServices.Current, main);
        _open.Closed += (_, _) => _open = null;
        _open.Show(main);
        if (section is not null) _open.Select(section);
    }

    readonly ListBox _list;
    readonly ContentControl _content;
    readonly SettingsContext _ctx;

    SettingsWindow(AppServices app, MainWindow main)
    {
        Title = "设置";
        Width = 860;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = main.Icon;
        _ctx = new SettingsContext(app, this, main);
        _list = new ListBox { Width = 180, Background = Brushes.Transparent, Margin = new Thickness(8) };
        foreach (var s in Sections.OrderBy(s => s.Order)) _list.Items.Add(s.Title);
        _content = new ContentControl();
        _list.SelectionChanged += (_, _) => Build();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(_list);
        var scroll = new ScrollViewer { Content = _content, Padding = new Thickness(16, 12, 24, 24) };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        Content = grid;
        _list.SelectedIndex = 0;
    }

    public void Select(string title) => _list.SelectedItem = title;

    /// <summary>重新构建当前分区（设置变了，比如设备列表）。</summary>
    public void Build()
    {
        if (_list.SelectedItem is not string title || Sections.FirstOrDefault(s => s.Title == title) is not { } section) return;
        _content.Content = new StackPanel { Spacing = 6, Children = { Heading(title), section.Build(_ctx) } };
    }

    // ── 搭界面的小工具 ──

    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

    public static TextBlock SubHeading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 4) };

    public static TextBlock Note(string text) => new() { Text = text, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };

    public static CheckBox Toggle(string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = text, IsChecked = value };
        c.IsCheckedChanged += (_, _) => changed(c.IsChecked == true);
        return c;
    }

    public static StackPanel Row(params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static ComboBox Choice<T>(IReadOnlyList<(T value, string label)> options, T current, Action<T> changed)
    {
        var box = new ComboBox { MinWidth = 220 };
        foreach (var (_, label) in options) box.Items.Add(label);
        box.SelectedIndex = Math.Max(0, options.ToList().FindIndex(o => EqualityComparer<T>.Default.Equals(o.value, current)));
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) changed(options[box.SelectedIndex].value); };
        return box;
    }

    public static Button Action(string text, Action action)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => action();
        return b;
    }

    public static Control Labeled(string label, Control control) =>
        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, MinWidth = 140 }, control } };

    // ── 内置分区 ──

    static bool _builtins;

    static void RegisterBuiltins()
    {
        if (_builtins) return;
        _builtins = true;
        Register("曲库", 10, LibrarySection);
        Register("曲库规则", 20, RulesSection);
        Register("播放", 30, PlaybackSection);
        Register("外观和窗口", 40, WindowSection);
        Register("快捷键", 50, HotkeySection);
        ExtraSections?.Invoke();
    }

    /// <summary>各功能注册自己的分区（歌词、封面、智能标签、同步、关于）。</summary>
    public static event Action? ExtraSections;

    static Control LibrarySection(SettingsContext c)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Note("曲库围绕这些文件夹组织（需求 L-1）。可以是本地磁盘、U 盘、移动硬盘；也可以从资源管理器把文件夹拖进这里。"));
        var list = new StackPanel { Spacing = 4 };
        void Fill()
        {
            list.Children.Clear();
            foreach (var f in c.App.Library.Db.Folders())
            {
                var count = c.App.Library.Snapshot.Tracks.Count(t => t.FolderId == f.Id);
                var remove = new Button { Content = "移除", Padding = new Thickness(10, 2) };
                remove.Click += async (_, _) =>
                {
                    if (!await Dialogs.Confirm(c.Window, "移除音乐文件夹", $"移除「{f.Root.TrimEnd('\\')}」？其中的歌会从曲库消失，播放列表里的条目保留并显示为不可用。文件不会被删除。", "移除", danger: true)) return;
                    await c.App.Library.RemoveFolderAsync(f.Id);
                    Fill();
                };
                list.Children.Add(new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                    Children =
                    {
                        new TextBlock { Text = f.Root.TrimEnd('\\') + (f.Available ? "" : "（不可用）"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                        Col(new TextBlock { Text = Text.Count(count), Opacity = 0.6, Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center }, 1),
                        Col(remove, 2),
                    },
                });
            }
            if (list.Children.Count == 0) list.Children.Add(Note("还没有添加音乐文件夹。"));
        }
        Fill();
        c.App.Library.SnapshotChanged += Fill;
        c.Window.Closed += (_, _) => c.App.Library.SnapshotChanged -= Fill;
        panel.Children.Add(list);
        panel.Children.Add(Row(
            Action("添加文件夹…", async () => { await c.Main.AddFolder(); }),
            Action("重新扫描", () => { _ = c.App.Library.RescanAsync(full: false); c.Main.ViewModel.ShowToast("正在重新扫描"); }),
            Action("重建曲库", async () =>
            {
                if (await Dialogs.Confirm(c.Window, "重建曲库", "清空曲库缓存，重新读取所有文件的标签？播放列表、手动修正等不受影响。", "重建"))
                { _ = c.App.Library.RescanAsync(full: true); c.Main.ViewModel.ShowToast("正在重建曲库"); }
            })));
        if (c.App.Library.UnsupportedCount > 0)
            panel.Children.Add(Note($"有 {c.App.Library.UnsupportedCount} 个不支持的文件（如 DSD），没有加入曲库。可以转换成 FLAC。"));
        DragDrop.SetAllowDrop(panel, true);
        panel.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            foreach (var item in e.DataTransfer.TryGetFiles() ?? [])
                if (item.TryGetLocalPath() is { } path && Directory.Exists(path)) await c.Main.AddFolder(path);
        });
        return panel;
    }

    static Control Col(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    static Control RulesSection(SettingsContext c)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Note("这些规则和手机端一样，改动后自动同步到其他设备（需求 SY-2）。改了之后用库里已有的标签重新整理艺术家，不用重新读文件。"));
        panel.Children.Add(SubHeading("艺术家分隔符"));
        panel.Children.Add(Note("一首歌的艺术家标签里有这些符号时拆成多个艺术家（需求 A-3）。& 和 , 默认不拆，因为会把\"小娟&山谷里的居民\"这类组合名拆坏。"));
        var wrap = new WrapPanel();
        foreach (var sep in ArtistSplitConfig.AllSeparators)
        {
            var label = sep is "feat." or "ft." or "featuring" or "x" ? $" {sep} " : sep;
            wrap.Children.Add(new CheckBox
            {
                Content = label, IsChecked = c.S.ArtistSeparators.Contains(sep), Margin = new Thickness(0, 0, 16, 0),
                Tag = sep,
            });
        }
        foreach (var child in wrap.Children.OfType<CheckBox>())
            child.IsCheckedChanged += (_, _) =>
            {
                c.S.ArtistSeparators = wrap.Children.OfType<CheckBox>().Where(x => x.IsChecked == true).Select(x => (string)x.Tag!).ToList();
                RulesChanged(c);
            };
        panel.Children.Add(wrap);

        panel.Children.Add(SubHeading("不拆分的名字"));
        panel.Children.Add(Note("名字里本来就带分隔符的乐队或组合，每行一个（需求 A-4）。"));
        var exceptions = new TextBox { AcceptsReturn = true, Height = 110, Text = string.Join("\n", c.S.ArtistExceptions), TextWrapping = TextWrapping.NoWrap };
        exceptions.LostFocus += (_, _) =>
        {
            var list = (exceptions.Text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            if (list.SequenceEqual(c.S.ArtistExceptions)) return;
            c.S.ArtistExceptions = list;
            RulesChanged(c);
        };
        panel.Children.Add(exceptions);

        panel.Children.Add(SubHeading("从文件名推断艺术家"));
        panel.Children.Add(Toggle("艺术家标签为空时，从文件名推断（需求 A-9）", c.S.InferFromFileName, v => { c.S.InferFromFileName = v; RulesChanged(c); }));
        panel.Children.Add(Labeled("文件名格式", Choice<string>([("ARTIST_TITLE", "艺术家 - 标题"), ("TITLE_ARTIST", "标题 - 艺术家")], c.S.FileNameFormat,
            v => { c.S.FileNameFormat = v; RulesChanged(c); })));

        panel.Children.Add(SubHeading("旧标签编码"));
        panel.Children.Add(Note("ID3v2.3 等旧标签里没有标明编码的文字。自动时只修复看起来像 GBK 的乱码；改编码后要\"重建曲库\"才对已有的歌生效。"));
        panel.Children.Add(Labeled("编码", Choice<string?>([(null, "自动"), ("GBK", "GBK（简体中文）"), ("Big5", "Big5（繁体中文）"), ("Shift_JIS", "Shift-JIS（日文）")], c.S.LegacyCharset,
            v => { c.S.LegacyCharset = v; c.Save("rules"); })));
        return panel;
    }

    static void RulesChanged(SettingsContext c)
    {
        c.Save("rules");
        _ = c.App.Library.ApplyRulesAsync();
    }

    static Control PlaybackSection(SettingsContext c)
    {
        var panel = new StackPanel { Spacing = 6 };
        var p = c.App.Player;
        panel.Children.Add(SubHeading("输出设备"));
        var devices = new List<(string?, string)> { (null, "跟随 Windows 的默认设备") };
        devices.AddRange(p.Devices.Where(d => d.name != "auto").Select(d => ((string?)d.name, d.description)));
        if (c.S.OutputDevice is { } current && devices.All(d => d.Item1 != current)) devices.Add((current, (c.S.OutputDeviceDescription ?? current) + "（未连接）"));
        panel.Children.Add(Choice<string?>(devices, c.S.OutputDevice, v =>
        {
            c.S.OutputDevice = v;
            c.S.OutputDeviceDescription = devices.FirstOrDefault(d => d.Item1 == v).Item2;
            c.Save("playback");
            p.SetDevice(v);
        }));
        panel.Children.Add(Note("跟随默认设备时，插上耳机、连上蓝牙后自动切过去；指定的设备断开时暂停（需求 O-2）。"));

        panel.Children.Add(SubHeading("音质"));
        panel.Children.Add(Toggle("WASAPI 独占模式", c.S.Exclusive, v => { c.S.Exclusive = v; c.Save("playback"); p.SetExclusive(v); }));
        panel.Children.Add(Note("开启后设备使用文件自己的采样率和位深，不经过系统混音器、不重采样；这时其他程序没有声音，音量条调的是设备音量（设备不支持时不可调）。正在播放时切换会在这首歌结束、暂停或跳转时生效（需求 O-3、O-4）。"));
        panel.Children.Add(Note($"当前：{c.Main.ViewModel.Player.FormatText}"));

        panel.Children.Add(SubHeading("其他"));
        panel.Children.Add(Toggle("任务栏图标上显示播放进度", c.S.TaskbarProgress, v => { c.S.TaskbarProgress = v; c.Save("playback"); }));
        return panel;
    }

    static Control WindowSection(SettingsContext c)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Labeled("主题", Choice([(ThemeMode.System, "跟随系统"), (ThemeMode.Light, "浅色"), (ThemeMode.Dark, "深色")], c.S.Theme,
            v => { c.S.Theme = v; c.Save("theme"); })));
        panel.Children.Add(Labeled("关闭主窗口时", Choice([(CloseAction.Tray, "隐藏到托盘，继续播放"), (CloseAction.Exit, "退出程序")], c.S.CloseAction,
            v => { c.S.CloseAction = v; c.Save("window"); })));
        panel.Children.Add(Toggle("开机时启动（隐藏在托盘里）", c.S.LaunchAtStartup, v =>
        {
            c.S.LaunchAtStartup = v;
            c.Save("window");
            SystemState.SetLaunchAtStartup(v);
        }));
        panel.Children.Add(Toggle("迷你播放器总在最前", c.S.MiniPlayerTopmost, v => { c.S.MiniPlayerTopmost = v; c.Save("mini"); }));
        return panel;
    }

    static readonly (string action, string label)[] HotkeyActions =
    [
        ("PlayPause", "播放 / 暂停"), ("Previous", "上一首"), ("Next", "下一首"), ("DesktopLyrics", "显示 / 隐藏桌面歌词"),
        ("LockLyrics", "锁定 / 解锁桌面歌词"), ("ShowWindow", "显示主窗口"),
    ];

    static Control HotkeySection(SettingsContext c)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(SubHeading("全局快捷键"));
        panel.Children.Add(Note("窗口不在前台、隐藏到托盘时也有效（需求 PC-3）。点输入框后按下想要的组合键；按 Backspace 清空。"));
        panel.Children.Add(Toggle("启用全局快捷键", c.S.GlobalHotkeysEnabled, v => { c.S.GlobalHotkeysEnabled = v; c.Save("hotkeys"); }));
        foreach (var (action, label) in HotkeyActions)
        {
            var box = new TextBox { Width = 220, IsReadOnly = true, Text = c.S.GlobalHotkeys.GetValueOrDefault(action, "") };
            var failed = c.App.Hotkeys.Failed.Contains(action);
            box.KeyDown += (_, e) =>
            {
                e.Handled = true;
                if (e.Key is Key.Back or Key.Delete) { box.Text = ""; }
                else if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
                else box.Text = HotkeyText(e.KeyModifiers, e.Key);
                c.S.GlobalHotkeys[action] = box.Text ?? "";
                c.Save("hotkeys");
            };
            panel.Children.Add(Labeled(label, failed
                ? Row(box, new TextBlock { Text = "已被其他程序占用", Foreground = Brushes.IndianRed, VerticalAlignment = VerticalAlignment.Center })
                : box));
        }
        panel.Children.Add(SubHeading("窗口里的快捷键"));
        panel.Children.Add(Note("空格 播放 / 暂停　Ctrl+← / → 上一首 / 下一首　Ctrl+F 搜索　Ctrl+L 定位到正在播放\nAlt+← / → 后退 / 前进　F5 重新扫描　回车 播放选中的歌　F2 改名　Delete 删除"));
        return panel;
    }

    public static string HotkeyText(KeyModifiers mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((int)(key - Key.NumPad0)).ToString(),
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            _ => key.ToString(),
        });
        return string.Join('+', parts);
    }
}
