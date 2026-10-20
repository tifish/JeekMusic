using System.Runtime.CompilerServices;
using JeekMusic.Core.Text;

namespace JeekMusic.Library;

/// <summary>
/// 歌曲的比较规则（同移动端设计 §5.8）：排序键（中文转拼音）用自然排序比较，相同时再比原文；
/// 按文件名时，文件夹里 CUE 虚拟歌曲排在普通歌曲后面，"含子文件夹"时先按所在目录。"未知艺术家"排在最后。
/// </summary>
public static class SongOrder
{
    static readonly NaturalOrderComparer Natural = NaturalOrderComparer.Instance;

    public static int CompareKey(string keyA, string rawA, string keyB, string rawB)
    {
        var c = Natural.Compare(keyA, keyB);
        return c != 0 ? c : Natural.Compare(rawA, rawB);
    }

    public static int CompareFile(Track a, Track b, ListKind kind)
    {
        if (kind == ListKind.FolderRecursive)
        {
            var d = CompareDir(a, b);
            if (d != 0) return d;
        }
        if (kind is ListKind.Folder or ListKind.FolderRecursive && a.IsCue != b.IsCue) return a.IsCue ? 1 : -1;
        var c = CompareKey(a.SortFile, a.FileName, b.SortFile, b.FileName);
        if (c != 0) return c;
        c = (a.CueTrackNo ?? 0).CompareTo(b.CueTrackNo ?? 0);
        return c != 0 ? c : a.Id.CompareTo(b.Id);
    }

    public static int CompareArtist(Track a, Track b)
    {
        var ua = a.ArtistDisplay.Length == 0;
        var ub = b.ArtistDisplay.Length == 0;
        if (ua != ub) return ua ? 1 : -1;
        return CompareKey(a.SortArtist, a.ArtistDisplay, b.SortArtist, b.ArtistDisplay);
    }

    static int CompareDir(Track a, Track b)
    {
        if (a.FolderId != b.FolderId) return a.FolderId.CompareTo(b.FolderId);
        if (string.Equals(a.Dir, b.Dir, StringComparison.OrdinalIgnoreCase)) return 0;
        return CompareKey(DirKey(a.Dir), a.Dir, DirKey(b.Dir), b.Dir);
    }

    static readonly ConditionalWeakTable<string, string> DirKeys = new();

    static string DirKey(string dir) => DirKeys.GetValue(dir, d => SortKeys.SortKey(d.Replace('\\', '/')));
}

/// <summary>
/// 搜索索引（需求 S-1、S-2）：每首歌预先算好"标题 / 艺术家 / 专辑 / 文件名"的小写和拼音首字母（设计 §6.5）。
/// 按快照缓存，快照替换后第一次搜索时重建（启动后也会在后台预先建好）。
/// </summary>
public sealed class SearchIndex
{
    static readonly ConditionalWeakTable<LibrarySnapshot, SearchIndex> Cache = new();

    readonly Dictionary<long, (string text, string initials)> _entries;

    SearchIndex(LibrarySnapshot snapshot)
    {
        _entries = new Dictionary<long, (string, string)>(snapshot.Tracks.Count);
        foreach (var t in snapshot.Tracks)
        {
            var text = $"{t.Title}\n{t.ArtistDisplay}\n{t.Album}\n{t.FileName}".ToLowerInvariant();
            _entries[t.Id] = (text, Initials(t.Title) + "\n" + Initials(t.ArtistDisplay) + "\n" + Initials(t.Album ?? ""));
        }
    }

    public static SearchIndex For(LibrarySnapshot snapshot) => Cache.GetValue(snapshot, s => new SearchIndex(s));

    public bool Matches(Track t, string lowerQuery) =>
        _entries.TryGetValue(t.Id, out var e) && (e.text.Contains(lowerQuery, StringComparison.Ordinal) ||
                                                   lowerQuery.All(char.IsAsciiLetterOrDigit) && e.initials.Contains(lowerQuery, StringComparison.Ordinal));

    /// <summary>拼音首字母：只取汉字的（"周杰伦 Jay" → "zjl"），英文单词不参与，避免误匹配。</summary>
    static string Initials(string text)
    {
        if (!text.Any(c => c >= 0x3400)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (c < 0x3400) continue;
            var p = Pinyin.Transliterate(c.ToString());
            if (p.Length > 0 && char.IsAsciiLetter(p[0])) sb.Append(char.ToLowerInvariant(p[0]));
        }
        return sb.ToString();
    }

    /// <summary>"艺术家"搜索结果：名字或拼音首字母匹配的艺术家。</summary>
    public static IEnumerable<ArtistInfo> Artists(LibrarySnapshot snapshot, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return [];
        return snapshot.Artists.Where(a => !a.IsUnknown && (a.Name.ToLowerInvariant().Contains(q) ||
                                                           q.All(char.IsAsciiLetterOrDigit) && Initials(a.Name).Contains(q)));
    }
}
