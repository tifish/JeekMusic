using JeekMusic.Core.Lyrics;
using JeekMusic.Core.Text;
using JeekMusic.Infrastructure;
using JeekMusic.Library;

namespace JeekMusic.Lyrics;

/// <summary>找到的歌词：解析结果、来自哪里、这首歌的时间偏移。</summary>
public sealed record FoundLyrics(LyricsText Text, string Source, string? Path, int OffsetMs);

/// <summary>
/// 歌词（需求 LY-1 ~ LY-7、CUE-7；规则同移动端设计 §8.1）。读取时的优先级：隐藏目录 > 应用内缓存 > 同名 .lrc > 内嵌歌词
/// （App 保存的在前，手动换的歌词才一定能显示）。CUE 整轨的歌先找 "&lt;音轨号&gt;. &lt;歌名&gt;.lrc"，再找 "&lt;歌名&gt;.lrc"。
/// </summary>
public sealed class LyricsService(LibraryService library)
{
    readonly Dictionary<string, FoundLyrics?> _cache = new();
    readonly Lock _lock = new();

    /// <summary>某首歌的歌词换了（下载、手动搜索保存、改偏移）。</summary>
    public event Action<Track>? Changed;

    /// <summary>歌曲旁边的同名 .lrc 文件名（CUE 整轨按音轨号和歌名，需求 CUE-7）。</summary>
    public static IEnumerable<string> SidecarNames(Track t)
    {
        if (t.IsCue)
        {
            var title = Sanitize(t.Title);
            yield return $"{t.CueTrackNo:00}. {title}.lrc";
            yield return $"{title}.lrc";
            yield break;
        }
        yield return Path.GetFileNameWithoutExtension(t.FileName) + ".lrc";
    }

    /// <summary>隐藏目录：音乐文件夹根下的 .jeekmusic\lyrics\&lt;文件名&gt;.lrc（同移动端 LY-11）。</summary>
    public static string HiddenPath(LibraryFolder folder, Track t) =>
        Path.Combine(folder.Root, ".jeekmusic", "lyrics", Path.GetFileNameWithoutExtension(SidecarNames(t).First()) + ".lrc");

    static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    /// <summary>读歌词（读文件，在后台线程上调用）。没有歌词时返回 null。结果按歌曲缓存，直到 <see cref="Invalidate"/>。</summary>
    public FoundLyrics? Load(Track t)
    {
        lock (_lock)
            if (_cache.TryGetValue(t.TrackKey, out var cached)) return cached;
        var found = Find(t);
        lock (_lock) _cache[t.TrackKey] = found;
        return found;
    }

    FoundLyrics? Find(Track t)
    {
        var folder = library.Snapshot.Folder(t.FolderId);
        var offset = library.Db.LyricsOffsets().FirstOrDefault(o => o.TrackKey == t.TrackKey)?.OffsetMs ?? 0;
        FoundLyrics? Make(string text, string source, string? path)
        {
            var parsed = LrcParser.Parse(text);
            return parsed.Lines.Count == 0 ? null : new FoundLyrics(parsed, source, path, offset);
        }
        try
        {
            if (folder is { Available: true })
            {
                var hidden = HiddenPath(folder, t);
                if (File.Exists(hidden) && Make(CharsetDetector.Decode(File.ReadAllBytes(hidden)), L.T("隐藏目录"), hidden) is { } h) return h;
            }
            if (library.Db.CachedLyrics(t.TrackKey) is { content: { } cachedText } && Make(cachedText, L.T("应用内缓存"), null) is { } c) return c;
            if (library.PathOf(t) is { } audioPath)
            {
                foreach (var name in SidecarNames(t))
                {
                    var path = Path.Combine(Path.GetDirectoryName(audioPath)!, name);
                    if (File.Exists(path) && Make(CharsetDetector.Decode(File.ReadAllBytes(path)), L.T("同名 .lrc"), path) is { } sidecar) return sidecar;
                }
                if (!t.IsCue && t.HasEmbeddedLyrics && TagReader.ReadLyrics(audioPath) is { } embedded && Make(embedded, L.T("内嵌歌词"), null) is { } e) return e;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("lyrics", $"读取歌词失败：{t.RelativePath}", ex);
        }
        return null;
    }

    public void Invalidate(Track t)
    {
        lock (_lock) _cache.Remove(t.TrackKey);
        Changed?.Invoke(t);
    }

    public void ClearMemoryCache()
    {
        lock (_lock) _cache.Clear();
    }

    /// <summary>调整这首歌的歌词偏移（±0.5 秒步进，需求 LY-6），参与同步（需求 SY-2）。</summary>
    public void SetOffset(Track t, int offsetMs)
    {
        library.Db.SetLyricsOffset(t.TrackKey, offsetMs);
        Invalidate(t);
        OffsetChanged?.Invoke();
    }

    /// <summary>歌词偏移变了（同步服务据此写同步文件）。</summary>
    public event Action? OffsetChanged;
}
