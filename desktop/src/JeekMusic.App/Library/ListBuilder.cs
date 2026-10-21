using JeekMusic.Core.Queue;
using JeekMusic.Settings;

namespace JeekMusic.Library;

/// <summary>列表来自哪里（决定排序的特殊规则）。</summary>
public enum ListKind { Folder, FolderRecursive, Artist, Album, Playlist, Search }

/// <summary>
/// 歌曲列表的计算管线（设计 §6.2）：来源 → 智能标签筛选 → 排序。界面上的列表和播放队列的"来自列表的部分"用同一条管线，
/// 保证队列和看到的顺序一样（需求 P-15）。全部在内存里算。
/// </summary>
public static class ListBuilder
{
    /// <summary>智能标签筛选（需求 M-6）：每个维度选中的标签都要符合；没分析过的歌不符合任何筛选。</summary>
    public static Func<Track, bool>? FilterOf(LibrarySnapshot snapshot, IReadOnlyCollection<string> filter)
    {
        if (filter.Count == 0) return null;
        var wanted = filter.Select(t => (dimension: DimensionOf(t), tag: t)).Where(x => x.dimension is not null).ToList();
        if (wanted.Count == 0) return null;
        return t => snapshot.Tags.TryGetValue(t.TrackKey, out var tags) && wanted.All(w => tags[w.dimension!] == w.tag);
    }

    public static string? DimensionOf(string tag) => tag switch
    {
        "SPORT" or "NON_SPORT" => "MOOD",
        "VOCAL" or "INSTRUMENTAL" => "VOICE",
        "CLASSICAL" or "NON_CLASSICAL" => "GENRE",
        _ => null,
    };

    /// <summary>某个来源的歌（已筛选、已排序）。播放列表的不可用条目不在这里（它们不能播放）。</summary>
    public static List<Track> Tracks(LibrarySnapshot snapshot, QueueSource source, AppSettings settings, PlaylistService? playlists)
    {
        var filter = FilterOf(snapshot, settings.TagFilter);
        IEnumerable<Track> items;
        ListKind kind;
        switch (source)
        {
            case QueueSource.Folder f:
                var node = snapshot.Node(f.FolderId, f.Dir);
                items = node is null ? [] : f.Recursive ? node.AllTracks() : node.Tracks;
                kind = f.Recursive ? ListKind.FolderRecursive : ListKind.Folder;
                break;
            case QueueSource.Artist a:
                items = snapshot.ArtistByKey.TryGetValue(a.NameKey, out var artist) ? artist.Tracks : [];
                kind = ListKind.Artist;
                break;
            case QueueSource.Album al:
                items = snapshot.Albums.TryGetValue(al.AlbumKey, out var album) ? album : [];
                kind = ListKind.Album;
                break;
            case QueueSource.Playlist p:
                items = playlists?.Entries(p.PlaylistId).Where(e => e.Track is not null).Select(e => e.Track!) ?? [];
                kind = ListKind.Playlist;
                break;
            case QueueSource.Search s:
                items = Search(snapshot, s.Query);
                kind = ListKind.Search;
                break;
            default:
                return [];
        }
        if (filter is not null) items = items.Where(filter);
        var list = items.ToList();
        return Sort(list, kind, settings.SongSort, settings.SortDescending);
    }

    /// <summary>
    /// 排序（需求 U-8、G-2；规则同移动端设计 §5.8）。播放列表按文件名时保持手动顺序；专辑页按碟号、音轨号（需求 AL-3）。
    /// </summary>
    public static List<Track> Sort(List<Track> list, ListKind kind, SongSort sort, bool descending)
    {
        if (kind == ListKind.Playlist && sort == SongSort.FileName) return descending ? Enumerable.Reverse(list).ToList() : list;
        if (kind == ListKind.Album && sort == SongSort.FileName)
        {
            list.Sort((a, b) => (a.DiscNo ?? 1).CompareTo(b.DiscNo ?? 1) is var d && d != 0 ? d
                : (a.TrackNo ?? int.MaxValue).CompareTo(b.TrackNo ?? int.MaxValue) is var n && n != 0 ? n
                : SongOrder.CompareFile(a, b, kind));
            return list;
        }
        var albumKeys = sort == SongSort.Album ? list.Select(t => t.Album).OfType<string>().Distinct().ToDictionary(a => a, Core.Text.SortKeys.SortKey) : null;
        Comparison<Track> cmp = sort switch
        {
            SongSort.Modified => (a, b) => b.Mtime.CompareTo(a.Mtime) is var c && c != 0 ? c : SongOrder.CompareFile(a, b, kind),
            SongSort.Title => (a, b) => SongOrder.CompareKey(a.SortTitle, a.Title, b.SortTitle, b.Title) is var c && c != 0 ? c : SongOrder.CompareFile(a, b, kind),
            SongSort.Artist => (a, b) => SongOrder.CompareArtist(a, b) is var c && c != 0 ? c : SongOrder.CompareFile(a, b, kind),
            SongSort.Album => (a, b) => SongOrder.CompareKey(a.Album is { } aa ? albumKeys![aa] : "￿", a.Album ?? "", b.Album is { } ba ? albumKeys![ba] : "￿", b.Album ?? "") is var c && c != 0 ? c
                : (a.DiscNo ?? 1).CompareTo(b.DiscNo ?? 1) is var d && d != 0 ? d
                : (a.TrackNo ?? int.MaxValue).CompareTo(b.TrackNo ?? int.MaxValue) is var n && n != 0 ? n : SongOrder.CompareFile(a, b, kind),
            SongSort.Duration => (a, b) => a.DurationMs.CompareTo(b.DurationMs) is var c && c != 0 ? c : SongOrder.CompareFile(a, b, kind),
            SongSort.Format => (a, b) => string.CompareOrdinal(a.FormatLabel, b.FormatLabel) is var c && c != 0 ? c : SongOrder.CompareFile(a, b, kind),
            _ => (a, b) => SongOrder.CompareFile(a, b, kind),
        };
        list.Sort(descending ? (a, b) => cmp(b, a) : cmp);
        return list;
    }

    /// <summary>
    /// 全局搜索（需求 S-1、S-2）：标题、艺术家、专辑、文件名，支持拼音首字母（"zjl" → 周杰伦）。
    /// 按全局排序后取前 200 首（排序在调用方做，这里多取一些再截断由调用方决定）。
    /// </summary>
    public static IEnumerable<Track> Search(LibrarySnapshot snapshot, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return [];
        var index = SearchIndex.For(snapshot);
        return snapshot.Tracks.Where(t => index.Matches(t, q));
    }
}
