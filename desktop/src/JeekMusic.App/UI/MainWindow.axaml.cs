using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Settings;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

/// <summary>主窗口（需求 §3.2 ~ §3.4、W-1 ~ W-8、G-1 ~ G-7）。</summary>
public partial class MainWindow : Window
{
    readonly AppServices _app;
    readonly MainViewModel _vm;

    public MainViewModel ViewModel => _vm;

    /// <summary>真正退出（托盘菜单"退出"）；否则关闭按钮按设置隐藏到托盘（需求 PC-2）。</summary>
    public bool ExitRequested { get; set; }

    public MainWindow() : this(AppServices.Current) { }

    public MainWindow(AppServices app)
    {
        _app = app;
        _vm = new MainViewModel(app);
        DataContext = _vm;
        InitializeComponent();

        Table.DoubleTapped += (_, e) => { if (e.Source is not DataGridColumnHeader) PlaySelected(); };
        Table.KeyDown += OnTableKeyDown;
        Table.Sorting += OnSorting;
        Table.LoadingRow += OnLoadingRow;
        Table.UnloadingRow += OnUnloadingRow;
        Table.ContextRequested += OnTableContextRequested;
        _vm.RowsChanged += UpdateSortHeaders;
        _vm.Navigated += () => { if (Table.ItemsSource is not null) Dispatcher.UIThread.Post(() => ScrollToTop(), DispatcherPriority.Background); };

        Progress.AddHandler(PointerPressedEvent, (_, _) => _vm.Player.BeginSeek(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Progress.AddHandler(PointerReleasedEvent, (_, _) => _vm.Player.EndSeek(Progress.Value), RoutingStrategies.Tunnel, handledEventsToo: true);
        Progress.AddHandler(PointerCaptureLostEvent, (_, _) => _vm.Player.EndSeek(Progress.Value), RoutingStrategies.Bubble, handledEventsToo: true);

        FilterButton.Click += (_, _) => ShowFilterMenu();
        WelcomeAddFolder.Click += async (_, _) => await AddFolder();
        LocateButton.Click += (_, _) => LocatePlaying();
        ListMoreButton.Click += (_, _) => ShowListMenu();
        ArtistPinButton.Click += (_, _) => { if (_vm.CurrentArtistKey is { } key) TogglePin(key); };
        NowTitle.PointerPressed += (_, _) => LocatePlaying();
        NowArtist.Click += (_, _) => { if (_app.Player.CurrentTrack is { } t) GoToArtist(t); };

        NowAlbum.Click += (_, _) => { if (_app.Player.CurrentTrack is { } track) GoToAlbum(track); };
        OutputButton.Click += (_, _) => ShowOutputMenu();
        OutputModeButton.Click += (_, _) => ShowOutputMenu(OutputModeButton);
        OutputModeButton.ContextRequested += (_, e) => { ShowOutputMenu(OutputModeButton); e.Handled = true; };
        Closed += (_, _) => ActiveOutputMenu?.Close();
        ArtistList.ContextRequested += OnArtistContextRequested;
        LetterIndex.PointerPressed += OnLetterIndexPressed;
        _app.ArtistList = ArtistList;
        _app.Navigation = _vm;
        Closed += (_, _) => { if (ReferenceEquals(_app.ArtistList, ArtistList)) _app.ArtistList = null; };
        Closed += (_, _) => { if (ReferenceEquals(_app.Navigation, _vm)) _app.Navigation = null; };

        InitExtras();
        InitNav();
        PlaybackShortcuts.Attach(this, _app.Player, Progress, VolumeSlider);
        KeyDown += OnWindowKeyDown;
        AddHandler(PointerPressedEvent, OnMouseButtons, RoutingStrategies.Tunnel);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) UpdateVisibility();
        };
    }

    // ── 播放 ──

    /// <summary>播放选中的那首：当前列表作为队列，从它开始（需求 G-3、P-15）。</summary>
    void PlaySelected()
    {
        if (Table.SelectedItem is not SongRow { Track: { } track }) return;
        PlayFrom(track);
    }

    public void PlayFrom(Track track, bool? shuffle = null)
    {
        var playable = _vm.Rows.Where(r => r.Track is not null).Select(r => r.Track!).ToList();
        var index = playable.FindIndex(t => t.Id == track.Id);
        if (index < 0) return;
        var source = _vm.CurrentSource;
        _app.Player.Play(source is null ? null : new QueueContext(source, _vm.ContentTitle), playable.Select(t => t.Id).ToList(), index, shuffle);
    }

    /// <summary>"播放全部"/"随机播放"（文件夹的更多菜单，同移动端 F-6）。</summary>
    void PlayAll(bool shuffle, bool recursive = false)
    {
        QueueSource? source = _vm.CurrentSource;
        List<Track> list;
        if (recursive && source is QueueSource.Folder f)
        {
            source = f with { Recursive = true };
            list = ListBuilder.Tracks(_app.Library.Snapshot, source, _app.Settings.Current, _app.Playlists);
        }
        else list = _vm.Rows.Where(r => r.Track is not null).Select(r => r.Track!).ToList();
        if (list.Count == 0) return;
        _app.Player.Play(source is null ? null : new QueueContext(source, _vm.ContentTitle), list.Select(t => t.Id).ToList(), shuffle ? null : 0, shuffle);
    }

