using System.Collections.Concurrent;
using JeekMusic.Core.Text;

namespace JeekMusic.Core.Library;

/// <summary>全局的歌曲排序方式（需求 U-8，移动端 SettingsRepository.kt 的 SongSort）。</summary>
public enum SongSort
{
    FILE_NAME,
    MODIFIED,
    TITLE,
    ARTIST,
}

/// <summary>
/// 参与排序的一首歌。排序键用 <see cref="SortKeys.SortKey"/> 算（中文转拼音）；桌面端在扫描时算好存进数据库（桌面端设计 §4），
/// 没有存的可以用 <see cref="SongSorter.Key"/>（带缓存）现算。
/// </summary>
public interface ISortableSong
{
    /// <summary>文件名的排序键（含扩展名，同移动端）。</summary>
    string FileNameKey { get; }

    /// <summary>展示用歌名的排序键。</summary>
    string TitleKey { get; }

    /// <summary>展示用艺术家的排序键。</summary>
    string ArtistKey { get; }

    /// <summary>展示用艺术家为空（"未知艺术家"，按歌手排序时排在最后）。</summary>
    bool ArtistUnknown { get; }

    /// <summary>文件的修改时间，数值越大越新（单位由调用方定，只用来比较）。</summary>
    long Modified { get; }

    /// <summary>CUE 整轨里的音轨号；普通歌曲为 null。</summary>
    int? CueTrackNo { get; }
}

/// <summary>
/// 歌曲排序（需求 U-8，移动端 SongSorter.kt，移动端设计 §5.8）：修改时间新的在前；文件名、歌名、歌手从 A 到 Z，
/// 中文按拼音，数字按数值（"2" 在 "10" 前面）。排序是稳定的。播放列表按文件名时保持手动顺序、
/// "播放全部（含子文件夹）"先按目录、文件夹里 CUE 虚拟歌曲排在普通歌曲后面，这些由调用方处理。
/// </summary>
public static class SongSorter
{
    private static readonly ConcurrentDictionary<string, string> KeyCache = new();

    /// <summary>排序键（"周杰伦" → "zhou jie lun"），按字符串缓存：转写比较慢，长列表反复排序也不卡。</summary>
    public static string Key(string text) => KeyCache.GetOrAdd(text, static t => SortKeys.SortKey(t));

    /// <summary>按文件名（中文按拼音），CUE 整轨里的各首按音轨号。</summary>
    public static IComparer<ISortableSong> ByFileName { get; } = Comparer<ISortableSong>.Create((a, b) =>
    {
        var cmp = NaturalOrderComparer.Instance.Compare(a.FileNameKey, b.FileNameKey);
        return cmp != 0 ? cmp : (a.CueTrackNo ?? 0).CompareTo(b.CueTrackNo ?? 0);
    });

    /// <summary>按歌名（拼音），同名的按文件名。</summary>
    public static IComparer<ISortableSong> ByTitle { get; } = Comparer<ISortableSong>.Create((a, b) =>
    {
        var cmp = NaturalOrderComparer.Instance.Compare(a.TitleKey, b.TitleKey);
        return cmp != 0 ? cmp : ByFileName.Compare(a, b);
    });

    public static IComparer<ISortableSong> ByModified { get; } = Comparer<ISortableSong>.Create((a, b) =>
    {
        var cmp = b.Modified.CompareTo(a.Modified);
        return cmp != 0 ? cmp : ByFileName.Compare(a, b);
    });

    /// <summary>按歌手（拼音），"未知艺术家"排在最后，同一歌手按歌名。</summary>
    public static IComparer<ISortableSong> ByArtist { get; } = Comparer<ISortableSong>.Create((a, b) =>
    {
        var cmp = a.ArtistUnknown.CompareTo(b.ArtistUnknown);
        if (cmp != 0) return cmp;
        cmp = NaturalOrderComparer.Instance.Compare(a.ArtistKey, b.ArtistKey);
        return cmp != 0 ? cmp : ByTitle.Compare(a, b);
    });

    public static IComparer<ISortableSong> Comparer(SongSort sort) => sort switch
    {
        SongSort.MODIFIED => ByModified,
        SongSort.TITLE => ByTitle,
        SongSort.ARTIST => ByArtist,
        _ => ByFileName,
    };

    /// <summary>按 <paramref name="sort"/> 稳定排序；<paramref name="song"/> 返回 null 的条目（播放列表里暂时不可用的歌）排在最后，保持原顺序。</summary>
    public static IReadOnlyList<T> Sorted<T>(IEnumerable<T> items, SongSort sort, Func<T, ISortableSong?> song)
    {
        var comparer = Comparer(sort);
        var present = new List<(T Item, ISortableSong Song)>();
        var missing = new List<T>();
        foreach (var item in items)
        {
            if (song(item) is { } s) present.Add((item, s));
            else missing.Add(item);
        }
        return present.OrderBy(p => p.Song, comparer).Select(p => p.Item).Concat(missing).ToList();
    }
}
