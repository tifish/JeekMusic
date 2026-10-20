using JeekMusic.Core.Library;
using JeekMusic.Core.Text;

namespace JeekMusic.Library;

/// <summary>一个艺术家（需求 A-1 ~ A-7）。<see cref="NameKey"/> 为空串的是"未知艺术家"。</summary>
public sealed class ArtistInfo
{
    public required string Name { get; init; }
    public required string NameKey { get; init; }
    public required string SortKey { get; init; }
    /// <summary>A–Z 或 "#"。</summary>
    public required string IndexLetter { get; init; }
    public required IReadOnlyList<Track> Tracks { get; init; }
    public bool IsUnknown => NameKey.Length == 0;
}

/// <summary>文件夹树的一个节点（需求 W-3）：一个音乐文件夹的根，或其中的一个子目录。</summary>
public sealed class DirNode
{
    public required LibraryFolder Folder { get; init; }
    /// <summary>相对于音乐文件夹的目录，"\" 结尾；根目录为空串。</summary>
    public required string Dir { get; init; }
    public required string Name { get; init; }
    public List<DirNode> Children { get; } = new();
    /// <summary>直接在这个目录里的歌（不含子目录）。</summary>
    public List<Track> Tracks { get; } = new();

    /// <summary>这个目录和所有子目录里的歌。</summary>
    public IEnumerable<Track> AllTracks()
    {
        foreach (var t in Tracks) yield return t;
        foreach (var c in Children)
            foreach (var t in c.AllTracks()) yield return t;
    }
}

/// <summary>一首歌三个维度的智能标签（需求 M-1）；没有分析过的维度是 null。</summary>
public sealed record SongTags(string? Mood, string? Voice, string? Genre, bool MoodManual, bool VoiceManual, bool GenreManual)
{
    public string? this[string dimension] => dimension switch { "MOOD" => Mood, "VOICE" => Voice, "GENRE" => Genre, _ => null };
}

/// <summary>
/// 内存里的整个曲库（设计 §2、§6.2），不可变：扫描、同步改了数据库后生成新的一份替换旧的。
/// 界面上所有的列表（文件夹、艺术家、专辑、搜索、筛选、排序）都从这里算，不查数据库。
/// </summary>
public sealed class LibrarySnapshot
{
    public IReadOnlyList<LibraryFolder> Folders { get; }
    public IReadOnlyList<Track> Tracks { get; }
    public IReadOnlyDictionary<long, Track> ById { get; }
    public IReadOnlyDictionary<string, Track> ByKey { get; }
    public IReadOnlyList<ArtistInfo> Artists { get; }
    public IReadOnlyDictionary<string, ArtistInfo> ArtistByKey { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<Track>> Albums { get; }
    /// <summary>每个音乐文件夹的目录树根节点。</summary>
    public IReadOnlyList<DirNode> Roots { get; }
    public IReadOnlyDictionary<string, SongTags> Tags { get; }
    /// <summary>扫描时跳过的不支持的文件数（设置的曲库页显示）。</summary>
    public int Version { get; }

    static int _version;

    public static readonly LibrarySnapshot Empty = new([], [], new Dictionary<string, SongTags>());

    public LibrarySnapshot(IReadOnlyList<LibraryFolder> folders, IReadOnlyList<Track> tracks, IReadOnlyDictionary<string, SongTags> tags)
    {
        Version = Interlocked.Increment(ref _version);
        Folders = folders;
        var available = folders.Where(f => f.Available).Select(f => f.Id).ToHashSet();
        // 所在的盘不可用的音乐文件夹：歌曲行保留在数据库里，但不出现在浏览视图中（需求 L-8）
        Tracks = tracks.Where(t => available.Contains(t.FolderId)).ToList();
        ById = tracks.ToDictionary(t => t.Id);
        ByKey = tracks.ToDictionary(t => t.TrackKey);
        Tags = tags;

        // 艺术家（需求 A-2：多艺术家拆分后，一首歌出现在每个艺术家下面；A-5：名称合并忽略大小写和空白）
        var groups = new Dictionary<string, (string name, List<Track> tracks)>();
        foreach (var t in Tracks)
        {
            if (t.Artists.Count == 0)
            {
                Add("", "", t);
                continue;
            }
            foreach (var a in t.Artists) Add(ArtistSplitter.NameKey(a), a, t);
        }
        void Add(string key, string name, Track t)
        {
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = (name, new List<Track>());
            g.tracks.Add(t);
        }
        var artists = groups.Select(kv =>
        {
            var sortKey = kv.Key.Length == 0 ? "￿" : SortKeys.SortKey(kv.Value.name);
            return new ArtistInfo
            {
                Name = kv.Value.name,
                NameKey = kv.Key,
                SortKey = sortKey,
                IndexLetter = kv.Key.Length == 0 ? "#" : SortKeys.IndexLetter(sortKey),
                Tracks = kv.Value.tracks,
            };
        }).ToList();
        // "未知艺术家"排在最后（同移动端）
        artists.Sort((a, b) => a.IsUnknown != b.IsUnknown ? (a.IsUnknown ? 1 : -1) : string.CompareOrdinal(a.SortKey, b.SortKey) is var c && c != 0 ? c : NaturalOrderComparer.Instance.Compare(a.Name, b.Name));
        Artists = artists;
        ArtistByKey = artists.ToDictionary(a => a.NameKey);

        Albums = Tracks.Where(t => t.AlbumKey is not null).GroupBy(t => t.AlbumKey!).ToDictionary(g => g.Key, g => (IReadOnlyList<Track>)g.ToList());

        // 目录树
        var roots = new List<DirNode>();
        foreach (var folder in folders.Where(f => f.Available))
        {
            var root = new DirNode { Folder = folder, Dir = "", Name = folder.DisplayName };
            var nodes = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };
            foreach (var t in Tracks.Where(t => t.FolderId == folder.Id)) NodeOf(t.Dir).Tracks.Add(t);
            DirNode NodeOf(string dir)
            {
                if (nodes.TryGetValue(dir, out var n)) return n;
                var trimmed = dir.TrimEnd('\\');
                var slash = trimmed.LastIndexOf('\\');
                var parent = NodeOf(slash < 0 ? "" : trimmed[..(slash + 1)]);
                n = new DirNode { Folder = folder, Dir = dir, Name = trimmed[(slash + 1)..] };
                parent.Children.Add(n);
                nodes[dir] = n;
                return n;
            }
            // 子文件夹按名称排序（中文按拼音，需求 F-4）
            foreach (var n in nodes.Values)
                n.Children.Sort((a, b) => string.CompareOrdinal(SortKeys.SortKey(a.Name), SortKeys.SortKey(b.Name)) is var c && c != 0 ? c : NaturalOrderComparer.Instance.Compare(a.Name, b.Name));
            roots.Add(root);
        }
        Roots = roots;
    }

    public LibraryFolder? Folder(long id) => Folders.FirstOrDefault(f => f.Id == id);

    /// <summary>找目录节点；目录不存在（被删了、被筛掉了）时返回 null。</summary>
    public DirNode? Node(long folderId, string dir)
    {
        var root = Roots.FirstOrDefault(r => r.Folder.Id == folderId);
        if (root is null) return null;
        if (dir.Length == 0) return root;
        var node = root;
        foreach (var part in dir.TrimEnd('\\').Split('\\'))
        {
            node = node.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>完整路径；所在的盘不可用时返回 null。</summary>
    public string? PathOf(Track t) => Folder(t.FolderId) is { Available: true } f ? f.Root + t.Dir + t.FileName : null;
}