    IReadOnlyList<Track> SelectedTracks() =>
        Table.SelectedItems.OfType<SongRow>().Where(r => r.Track is not null).Select(r => r.Track!).ToList();

    // ── 表格 ──

    void OnTableKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                PlaySelected();
                e.Handled = true;
                break;
            case Key.Delete:
                _ = DeleteSelected();
                e.Handled = true;
                break;
            case Key.F2:
                _ = RenameSelected();
                e.Handled = true;
                break;
        }
    }

    static readonly Dictionary<string, SongSort> SortOfColumn = new()
    {
        ["Title"] = SongSort.Title, ["Artist"] = SongSort.Artist, ["Album"] = SongSort.Album,
        ["Duration"] = SongSort.Duration, ["FileName"] = SongSort.FileName, ["Modified"] = SongSort.Modified, ["Format"] = SongSort.Format,
    };

    /// <summary>点表头：排序由我们自己做（全局生效，需求 G-2），不用 DataGrid 自带的排序（设计 §6.3）。</summary>
    void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (e.Column.Tag is string tag && SortOfColumn.TryGetValue(tag, out var sort)) _vm.SetSort(sort);
    }

    static readonly Dictionary<string, string> ColumnTitles = new()
    {
        ["Title"] = L.T("标题"), ["Artist"] = L.T("艺术家"), ["Album"] = L.T("专辑"), ["Duration"] = L.T("时长"), ["FileName"] = L.T("文件名"),
        ["Modified"] = L.T("修改时间"), ["Format"] = L.T("格式"), ["Tags"] = L.T("智能标签"),
    };

    /// <summary>表头的排序箭头自己画（DataGrid 自带的只反映它自己的排序）。</summary>
    void UpdateSortHeaders()
    {
        foreach (var c in Table.Columns)
        {
            if (c.Tag is not string tag || !ColumnTitles.TryGetValue(tag, out var title)) continue;
            var sorted = SortOfColumn.TryGetValue(tag, out var s) && s == _vm.Sort;
            var descending = _vm.Sort == SongSort.Modified ? !_vm.SortDescending : _vm.SortDescending;
            c.Header = sorted ? title + (descending ? " ↓" : " ↑") : title;
        }
    }

    void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not SongRow row) return;
        e.Row.Classes.Set("playing", row.IsPlaying);
        e.Row.Classes.Set("missing", !row.Available);
        row.PropertyChanged += OnRowChanged;
        e.Row.Tag = row;
        LoadRowCover(e.Row, row);
    }

    void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        CancelRowCover(e.Row);
        if (e.Row.Tag is SongRow row)
        {
            row.PropertyChanged -= OnRowChanged;
            row.Cover = null;
        }
        e.Row.Tag = null;
    }

    void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SongRow.IsPlaying) || sender is not SongRow row) return;
        foreach (var r in Table.GetVisualDescendants().OfType<DataGridRow>())
            if (r.DataContext == row) r.Classes.Set("playing", row.IsPlaying);
    }

    void ScrollToTop()
    {
        if (_vm.Rows.Count > 0) Table.ScrollIntoView(_vm.Rows[0], null);
    }

    /// <summary>定位到正在播放（需求 G-5、P-10）。</summary>
    public void LocatePlaying()
    {
        var id = _app.Player.CurrentTrack?.Id;
        var row = _vm.Rows.FirstOrDefault(r => r.Track?.Id == id);
        if (row is null)
        {
            // 不在当前列表里：跳到它所在的文件夹
            if (_app.Player.CurrentTrack is { } t)
            {
                _vm.Navigate(new NavTarget.Folder(t.FolderId, t.Dir));
                _vm.RowsChanged += Once;
                void Once()
                {
                    _vm.RowsChanged -= Once;
                    LocatePlaying();
                }
            }
            return;
        }
        _vm.RevealCurrentFolderPath();
        Table.SelectedItem = row;
        Table.ScrollIntoView(row, null);
    }

    // ── 艺术家字母索引 ──

    void OnLetterIndexPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<TextBlock>(includeSelf: true)?.DataContext is not ArtistLetter group) return;
        ArtistList.NavigateToLetter(group.Letter);
        e.Handled = true;
    }

    void OnSearchArtistClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key }) _vm.Navigate(new NavTarget.Artists(key));
    }

    public void GoToArtist(Track t)
    {
        var key = t.Artists.Count > 0 ? Core.Library.ArtistSplitter.NameKey(t.Artists[0]) : "";
        _vm.Navigate(new NavTarget.Artists(key));
    }

    public void GoToAlbum(Track t)
    {
        if (t.AlbumKey is { } key) _vm.Navigate(new NavTarget.Album(key));
    }

    // ── 快捷键（需求 PC-8、G-6）──

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var inText = FocusManager?.GetFocusedElement() is TextBox;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.F when ctrl:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.L when ctrl:
                LocatePlaying();
                e.Handled = true;
                break;
            case Key.Left when e.KeyModifiers == KeyModifiers.Alt:
                _vm.GoBackCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right when e.KeyModifiers == KeyModifiers.Alt:
                _vm.GoForwardCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F5:
                _ = _app.Library.RescanAsync(full: false);
                _vm.ShowToast(L.T("正在重新扫描"));
                e.Handled = true;
                break;
            case Key.Escape when inText:
                _vm.SearchText = "";
                Table.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>鼠标侧键后退 / 前进。</summary>
    void OnMouseButtons(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Properties;
        if (p.IsXButton1Pressed) { _vm.GoBackCommand.Execute(null); e.Handled = true; }
        else if (p.IsXButton2Pressed) { _vm.GoForwardCommand.Execute(null); e.Handled = true; }
    }

    // ── 筛选菜单（需求 M-6）──

    void ShowFilterMenu()
    {
        var menu = new MenuFlyout();
        void Group(string title, params string[] tags)
        {
            menu.Items.Add(new MenuItem { Header = title, IsEnabled = false });
            foreach (var tag in tags)
            {
                var item = new MenuItem { Header = "    " + Text.TagName(tag), ToggleType = MenuItemToggleType.CheckBox, IsChecked = _vm.TagFilter.Contains(tag) };
                item.Click += (_, _) => _vm.ToggleTag(tag);
                menu.Items.Add(item);
            }
        }
        Group(L.T("氛围"), "SPORT", "NON_SPORT");
        Group(L.T("人声"), "VOCAL", "INSTRUMENTAL");
        Group(L.T("曲风"), "CLASSICAL", "NON_CLASSICAL");
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = L.T("清除筛选"), IsEnabled = _vm.FilterActive };
        clear.Click += (_, _) => _vm.ClearFilterCommand.Execute(null);
        menu.Items.Add(clear);
        AddFilterExtras(menu);
        menu.ShowAt(FilterButton);
    }

    /// <summary>筛选菜单底部的"还有 N 首未分析"（智能标签做好后填充）。</summary>
    partial void AddFilterExtras(MenuFlyout menu);

    // ── 可见性：窗口不可见时停止刷新进度（设计 §13）──

    void UpdateVisibility() => _vm.Player.ProgressVisible = IsVisible && WindowState != WindowState.Minimized || MiniPlayerVisible;

    public bool MiniPlayerVisible { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) UpdateVisibility();
    }

    // ── 添加音乐文件夹（需求 L-1、L-3）──

    public async Task AddFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("选择音乐文件夹"), AllowMultiple = true });
        foreach (var f in folders)
            if (f.TryGetLocalPath() is { } path) await AddFolder(path);
    }

    public async Task AddFolder(string path)
    {
        var check = _app.Library.CheckFolder(path);
        switch (check.Status)
        {
            case AddFolderStatus.NotFound:
                await Dialogs.Info(this, L.T("添加音乐文件夹"), L.F($"找不到文件夹：{path}"));
                return;
            case AddFolderStatus.AlreadyAdded:
                _vm.ShowToast(L.F($"「{check.Folder!.DisplayName}」已经添加过了"));
                return;
            case AddFolderStatus.AlreadyContained:
                await Dialogs.Info(this, L.T("添加音乐文件夹"), L.F($"这个文件夹已包含在「{check.Folder!.Root.TrimEnd('\\')}」里，不需要再添加。"));
                return;
            case AddFolderStatus.ContainsExisting:
                var names = string.Join("\n", check.Children.Select(c => "  " + c.Root.TrimEnd('\\')));
                if (!await Dialogs.Confirm(this, L.T("添加音乐文件夹"), L.F($"这个文件夹包含已经添加的音乐文件夹：\n{names}\n\n是否用它替换这些子文件夹？"), L.T("替换")))
                    return;
                await _app.Library.AddFolderAsync(path, replaceChildren: true);
                break;
            default:
                _vm.ShowToast(L.T("正在扫描新添加的文件夹…"));
                await _app.Library.AddFolderAsync(path, replaceChildren: false);
                break;
        }
    }

    // ── 关闭 ──

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (ExitRequested) return;
        if (_app.Settings.Current.CloseAction == CloseAction.Exit)
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(Shell.Current.Exit);
            return;
        }
        e.Cancel = true;
        Hide();
        if (!_app.Settings.Current.CloseToTrayHintShown)
        {
            _app.Settings.Current.CloseToTrayHintShown = true;
            _app.Settings.Save("window");
            TrayHint?.Invoke();
        }
    }

    /// <summary>第一次关闭到托盘时提示一次（需求 PC-2）。</summary>
    public event Action? TrayHint;
}
