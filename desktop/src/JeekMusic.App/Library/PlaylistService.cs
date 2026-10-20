using Avalonia.Threading;

namespace JeekMusic.Library;

/// <summary>播放列表里的一行：条目和它对应的歌（对不上的为 null，显示为不可用，需求 PL-3）。</summary>
public sealed record PlaylistEntry(PlaylistItem Item, Track? Track);

/// <summary>
/// 播放列表（需求 PL-1 ~ PL-4，§3.7）。数据在数据库里；改动后触发 <see cref="Changed"/>（UI 线程），同步服务据此写同步文件。
/// 条目引用 track_key：文件被删除或移动后条目保留，显示为不可用；文件恢复后自动恢复（需求 PL-3）。
/// </summary>
public sealed class PlaylistService(LibraryService library)
{
    readonly LibraryDb _db = library.Db;
    List<Playlist>? _cache;

    /// <summary>播放列表变了（新建、改名、删除、条目变化）；参数是变化的播放列表 id，整体变化时为 null。</summary>
    public event Action<long?>? Changed;

    public IReadOnlyList<Playlist> All => _cache ??= _db.Playlists();

    public Playlist? Get(long id) => All.FirstOrDefault(p => p.Id == id);

    public void Invalidate(long? id = null)
    {
        _cache = null;
        if (Dispatcher.UIThread.CheckAccess()) Changed?.Invoke(id);
        else Dispatcher.UIThread.Post(() => Changed?.Invoke(id));
    }

    public IReadOnlyList<PlaylistEntry> Entries(long playlistId)
    {
        var snapshot = library.Snapshot;
        return _db.PlaylistItems(playlistId).Select(i => new PlaylistEntry(i, i.ForeignRef is null ? snapshot.ByKey.GetValueOrDefault(i.TrackKey) : null)).ToList();
    }

    /// <summary>新建；同名时自动加序号（"跑步 (2)"）。</summary>
    public long Create(string name)
    {
        name = UniqueName(name.Trim());
        var id = _db.CreatePlaylist(name);
        Invalidate();
        return id;
    }

    public string UniqueName(string name, long? except = null)
    {
        var names = All.Where(p => p.Id != except).Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!names.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    public void Rename(long id, string name)
    {
        _db.RenamePlaylist(id, UniqueName(name.Trim(), id));
        Invalidate(id);
    }

    public void Delete(long id)
    {
        _db.DeletePlaylist(id);
        Invalidate();
    }

    /// <summary>添加歌曲（已经在列表里的也照样加，和移动端一致）。返回添加的数量。</summary>
    public int Add(long id, IEnumerable<Track> tracks)
    {
        var items = Current(id);
        var added = 0;
        foreach (var t in tracks) { items.Add((t.TrackKey, null)); added++; }
        _db.SetPlaylistItems(id, items);
        Invalidate(id);
        return added;
    }

    /// <summary>从播放列表移除（只去掉条目，不动文件，需求 F-10）。</summary>
    public void Remove(long id, IEnumerable<int> positions)
    {
        var set = positions.ToHashSet();
        var items = Current(id).Where((_, i) => !set.Contains(i)).ToList();
        _db.SetPlaylistItems(id, items);
        Invalidate(id);
    }

    /// <summary>拖动调整顺序：把 <paramref name="positions"/> 这些条目移到 <paramref name="target"/> 之前（target 是移动前的位置）。</summary>
    public void Move(long id, IReadOnlyList<int> positions, int target)
    {
        var items = Current(id);
        var set = positions.ToHashSet();
        var moving = items.Where((_, i) => set.Contains(i)).ToList();
        var before = items.Take(target).Where((_, i) => !set.Contains(i)).Count();
        var rest = items.Where((_, i) => !set.Contains(i)).ToList();
        rest.InsertRange(Math.Clamp(before, 0, rest.Count), moving);
        if (rest.SequenceEqual(items)) return;
        _db.SetPlaylistItems(id, rest);
        Invalidate(id);
    }

    List<(string, string?)> Current(long id) => _db.PlaylistItems(id).Select(i => (i.TrackKey, i.ForeignRef)).ToList();
}
