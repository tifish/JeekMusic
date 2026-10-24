using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JeekMusic.Core.Library;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Settings;

namespace JeekMusic.UI.ViewModels;

/// <summary>内容区显示的东西（导航历史里的一项）。</summary>
public abstract record NavTarget
{
    public sealed record Folder(long FolderId, string Dir) : NavTarget;
    public sealed record Artists(string? ArtistKey) : NavTarget;
    public sealed record PinnedArtist(string ArtistKey) : NavTarget;
    public sealed record Playlist(long PlaylistId) : NavTarget;
    public sealed record Album(string AlbumKey) : NavTarget;
    public sealed record Search(string Query) : NavTarget;
}

public enum ContentKind { Welcome, Songs, Hint }

/// <summary>
/// 主窗口（需求 §3.2 ~ §3.4，设计 §6）：导航栏、艺术家栏、歌曲表格、搜索、筛选、排序、导航历史。
/// 列表的计算在后台线程上（设计 §6.2），新的计算开始时取消上一次。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    readonly AppServices _app;
    readonly LibraryService _library;
    readonly SettingsStore _settings;
    readonly PlaylistService _playlists;
    CancellationTokenSource? _computeCts;
    readonly Stack<NavTarget> _back = new();
    readonly Stack<NavTarget> _forward = new();
    bool _navigating;
    bool _firstContentLogged;

    public PlayerViewModel Player { get; }
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _albumCover;

    public MainViewModel(AppServices app)
    {
        _app = app;
        _library = app.Library;
        _settings = app.Settings;
        _playlists = app.Playlists;
        Player = new PlayerViewModel(app.Player, app.SleepTimer, app.Settings);
        _library.SnapshotChanged += OnSnapshotChanged;
        _library.ScanProgress += (done, total) => ScanText = total == 0 ? "" : L.F($"正在扫描 {done:N0} / {total:N0}");
        _playlists.Changed += OnPlaylistsChanged;
        app.Player.TrackChanged += _ => MarkPlaying();
        app.Player.Error += ShowToast;
        _includeSubfolders = _settings.Current.IncludeSubfolders;
    }

    LibrarySnapshot Snapshot => _library.Snapshot;
    AppSettings S => _settings.Current;

    // ── 导航栏 ──

    public ObservableCollection<NavNode> Nav { get; } = new();

    [ObservableProperty] private NavNode? _selectedNav;

    partial void OnSelectedNavChanged(NavNode? value)
    {
        if (value is null || _navigating) return;
        switch (value.Kind)
        {
            case NavKind.Folders:
                if (value.Children.FirstOrDefault() is { } first) SelectedNav = first;
                break;
            case NavKind.Folder: Navigate(new NavTarget.Folder(value.FolderId, value.Dir)); break;
            case NavKind.Artists: Navigate(new NavTarget.Artists(SelectedArtist?.NameKey)); break;
            case NavKind.Playlists:
                value.IsExpanded = true;
                if (value.Children.FirstOrDefault() is { } p) SelectedNav = p;
                else Show(ContentKind.Hint, L.T("播放列表"), L.T("还没有播放列表。在歌曲上点右键，选择\"添加到播放列表\" → \"新建播放列表\"。"));
                break;
            case NavKind.Playlist: Navigate(new NavTarget.Playlist(value.PlaylistId)); break;
            case NavKind.PinnedArtist: Navigate(new NavTarget.PinnedArtist(value.ArtistKey)); break;
        }
    }

    /// <summary>导航顺序：nav_item 表；没有时用默认值（文件夹、艺术家、播放列表；同移动端 T-2、T-3）。</summary>
    public List<NavItem> NavOrder()
    {
        var items = _library.Db.NavItems();
        if (items.Count == 0) items = [new("FOLDERS", null), new("ARTISTS", null), new("PLAYLISTS", null)];
        foreach (var builtin in new[] { "FOLDERS", "ARTISTS", "PLAYLISTS" })
            if (items.All(i => i.Type != builtin)) items.Add(new(builtin, null));
        return items;
    }

    void RebuildNav()
    {
        var selectedKey = SelectedNav?.Key;
        var expanded = Nav.SelectMany(Flatten).Where(n => n.Kind is not (NavKind.Folders or NavKind.Folder) && n.IsExpanded).Select(n => n.Key).ToHashSet();
        var filter = ListBuilder.FilterOf(Snapshot, S.TagFilter);
        _navigating = true;
        try
        {
            Nav.Clear();
            foreach (var item in NavOrder())
            {
                switch (item.Type)
                {
                    case "FOLDERS":
                        var folders = new NavNode { Kind = NavKind.Folders, Title = L.T("文件夹"), Icon = Icons.Folder, Item = item };
                        foreach (var root in Snapshot.Roots)
                            if (FolderNode(root, filter) is { } n) folders.Children.Add(n);
                        Nav.Add(folders);
                        break;
                    case "ARTISTS":
                        Nav.Add(new NavNode { Kind = NavKind.Artists, Title = L.T("艺术家"), Icon = Icons.Artist, Item = item });
                        break;
                    case "PLAYLISTS":
                        var lists = new NavNode { Kind = NavKind.Playlists, Title = L.T("播放列表"), Icon = Icons.Playlist, Item = item };
                        foreach (var p in _playlists.All)
                            lists.Children.Add(new NavNode { Kind = NavKind.Playlist, Title = p.Name, Icon = Icons.Playlist, PlaylistId = p.Id });
                        Nav.Add(lists);
                        break;
                    case "ARTIST" when item.Ref is { } key:
                        var name = Snapshot.ArtistByKey.TryGetValue(key, out var a) ? a.Name : key;
                        Nav.Add(new NavNode { Kind = NavKind.PinnedArtist, Title = name, Icon = Icons.Pin, ArtistKey = key, Item = item });
                        break;
                }
            }
            foreach (var n in Nav.SelectMany(Flatten))
            {
                _app.FolderExpansion.Apply(n);
                if (expanded.Contains(n.Key)) n.IsExpanded = true;
            }
            SelectedNav = selectedKey is null ? null : Nav.SelectMany(Flatten).FirstOrDefault(n => n.Key == selectedKey);
        }
        finally { _navigating = false; }
        RefreshArtistPin();
    }

    static IEnumerable<NavNode> Flatten(NavNode n) => n.Children.SelectMany(Flatten).Prepend(n);

    /// <summary>文件夹树：只显示含有音乐的子文件夹（需求 F-3）；筛选时没有这类歌的不显示，歌曲数只算这类歌（需求 F-9、M-6）。</summary>
    NavNode? FolderNode(DirNode dir, Func<Track, bool>? filter)
    {
        var count = filter is null ? dir.AllTracks().Count() : dir.AllTracks().Count(filter);
        if (count == 0 && dir.Dir.Length > 0) return null;
        var node = new NavNode
        {
            Kind = NavKind.Folder, Title = dir.Name, Icon = dir.Dir.Length == 0 ? Icons.Music : Icons.Folder,
            FolderId = dir.Folder.Id, Dir = dir.Dir, CountText = count.ToString("N0"),
            FolderExpansionKey = FolderExpansionState.DirectoryKey(dir.Folder, dir.Dir),
        };
        foreach (var c in dir.Children)
            if (FolderNode(c, filter) is { } child) node.Children.Add(child);
        return node;
    }

    /// <summary>选中导航栏里对应的项（导航历史、"查看艺术家"等跳转时）。</summary>
    void SelectNavFor(NavTarget target, bool revealFolderPath = false)
    {
        var key = target switch
        {
            NavTarget.Folder f => $"folder:{f.FolderId}:{f.Dir}",
            NavTarget.Playlist p => $"playlist:{p.PlaylistId}",
            NavTarget.PinnedArtist a => $"artist:{a.ArtistKey}",
            NavTarget.Artists => nameof(NavKind.Artists),
            _ => null,
        };
        if (key is null) return;
        var node = Nav.SelectMany(Flatten).FirstOrDefault(n => n.Key == key);
        if (node is null) return;
        // 普通选中/恢复保留文件夹收起偏好；显式定位只临时显示路径。
        foreach (var root in Nav) ExpandPath(root, node, revealFolderPath);
        _navigating = true;
        try { SelectedNav = node; } finally { _navigating = false; }
    }

    static bool ExpandPath(NavNode current, NavNode target, bool revealFolderPath)
    {
        if (current == target) return true;
        foreach (var c in current.Children)
            if (ExpandPath(c, target, revealFolderPath))
            {
                if (revealFolderPath || current.Kind is not (NavKind.Folders or NavKind.Folder)) current.IsExpanded = true;
                return true;
            }
        return false;
    }

    internal void ApplyFolderExpansion()
    {
        foreach (var node in Nav.SelectMany(Flatten)) _app.FolderExpansion.Apply(node);
    }

    internal void RevealCurrentFolderPath()
    {
        if (Current is NavTarget.Folder) SelectNavFor(Current, revealFolderPath: true);
    }

    // ── 艺术家栏（需求 W-4）──

    [ObservableProperty] private bool _showArtistPane;
    [ObservableProperty] private IReadOnlyList<ArtistInfo> _artists = [];
    [ObservableProperty] private ArtistInfo? _selectedArtist;

    partial void OnSelectedArtistChanged(ArtistInfo? value)
    {
        if (value is not null && !_navigating && Current is NavTarget.Artists) Navigate(new NavTarget.Artists(value.NameKey));
    }

    void RefreshArtists()
    {
        var filter = ListBuilder.FilterOf(Snapshot, S.TagFilter);
        // 筛选时没有这类歌的艺术家不显示（需求 M-6）
        // Keep snapshot SortKey order within each group; symbols/digits and unknown share one final # group.
        Artists = Snapshot.Artists.Where(a => filter is null || a.Tracks.Any(filter))
            .OrderBy(a => a.IndexLetter == "#" ? "[" : a.IndexLetter, StringComparer.Ordinal).ToList();
    }

    // ── 内容区 ──

    [ObservableProperty] private ContentKind _contentKind = ContentKind.Welcome;

    public bool IsWelcome => ContentKind == ContentKind.Welcome;
    public bool IsSongs => ContentKind == ContentKind.Songs;
    public bool IsHint => ContentKind == ContentKind.Hint;

    partial void OnContentKindChanged(ContentKind value)
    {
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsSongs));
        OnPropertyChanged(nameof(IsHint));
    }
    [ObservableProperty] private string _contentTitle = "";
    [ObservableProperty] private string _contentSubtitle = "";
    [ObservableProperty] private string _hintText = "";
    [ObservableProperty] private IReadOnlyList<SongRow> _rows = [];
    [ObservableProperty] private bool _isPlaylistView;
    [ObservableProperty] private bool _isFolderView;
    [ObservableProperty] private bool _isAlbumView;
    [ObservableProperty] private string _albumInfo = "";
    [ObservableProperty] private IReadOnlyList<ArtistInfo> _searchArtists = [];
    [ObservableProperty] private string _scanText = "";

    public NavTarget? Current { get; private set; }
    public QueueSource? CurrentSource { get; private set; }
    internal int RowsGeneration { get; private set; }
    internal bool ComputingRows { get; private set; }
    internal event Action? RowsInvalidated;

    public string? CurrentArtistKey => Current switch
    {
        NavTarget.Artists { ArtistKey: { Length: > 0 } key } => key,
        NavTarget.PinnedArtist { ArtistKey: { Length: > 0 } key } => key,
        _ => null,
    };
    public bool CanPinArtist => CurrentArtistKey is not null;
    public bool IsArtistPinned => CurrentArtistKey is { } key && NavOrder().Any(i => i.Type == "ARTIST" && i.Ref == key);
    public string ArtistPinIcon => IsArtistPinned ? Icons.Unpin : Icons.Pin;
    public string ArtistPinText => IsArtistPinned ? L.T("取消固定") : L.T("固定到导航栏");

    void RefreshArtistPin()
    {
        OnPropertyChanged(nameof(CurrentArtistKey));
        OnPropertyChanged(nameof(CanPinArtist));
        OnPropertyChanged(nameof(IsArtistPinned));
        OnPropertyChanged(nameof(ArtistPinIcon));
        OnPropertyChanged(nameof(ArtistPinText));
    }

    /// <summary>"包含子文件夹"开关（需求 W-3）。</summary>
    private bool _includeSubfolders;
    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (!SetProperty(ref _includeSubfolders, value)) return;
            S.IncludeSubfolders = value;
            _settings.Save("view");
            Recompute();
        }
    }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>切换内容区；<paramref name="history"/> 为 false 时不记入历史（后退、前进、刷新）。</summary>
    public void Navigate(NavTarget target, bool history = true)
    {
        if (history && Current is not null && Current != target)
        {
            _back.Push(Current);
            _forward.Clear();
        }
        Current = target;
        RefreshArtistPin();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        ShowArtistPane = target is NavTarget.Artists;
        if (target is NavTarget.Artists { ArtistKey: { } key } && SelectedArtist?.NameKey != key)
        {
            _navigating = true;
            try { SelectedArtist = Artists.FirstOrDefault(a => a.NameKey == key); } finally { _navigating = false; }
        }
        if (target is not NavTarget.Search && SearchText.Length > 0)
        {
            _navigating = true;
            try { SearchText = ""; } finally { _navigating = false; }
        }
        SelectNavFor(target);
        Recompute();
        Navigated?.Invoke();
    }

    /// <summary>换了内容（表格要回到顶部或恢复位置）。</summary>
    public event Action? Navigated;

    [RelayCommand]
    void GoBack()
    {
        if (_back.Count == 0) return;
        if (Current is not null) _forward.Push(Current);
        Navigate(_back.Pop(), history: false);
    }

    [RelayCommand]
    void GoForward()
    {
        if (_forward.Count == 0) return;
        if (Current is not null) _back.Push(Current);
        Navigate(_forward.Pop(), history: false);
    }

    void Show(ContentKind kind, string title, string hint)
    {
        ContentKind = kind;
        ContentTitle = title;
        HintText = hint;
        ContentSubtitle = "";
        Rows = [];
        CurrentSource = null;
    }

    /// <summary>重算当前列表（来源、筛选、排序、曲库变了）。</summary>
    public void Recompute()
    {
        _computeCts?.Cancel();
        RowsGeneration++;
        ComputingRows = false;
        RowsInvalidated?.Invoke();
        if (Snapshot.Folders.Count == 0) { Show(ContentKind.Welcome, "", ""); return; }
        var target = Current;
        if (target is null) return;
        IsPlaylistView = target is NavTarget.Playlist;
        IsFolderView = target is NavTarget.Folder;
        IsAlbumView = target is NavTarget.Album;
        QueueSource? source;
        string title;
        switch (target)
        {
            case NavTarget.Folder f:
                var node = Snapshot.Node(f.FolderId, f.Dir);
                title = node is null ? L.T("文件夹") : (f.Dir.Length == 0 ? node.Name : node.Folder.DisplayName + "\\" + f.Dir.TrimEnd('\\'));
                source = new QueueSource.Folder(f.FolderId, f.Dir, IncludeSubfolders);
                break;
            case NavTarget.Artists a when a.ArtistKey is { } key:
                title = Snapshot.ArtistByKey.TryGetValue(key, out var artist) ? (artist.IsUnknown ? Text.UnknownArtist : artist.Name) : key;
                source = new QueueSource.Artist(key);
                break;
            case NavTarget.Artists:
                Show(ContentKind.Hint, L.T("艺术家"), L.T("选择一个艺术家"));
                return;
            case NavTarget.PinnedArtist pa:
                title = Snapshot.ArtistByKey.TryGetValue(pa.ArtistKey, out var pinned) ? pinned.Name : pa.ArtistKey;
                source = new QueueSource.Artist(pa.ArtistKey);
                break;
            case NavTarget.Playlist p:
                title = _playlists.Get(p.PlaylistId)?.Name ?? L.T("播放列表");
                source = new QueueSource.Playlist(p.PlaylistId);
                break;
            case NavTarget.Album al:
                var tracks = Snapshot.Albums.GetValueOrDefault(al.AlbumKey);
                var first = tracks?.FirstOrDefault();
                title = first?.Album ?? L.T("专辑");
                AlbumInfo = AlbumHeader(tracks);
                source = new QueueSource.Album(al.AlbumKey);
                break;
            case NavTarget.Search s:
                title = L.F($"搜索：{s.Query}");
                SearchArtists = SearchIndex.Artists(Snapshot, s.Query).Take(20).ToList();
                source = new QueueSource.Search(s.Query);
                break;
            default:
                return;
        }
        if (target is not NavTarget.Search) SearchArtists = [];
        ContentKind = ContentKind.Songs;
        ContentTitle = title;
        CurrentSource = source;
        var cts = _computeCts = new CancellationTokenSource();
        ComputingRows = true;
        var snapshot = Snapshot;
        var settings = S;
        var playlistId = (target as NavTarget.Playlist)?.PlaylistId;
        var isSearch = target is NavTarget.Search;
        _ = Task.Run(() =>
        {
            var list = ListBuilder.Tracks(snapshot, source, settings, _playlists);
            if (isSearch && list.Count > 200) list = list.Take(200).ToList(); // 需求 S-1：最多 200 首
            if (cts.IsCancellationRequested) return;
            var rows = BuildRows(snapshot, list, playlistId);
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                Rows = rows;
                ComputingRows = false;
                if (!_firstContentLogged && rows.Count > 0)
                {
                    _firstContentLogged = true;
                    Infrastructure.Log.Info("startup", $"{rows.Count} rows ready after {(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds:F0} ms");
                }
                var available = rows.Count(r => r.Available);
                ContentSubtitle = Text.Count(available) + (rows.Count > available ? L.F($"（另有 {rows.Count - available} 首不可用）") : "")
                                  + (isSearch && list.Count >= 200 ? L.T("，只显示前 200 首") : "");
                MarkPlaying();
                // 播放队列跟着来源列表走（需求 P-15）
                if (_app.Player.Queue.Context?.Source == source) _app.Player.Queue.SourceChanged(list.Select(t => t.Id).ToList());
                RowsChanged?.Invoke();
            });
        }, cts.Token);
    }

    /// <summary>表格内容换了。</summary>
    public event Action? RowsChanged;

    List<SongRow> BuildRows(LibrarySnapshot snapshot, List<Track> list, long? playlistId)
    {
        var rows = new List<SongRow>(list.Count);
        if (playlistId is { } pid)
        {
            // 播放列表：按条目顺序（含不可用的），排序方式不是"文件名"时按排序后的顺序、不可用的排最后
            var entries = _playlists.Entries(pid);
            if (S.SortFor(orderedView: true) == SongSort.FileName && !S.SortDescending)
            {
                var allowed = list.Select(t => t.Id).ToHashSet();
                foreach (var e in entries)
                {
                    if (e.Track is { } t) { if (allowed.Contains(t.Id)) rows.Add(new SongRow(t, e.Item.Position, snapshot.Tags.GetValueOrDefault(t.TrackKey))); }
                    else if (S.TagFilter.Count == 0) rows.Add(new SongRow(null, e.Item.Position, null, e.Item.ForeignRef ?? e.Item.TrackKey, e.Item.TrackKey));
                }
                return rows;
            }
            var positions = new Dictionary<long, Queue<int>>();
            foreach (var e in entries.Where(e => e.Track is not null))
            {
                if (!positions.TryGetValue(e.Track!.Id, out var q)) positions[e.Track.Id] = q = new();
                q.Enqueue(e.Item.Position);
            }
            foreach (var t in list) rows.Add(new SongRow(t, positions.TryGetValue(t.Id, out var q) && q.Count > 0 ? q.Dequeue() : -1, snapshot.Tags.GetValueOrDefault(t.TrackKey)));
            if (S.TagFilter.Count == 0)
                foreach (var e in entries.Where(e => e.Track is null))
                    rows.Add(new SongRow(null, e.Item.Position, null, e.Item.ForeignRef ?? e.Item.TrackKey, e.Item.TrackKey));
            return rows;
        }
        for (var i = 0; i < list.Count; i++) rows.Add(new SongRow(list[i], i, snapshot.Tags.GetValueOrDefault(list[i].TrackKey)));
        return rows;
    }

    static string AlbumHeader(IReadOnlyList<Track>? tracks)
    {
        if (tracks is null || tracks.Count == 0) return "";
        var first = tracks[0];
        var artist = first.AlbumArtist ?? string.Join(" / ", tracks.SelectMany(t => t.Artists).Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
        var year = tracks.Select(t => t.Year).FirstOrDefault(y => y is not null);
        return string.Join(" · ", new[] { artist, year?.ToString(), Text.Count(tracks.Count) }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>正在播放的行高亮（需求 G-5）。</summary>
    void MarkPlaying()
    {
        var id = _app.Player.CurrentTrack?.Id;
        foreach (var r in Rows) r.IsPlaying = r.Track?.Id == id;
        PlayingChanged?.Invoke();
    }

    public event Action? PlayingChanged;

    // ── 搜索（需求 S-1、S-2）──

    [ObservableProperty] private string _searchText = "";
    readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    NavTarget? _beforeSearch;

    partial void OnSearchTextChanged(string value)
    {
        if (_navigating) return;
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnSearchTick;
        _searchDebounce.Tick += OnSearchTick;
        _searchDebounce.Start();
    }

    void OnSearchTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        var q = SearchText.Trim();
        if (q.Length == 0)
        {
            if (Current is NavTarget.Search && _beforeSearch is { } back) Navigate(back, history: false);
            return;
        }
        if (Current is not NavTarget.Search) _beforeSearch = Current;
        Navigate(new NavTarget.Search(q), history: Current is not NavTarget.Search);
    }

    // ── 智能标签筛选（需求 M-6）──

    public IReadOnlyList<string> TagFilter => S.TagFilter;
    public bool FilterActive => S.TagFilter.Count > 0;
    public string FilterText => FilterActive ? string.Join("·", S.TagFilter.Select(Text.TagName)) : L.T("筛选");

    /// <summary>点一下选中，再点一下取消；同一维度只能选一个。</summary>
    public void ToggleTag(string tag)
    {
        var dim = ListBuilder.DimensionOf(tag);
        var list = S.TagFilter.Where(t => ListBuilder.DimensionOf(t) != dim).ToList();
        if (!S.TagFilter.Contains(tag)) list.Add(tag);
        S.TagFilter = list.OrderBy(t => ListBuilder.DimensionOf(t) switch { "MOOD" => 0, "VOICE" => 1, _ => 2 }).ToList();
        _settings.Save("filter");
        FilterChanged();
    }

    [RelayCommand]
    void ClearFilter()
    {
        S.TagFilter = [];
        _settings.Save("filter");
        FilterChanged();
    }

    void FilterChanged()
    {
        OnPropertyChanged(nameof(TagFilter));
        OnPropertyChanged(nameof(FilterActive));
        OnPropertyChanged(nameof(FilterText));
        RebuildNav();
        RefreshArtists();
        Recompute();
    }

    // ── 排序（需求 G-2）──

    public SongSort Sort => S.SortFor(Current is NavTarget.Playlist or NavTarget.Album);
    public bool SortDescending => S.SortDescending;

    /// <summary>点表头：换成这一列排序；再点同一列反向。</summary>
    public void SetSort(SongSort sort)
    {
        S.SortDescending = Sort == sort && !S.SortDescending;
        S.SongSort = sort;
        _settings.Save("sort");
        OnPropertyChanged(nameof(Sort));
        OnPropertyChanged(nameof(SortDescending));
        Recompute();
    }

    // ── 曲库变化 ──

    void OnSnapshotChanged()
    {
        RefreshArtists();
        RebuildNav();
        if (Current is null && Snapshot.Folders.Count > 0) RestoreInitialView();
        else Recompute();
        // 预先建好搜索索引，第一次搜索不卡
        var snapshot = Snapshot;
        _ = Task.Run(() => SearchIndex.For(snapshot));
        if (_app.Player.Queue.Items.Count > 0) _app.Player.Queue.RemoveMissing(id => _app.Library.ById(id) is not null);
        SnapshotApplied?.Invoke();
    }

    public event Action? SnapshotApplied;

    void OnPlaylistsChanged(long? id)
    {
        RebuildNav();
        if (Current is NavTarget.Playlist p && (id is null || id == p.PlaylistId))
        {
            if (_playlists.Get(p.PlaylistId) is null) SelectedNav = Nav.FirstOrDefault();
            else Recompute();
        }
    }

    /// <summary>启动时恢复上次的视图（来自快照文件，需求 T-6、F-8），没有时选第一个音乐文件夹。</summary>
    public NavTarget? InitialTarget { get; set; }

    void RestoreInitialView()
    {
        var target = InitialTarget;
        InitialTarget = null;
        if (target is NavTarget.Folder f && Snapshot.Node(f.FolderId, f.Dir) is null) target = null;
        if (target is NavTarget.Playlist p && _playlists.Get(p.PlaylistId) is null) target = null;
        if (target is NavTarget.Search) target = null;
        target ??= Snapshot.Roots.FirstOrDefault() is { } r ? new NavTarget.Folder(r.Folder.Id, "") : null;
        if (target is not null) Navigate(target, history: false);
    }

    // ── 提示 ──

    [ObservableProperty] private string _toast = "";
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public void ShowToast(string message)
    {
        Toast = message;
        _toastTimer.Stop();
        _toastTimer.Tick -= HideToast;
        _toastTimer.Tick += HideToast;
        _toastTimer.Start();
    }

    void HideToast(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        Toast = "";
    }

    public void Refresh()
    {
        RebuildNav();
        RefreshArtists();
        Recompute();
    }
}
