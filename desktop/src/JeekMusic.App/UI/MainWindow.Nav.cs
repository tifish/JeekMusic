using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

/// <summary>
/// 导航栏的交互（需求 W-2、T-5、§3.7）：右键菜单、拖动排序、折叠成图标；歌曲从表格拖到播放列表上加入它，
/// 播放列表视图里拖动调整顺序。
/// </summary>
public partial class MainWindow
{
    static readonly DataFormat<string> TracksFormat = DataFormat.CreateInProcessFormat<string>("jeekmusic/tracks");
    static readonly DataFormat<string> NavFormat = DataFormat.CreateInProcessFormat<string>("jeekmusic/nav");
    bool _navCollapsed;
    double _navWidth = 230;

    void InitNav()
    {
        FolderNavigationInput.Attach(NavTree, _app.FolderExpansion);
        NavCollapse.Click += (_, _) => SetNavCollapsed(!_navCollapsed);
        NavTree.ContextRequested += OnNavContextRequested;
        // 拖动排序：只有顶层的内置项和固定的艺术家可以拖（同移动端 T-5）
        NavTree.AddHandler(PointerPressedEvent, OnNavPointerPressed, RoutingStrategies.Tunnel);
        NavTree.AddHandler(PointerMovedEvent, OnNavPointerMoved, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(NavTree, true);
        NavTree.AddHandler(DragDrop.DragOverEvent, OnNavDragOver);
        NavTree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropTarget());
        NavTree.AddHandler(DragDrop.DropEvent, OnNavDrop);

        // 从表格拖出歌曲
        Table.AddHandler(PointerPressedEvent, OnTablePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Table.AddHandler(PointerMovedEvent, OnTablePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(Table, true);
        Table.AddHandler(DragDrop.DragOverEvent, OnTableDragOver);
        Table.AddHandler(DragDrop.DropEvent, OnTableDrop);
    }

    void SetNavCollapsed(bool collapsed)
    {
        if (collapsed == _navCollapsed) return;
        if (collapsed) _navWidth = MainArea.ColumnDefinitions[0].ActualWidth;
        _navCollapsed = collapsed;
        NavTree.Classes.Set("collapsed", collapsed);
        MainArea.ColumnDefinitions[0].Width = new GridLength(collapsed ? 56 : Math.Clamp(_navWidth, 160, 480));
        NavSplitter.IsVisible = !collapsed;
    }

    static NavNode? NodeOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as NavNode;

    // ── 右键菜单 ──

    void OnNavContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (NodeOf(e.Source) is not { } node) return;
        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        switch (node.Kind)
        {
            case NavKind.Folder:
                var source = new QueueSource.Folder(node.FolderId, node.Dir, true);
                Add(L.T("播放全部（含子文件夹）"), () => PlaySource(source, node.Title, shuffle: false));
                Add(L.T("随机播放（含子文件夹）"), () => PlaySource(source, node.Title, shuffle: true));
                Add(L.T("全部添加到队列"), () => _app.Player.AddToQueue(ListBuilder.Tracks(_app.Library.Snapshot, source, _app.Settings.Current, _app.Playlists).Select(t => t.Id).ToList()));
                Add(L.T("在资源管理器中打开"), () => OpenFolder(node));
                if (node.IsFolderRoot)
                {
                    menu.Items.Add(new Separator());
                    Add(L.T("移除音乐文件夹…"), async () =>
                    {
                        var f = _app.Library.Snapshot.Folder(node.FolderId);
                        if (f is not null && await Dialogs.Confirm(this, L.T("移除音乐文件夹"), L.F($"移除「{f.Root.TrimEnd('\\')}」？其中的歌会从曲库消失，播放列表里的条目保留并显示为不可用。文件不会被删除。"), L.T("移除"), danger: true))
                            await _app.Library.RemoveFolderAsync(f.Id);
                    });
                }
                break;
            case NavKind.Folders:
                Add(L.T("添加音乐文件夹…"), () => _ = AddFolder());
                Add(L.T("重新扫描"), () => { _ = _app.Library.RescanAsync(false); _vm.ShowToast(L.T("正在重新扫描")); });
                break;
            case NavKind.Playlists:
                Add(L.T("新建播放列表…"), async () =>
                {
                    if (await Dialogs.Prompt(this, L.T("新建播放列表"), L.T("名称"), L.T("新建播放列表")) is { } name) _app.Playlists.Create(name);
                });
                AddPlaylistsExtras(menu);
                break;
            case NavKind.Playlist:
                Add(L.T("播放"), () => PlaySource(new QueueSource.Playlist(node.PlaylistId), node.Title, shuffle: false));
                Add(L.T("随机播放"), () => PlaySource(new QueueSource.Playlist(node.PlaylistId), node.Title, shuffle: true));
                Add(L.T("重命名…"), async () =>
                {
                    if (await Dialogs.Prompt(this, L.T("重命名播放列表"), L.T("名称"), node.Title) is { } name) _app.Playlists.Rename(node.PlaylistId, name);
                });
                AddPlaylistNodeExtras(menu, node.PlaylistId);
                Add(L.T("删除…"), async () =>
                {
                    if (await Dialogs.Confirm(this, L.T("删除播放列表"), L.F($"删除播放列表「{node.Title}」？歌曲文件不会被删除。"), L.T("删除"), danger: true))
                        _app.Playlists.Delete(node.PlaylistId);
                });
                break;
            case NavKind.PinnedArtist:
                Add(L.T("播放全部"), () => PlaySource(new QueueSource.Artist(node.ArtistKey), node.Title, shuffle: false));
                Add(L.T("取消固定"), () => TogglePin(node.ArtistKey));
                break;
        }
        if (node.IsTopLevel)
        {
            menu.Items.Add(new Separator());
            var order = _vm.NavOrder();
            var index = order.FindIndex(i => i.Type == node.Item!.Type && i.Ref == node.Item.Ref);
            Add(L.T("上移"), () => MoveNav(index, index - 1), index > 0);
            Add(L.T("下移"), () => MoveNav(index, index + 1), index >= 0 && index < order.Count - 1);
        }
        e.Handled = true;
        menu.Open(NavTree);
    }

    /// <summary>播放列表的导入导出（m3u，需求 PL-4）。</summary>
    partial void AddPlaylistsExtras(ContextMenu menu);
    partial void AddPlaylistNodeExtras(ContextMenu menu, long playlistId);

    void PlaySource(QueueSource source, string title, bool shuffle)
    {
        var list = ListBuilder.Tracks(_app.Library.Snapshot, source, _app.Settings.Current, _app.Playlists);
        if (list.Count == 0) return;
        _app.Player.Play(new QueueContext(source, title), list.Select(t => t.Id).ToList(), shuffle ? null : 0, shuffle);
    }

    void OpenFolder(NavNode node)
    {
        if (_app.Library.Snapshot.Folder(node.FolderId) is not { Available: true } f) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f.Root + node.Dir) { UseShellExecute = true });
    }

    void MoveNav(int from, int to)
    {
        var order = _vm.NavOrder();
        if (from < 0 || to < 0 || from >= order.Count || to >= order.Count) return;
        var item = order[from];
        order.RemoveAt(from);
        order.Insert(to, item);
        _app.Db.SetNavItems(order);
        NavOrderChanged?.Invoke();
        _vm.Refresh();
    }

    // ── 导航栏拖动排序 ──

    PointerPressedEventArgs? _navPress;
    Point _navPressPoint;

    void OnNavPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _navPress = e.GetCurrentPoint(NavTree).Properties.IsLeftButtonPressed && NodeOf(e.Source) is { IsTopLevel: true } ? e : null;
        _navPressPoint = e.GetPosition(NavTree);
    }

    async void OnNavPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_navPress is null || !e.GetCurrentPoint(NavTree).Properties.IsLeftButtonPressed) { _navPress = null; return; }
        if (Point.Distance(e.GetPosition(NavTree), _navPressPoint) < 8) return;
        var press = _navPress;
        _navPress = null;
        if (NodeOf(press.Source) is not { Item: { } item }) return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(NavFormat, item.Type + "\u001F" + item.Ref));
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        ClearDropTarget();
    }

    TreeViewItem? _dropTarget;

    void SetDropTarget(TreeViewItem? item)
    {
        if (_dropTarget == item) return;
        _dropTarget?.Classes.Set("droptarget", false);
        _dropTarget = item;
        _dropTarget?.Classes.Set("droptarget", true);
    }

    void ClearDropTarget() => SetDropTarget(null);

    void OnNavDragOver(object? sender, DragEventArgs e)
    {
        var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        var node = tvi?.DataContext as NavNode;
        if (e.DataTransfer.Contains(TracksFormat) && node is { Kind: NavKind.Playlist })
        {
            e.DragEffects = DragDropEffects.Copy;
            SetDropTarget(tvi);
        }
        else if (e.DataTransfer.Contains(NavFormat) && node is { IsTopLevel: true })
        {
            e.DragEffects = DragDropEffects.Move;
            SetDropTarget(tvi);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropTarget();
        }
        e.Handled = true;
    }

    void OnNavDrop(object? sender, DragEventArgs e)
    {
        ClearDropTarget();
        var node = NodeOf(e.Source);
        if (node is { Kind: NavKind.Playlist } && e.DataTransfer.TryGetValue(TracksFormat) is { } ids)
        {
            var snapshot = _app.Library.Snapshot;
            var tracks = ids.Split(',').Select(s => long.TryParse(s, out var id) ? snapshot.ById.GetValueOrDefault(id) : null).OfType<Track>().ToList();
            var n = _app.Playlists.Add(node.PlaylistId, tracks);
            _vm.ShowToast(L.F($"已添加 {n} 首到「{node.Title}」"));
        }
        else if (node is { Item: { } target } && e.DataTransfer.TryGetValue(NavFormat) is { } moving)
        {
            var parts = moving.Split('\u001F');
            var order = _vm.NavOrder();
            var from = order.FindIndex(i => i.Type == parts[0] && (i.Ref ?? "") == parts[1]);
            var to = order.FindIndex(i => i.Type == target.Type && i.Ref == target.Ref);
            MoveNav(from, to);
        }
        e.Handled = true;
    }

    // ── 从表格拖出歌曲；播放列表里拖动调整顺序 ──

    PointerPressedEventArgs? _tablePress;
    Point _tablePressPoint;

    void OnTablePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);
        // 只有按在已经选中的行上才开始拖动（不影响点一下选中、Shift 多选）
        _tablePress = e.GetCurrentPoint(Table).Properties.IsLeftButtonPressed && row?.DataContext is SongRow r && Table.SelectedItems.Contains(r) ? e : null;
        _tablePressPoint = e.GetPosition(Table);
    }

    async void OnTablePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tablePress is null || !e.GetCurrentPoint(Table).Properties.IsLeftButtonPressed) { _tablePress = null; return; }
        if (Point.Distance(e.GetPosition(Table), _tablePressPoint) < 10) return;
        var press = _tablePress;
        _tablePress = null;
        var rows = Table.SelectedItems.OfType<SongRow>().Where(r => r.Track is not null).ToList();
        if (rows.Count == 0) return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(TracksFormat, string.Join(',', rows.Select(r => r.Track!.Id))));
        data.Add(DataTransferItem.CreateText(string.Join(Environment.NewLine, rows.Select(r => _app.Library.PathOf(r.Track!) ?? r.Title))));
        _draggingPositions = _vm.IsPlaylistView ? rows.Select(r => r.Position).ToList() : null;
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy | DragDropEffects.Move);
        _draggingPositions = null;
    }

    List<int>? _draggingPositions;

    /// <summary>播放列表按文件名（手动顺序）排序时可以拖动调整顺序（同移动端 U-8）。</summary>
    bool CanReorderPlaylist => _vm.IsPlaylistView && _vm.Sort == SongSort.FileName && !_vm.SortDescending && !_vm.FilterActive;

    void OnTableDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _draggingPositions is not null && CanReorderPlaylist && e.DataTransfer.Contains(TracksFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void OnTableDrop(object? sender, DragEventArgs e)
    {
        if (_draggingPositions is null || !CanReorderPlaylist || _vm.Current is not NavTarget.Playlist p) return;
        var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);
        var target = row?.DataContext is SongRow r ? r.Position : int.MaxValue;
        // 放在目标行的下半部分时插到它后面
        if (row is not null && e.GetPosition(row).Y > row.Bounds.Height / 2) target++;
        _app.Playlists.Move(p.PlaylistId, _draggingPositions, Math.Min(target, int.MaxValue - 1));
        e.Handled = true;
    }
}
