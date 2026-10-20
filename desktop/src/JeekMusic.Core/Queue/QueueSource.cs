namespace JeekMusic.Core.Queue;

/// <summary>
/// 播放队列来自哪个列表（需求 P-15，同移动端 playback/QueueSource.kt）。列表变了就按它重新生成队列里来自列表的部分。
/// <see cref="Encode"/> / <see cref="Decode"/> 用于断点续播时保存。
/// </summary>
public abstract record QueueSource
{
    /// <summary>文件夹；<paramref name="Dir"/> 是相对于音乐文件夹的目录（"\" 结尾，根目录为空串）；<paramref name="Recursive"/> 为 true 时包括子文件夹。</summary>
    public sealed record Folder(long FolderId, string Dir, bool Recursive) : QueueSource;

    public sealed record Artist(string NameKey) : QueueSource;

    public sealed record Album(string AlbumKey) : QueueSource;

    public sealed record Playlist(long PlaylistId) : QueueSource;

    public sealed record Search(string Query) : QueueSource;

    const char Separator = '\u001F';

    public string Encode() => string.Join(Separator, this switch
    {
        Folder f => ["folder", f.FolderId.ToString(), f.Dir, f.Recursive ? "true" : "false"],
        Artist a => ["artist", a.NameKey],
        Album a => ["album", a.AlbumKey],
        Playlist p => ["playlist", p.PlaylistId.ToString()],
        Search s => new[] { "search", s.Query },
        _ => throw new InvalidOperationException(),
    });

    public static QueueSource? Decode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var parts = value.Split(Separator);
        try
        {
            return parts[0] switch
            {
                "folder" => new Folder(long.Parse(parts[1]), parts[2], parts[3] == "true"),
                "artist" => new Artist(parts[1]),
                "album" => new Album(parts[1]),
                "playlist" => new Playlist(long.Parse(parts[1])),
                "search" => new Search(parts[1]),
                _ => null,
            };
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }
}
