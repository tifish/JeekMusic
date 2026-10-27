using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using JeekMusic.Core.Library;
using JeekMusic.Library;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

/// <summary>歌曲的右键菜单（需求 G-4，内容同移动端 U-2）和列表的"更多"菜单（同移动端 U-12）。</summary>
public partial class MainWindow
{
    void OnTableContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var tracks = SelectedTracks();
        var rows = Table.SelectedItems.OfType<SongRow>().ToList();
        if (rows.Count == 0) return;
        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        var ids = tracks.Select(t => t.Id).ToList();
        if (tracks.Count > 0)
        {
            Add(L.T("播放"), () => PlayFrom(tracks[0]));
            Add(L.T("下一首播放"), () => { _app.Player.PlayNext(ids); _vm.ShowToast(L.F($"{tracks.Count} 首将在下一首播放")); });
            Add(L.T("添加到队列"), () => { _app.Player.AddToQueue(ids); _vm.ShowToast(L.F($"已添加 {tracks.Count} 首到队列")); });
            menu.Items.Add(PlaylistSubmenu(tracks));
            menu.Items.Add(new Separator());
        }
        if (tracks.Count == 1)
        {
            var t = tracks[0];
            Add(L.T("查看艺术家"), () => GoToArtist(t), t.Artists.Count > 0);
            var artists = t.Artists.Select(name => (Name: name, Key: ArtistSplitter.NameKey(name)))
                .Where(a => a.Key.Length > 0).DistinctBy(a => a.Key).ToList();
            if (artists.Count == 1) menu.Items.Add(ArtistPinItem(artists[0].Key));
            else if (artists.Count > 1)
            {
                var pin = new MenuItem { Header = L.T("固定到导航栏") };
                foreach (var artist in artists)
                {
                    var item = ArtistPinItem(artist.Key);
                    item.Header = artist.Name + " · " + item.Header;
                    pin.Items.Add(item);
                }
                menu.Items.Add(pin);
            }
            Add(L.T("查看专辑"), () => GoToAlbum(t), t.AlbumKey is not null);
            Add(L.T("在资源管理器中显示"), () => ShowInExplorer(t));
            menu.Items.Add(new Separator());
        }
        if (_vm.IsPlaylistView && _vm.Current is NavTarget.Playlist p)
            Add(L.T("从播放列表移除"), () => _app.Playlists.Remove(p.PlaylistId, rows.Select(r => r.Position)));
        AddSongActions(menu, tracks);
        e.Handled = true;
        menu.Open(Table);
    }

    /// <summary>智能标签修正、歌曲信息、在线歌词 / 封面、文件操作（各功能完成后填充）。</summary>
    partial void AddSongActions(ContextMenu menu, IReadOnlyList<Track> tracks);

    MenuItem PlaylistSubmenu(IReadOnlyList<Track> tracks)
    {
        var sub = new MenuItem { Header = L.T("添加到播放列表") };
        var create = new MenuItem { Header = L.T("新建播放列表…") };
        create.Click += async (_, _) =>
        {
            var name = await Dialogs.Prompt(this, L.T("新建播放列表"), L.T("名称"), L.T("新建播放列表"));
            if (name is null) return;
            var id = _app.Playlists.Create(name);
            _app.Playlists.Add(id, tracks);
            _vm.ShowToast(L.F($"已添加 {tracks.Count} 首到「{_app.Playlists.Get(id)?.Name}」"));
        };
        sub.Items.Add(create);
        if (_app.Playlists.All.Count > 0) sub.Items.Add(new Separator());
        foreach (var p in _app.Playlists.All)
        {
            var item = new MenuItem { Header = p.Name };
            item.Click += (_, _) =>
            {
                _app.Playlists.Add(p.Id, tracks);
                _vm.ShowToast(L.F($"已添加 {tracks.Count} 首到「{p.Name}」"));
            };
            sub.Items.Add(item);
        }
        return sub;
    }

    void ShowInExplorer(Track t)
    {
        if (_app.Library.PathOf(t) is not { } path) { _vm.ShowToast(L.T("文件所在的盘不可用")); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    /// <summary>列表标题栏的"更多"菜单（同移动端 U-12）。</summary>
    void ShowListMenu()
    {
        CreateListMenu().ShowAt(ListMoreButton);
    }

    internal MenuFlyout CreateListMenu()
    {
        var menu = new MenuFlyout();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        var any = _vm.Rows.Any(r => r.Available);
        Add(L.T("播放全部"), () => PlayAll(shuffle: false), any);
        Add(L.T("随机播放"), () => PlayAll(shuffle: true), any);
        if (_vm.IsFolderView && !_vm.IncludeSubfolders)
        {
            Add(L.T("播放全部（含子文件夹）"), () => PlayAll(shuffle: false, recursive: true));
            Add(L.T("随机播放（含子文件夹）"), () => PlayAll(shuffle: true, recursive: true));
        }
        Add(L.T("全部添加到队列"), () => _app.Player.AddToQueue(_vm.Rows.Where(r => r.Track is not null).Select(r => r.Track!.Id).ToList()), any);
        Add(L.T("全选"), () => Table.SelectAll(), any);
        switch (_vm.Current)
        {
            case NavTarget.Playlist p:
                menu.Items.Add(new Separator());
                Add(L.T("重命名播放列表…"), async () =>
                {
                    if (await Dialogs.Prompt(this, L.T("重命名播放列表"), L.T("名称"), _app.Playlists.Get(p.PlaylistId)?.Name ?? "") is { } name)
                        _app.Playlists.Rename(p.PlaylistId, name);
                });
                AddPlaylistExtras(menu, p.PlaylistId);
                Add(L.T("删除播放列表…"), async () =>
                {
                    var name = _app.Playlists.Get(p.PlaylistId)?.Name;
                    if (await Dialogs.Confirm(this, L.T("删除播放列表"), L.F($"删除播放列表「{name}」？歌曲文件不会被删除。"), L.T("删除"), danger: true))
                        _app.Playlists.Delete(p.PlaylistId);
                });
                break;
            case NavTarget.Artists { ArtistKey: { Length: > 0 } key }:
                menu.Items.Add(new Separator());
                AddPinItem(menu, key);
                break;
            case NavTarget.PinnedArtist pa:
                menu.Items.Add(new Separator());
                AddPinItem(menu, pa.ArtistKey);
                break;
        }
        AddListExtras(menu);
        return menu;
    }

    /// <summary>播放列表的导出 m3u 等（播放列表功能完成后填充）。</summary>
    partial void AddPlaylistExtras(MenuFlyout menu, long playlistId);

    /// <summary>"下载这些歌的封面 / 歌词"、"多选"等（各功能完成后填充）。</summary>
    partial void AddListExtras(MenuFlyout menu);

    /// <summary>固定到导航栏 / 取消固定（需求 W-2，同移动端 T-3）。</summary>
    void AddPinItem(MenuFlyout menu, string artistKey) => menu.Items.Add(ArtistPinItem(artistKey));

    MenuItem ArtistPinItem(string artistKey)
    {
        var order = _vm.NavOrder();
        var pinned = order.Any(i => i.Type == "ARTIST" && i.Ref == artistKey);
        var item = new MenuItem { Header = pinned ? L.T("取消固定") : L.T("固定到导航栏") };
        item.Click += (_, _) => TogglePin(artistKey);
        return item;
    }

    void OnArtistContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var artist = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ArtistInfo;
        if (artist is null && !e.TryGetPosition(ArtistList, out _)) artist = ArtistList.SelectedItem as ArtistInfo;
        if (artist is null || artist.NameKey.Length == 0) return;
        var menu = new ContextMenu();
        menu.Items.Add(ArtistPinItem(artist.NameKey));
        e.Handled = true;
        menu.Open(ArtistList);
    }

    public void TogglePin(string artistKey)
    {
        if (string.IsNullOrWhiteSpace(artistKey)) return;
        var order = _vm.NavOrder();
        var existing = order.FindIndex(i => i.Type == "ARTIST" && i.Ref == artistKey);
        if (existing >= 0)
        {
            order.RemoveAt(existing);
            _vm.ShowToast(L.T("已取消固定"));
        }
        else
        {
            // 新固定的放在"文件夹"之后（同移动端 T-3）
            var at = order.FindIndex(i => i.Type == "FOLDERS") + 1;
            while (at < order.Count && order[at].Type == "ARTIST") at++;
            order.Insert(at, new NavItem("ARTIST", artistKey));
            _vm.ShowToast(L.T("已固定到导航栏"));
        }
        _app.Db.SetNavItems(order);
        NavOrderChanged?.Invoke();
        _vm.Refresh();
    }

    /// <summary>导航顺序变了（同步服务据此写同步文件）。</summary>
    public event Action? NavOrderChanged;
}
