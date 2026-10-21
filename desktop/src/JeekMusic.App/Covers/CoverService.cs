using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using JeekMusic.Core.Online;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.Online;
using JeekMusic.Settings;

namespace JeekMusic.Covers;

/// <summary>
/// 封面的查找、缓存和保存（需求 C-1 ~ C-5、C-7；规则同移动端 data/cover/CoverStore.kt）。封面按专辑共用，查找顺序：
/// <list type="number">
/// <item>这首歌自己的：隐藏目录 .jeekmusic\covers\ 里的在线封面（用户选的，最优先）→ 歌曲旁边的同名图片 → 内嵌封面；</item>
/// <item>同专辑其他歌的（同样的顺序）：专辑里只要有一个文件带封面，整张专辑都用它，也不会再去下载；</item>
/// <item>同目录的 cover.jpg / folder.jpg 等。</item>
/// </list>
/// 结果缩放后按专辑缓存在 %LOCALAPPDATA%\JeekMusic\covers\（没有专辑时按歌曲），"没有封面"也会缓存（标记文件，每次启动清掉）。
/// 解码和读文件都在后台线程上；内存里按尺寸缓存最近用过的 Bitmap。
/// </summary>
public sealed partial class CoverService
{
    public const int SmallPx = 160, LargePx = 720;
    /// <summary>找同专辑封面时最多看几个文件（避免误把几百首归成一张"专辑"时逐个读文件）。</summary>
    const int MaxAlbumFiles = 50;
    const string CoversDir = "covers";
    static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    static readonly string[] CoverNames = ["cover", "folder", "front", "albumart", "album"];

    readonly LibraryService _library;
    readonly SettingsStore _settings;
    readonly string _cacheDir = AppPaths.CoversDir;
    readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    readonly LinkedList<(string key, Bitmap bitmap)> _lru = new();
    readonly Lock _lruLock = new();
    const int LruSize = 48;

    /// <summary>某张专辑（或某首歌）的封面换了；参数是缓存键。界面、SMTC 据此重新加载。</summary>
    public event Action<string>? Changed;

    /// <summary>写文件被推迟了（正在播放）：由在线功能在播放器不再用这个文件后写入。</summary>
    public Func<Track, bool>? IsInUse { get; set; }
    public Action<Track>? DeferEmbed { get; set; }

    public CoverService(LibraryService library, SettingsStore settings)
    {
        _library = library;
        _settings = settings;
        // "没有封面"的标记每次启动清掉：用户可能新放了 cover.jpg，或扫描后文件里有了封面
        try { foreach (var f in Directory.EnumerateFiles(_cacheDir, "*.none")) File.Delete(f); }
        catch (IOException) { }
    }

    public static string CacheKey(Track t) => Sha1(t.AlbumKey ?? t.TrackKey);

    static string Sha1(string text) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)));

    string CachePath(string key, int size) => Path.Combine(_cacheDir, $"{key}_{size}.jpg");

    /// <summary>已经缓存好的封面文件（不读音乐文件）；没有缓存或没有封面时返回 null。SMTC 卡片用。</summary>
    public string? CachedPathIfReady(Track t, bool large = true) => File.Exists(CachePath(CacheKey(t), large ? LargePx : SmallPx)) ? CachePath(CacheKey(t), large ? LargePx : SmallPx) : null;

    /// <summary>缩放好的封面缓存文件（后台线程）；没有封面时返回 null。</summary>
    public async Task<string?> CachedFileAsync(Track t, bool large, CancellationToken ct = default)
    {
        var size = large ? LargePx : SmallPx;
        var key = CacheKey(t);
        var file = CachePath(key, size);
        if (File.Exists(file)) return file;
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var none = Path.Combine(_cacheDir, key + ".none");
            if (!File.Exists(file) && !File.Exists(none))
            {
                var bytes = await Task.Run(() => AlbumImage(t), ct);
                var scaled = bytes is null ? null : CoverImages.Scaled(bytes, size);
                if (scaled is null) TryWrite(none, []);
                else TryWrite(file, scaled);
            }
        }
        finally { gate.Release(); }
        return File.Exists(file) ? file : null;
    }

    static void TryWrite(string path, byte[] bytes)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException e) { Log.Warn("cover", $"写缓存失败：{path}", e); }
    }

    /// <summary>封面的 Bitmap（内存里缓存最近用过的）。</summary>
    public async Task<Bitmap?> BitmapAsync(Track t, bool large, CancellationToken ct = default)
    {
        var memKey = CacheKey(t) + (large ? "L" : "S");
        lock (_lruLock)
        {
            for (var n = _lru.First; n is not null; n = n.Next)
                if (n.Value.key == memKey)
                {
                    _lru.Remove(n);
                    _lru.AddFirst(n);
                    return n.Value.bitmap;
                }
        }
        var path = await CachedFileAsync(t, large, ct);
        if (path is null) return null;
        var bitmap = await Task.Run(() =>
        {
            try { using var s = File.OpenRead(path); return new Bitmap(s); }
            catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException) { return null; }
        }, ct);
        if (bitmap is null) return null;
        lock (_lruLock)
        {
            _lru.AddFirst((memKey, bitmap));
            while (_lru.Count > LruSize) _lru.RemoveLast();
        }
        return bitmap;
    }

    public async Task<bool> HasCoverAsync(Track t) => await CachedFileAsync(t, large: false) is not null;

    // ── 查找 ──

    /// <summary>同专辑的歌（<paramref name="t"/> 排第一）；没有专辑时只有它自己。CUE 整轨的各首共用一个文件，只保留一首。</summary>
    public IReadOnlyList<Track> AlbumOf(Track t)
    {
        var others = t.AlbumKey is { } k && _library.Snapshot.Albums.TryGetValue(k, out var album) ? album.Where(x => x.Id != t.Id) : [];
        return new[] { t }.Concat(others).DistinctBy(x => x.FolderId + "|" + x.RelativePath.ToLowerInvariant()).ToList();
    }

    /// <summary>整张专辑的封面原图（查找顺序见类注释）；都没有时返回 null。</summary>
    public byte[]? AlbumImage(Track t)
    {
        foreach (var x in AlbumOf(t).Take(MaxAlbumFiles))
            if (OwnImage(x) is { } bytes) return bytes;
        return FolderImage(t);
    }

    LibraryFolder? FolderOf(Track t) => _library.Snapshot.Folder(t.FolderId) is { Available: true } f ? f : null;

    byte[]? OwnImage(Track t)
    {
        var folder = FolderOf(t);
        if (folder is null)
        {
            if (_library.PathOf(t) is not { } path) return null;
            var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
            return ImageExtensions.Select(e => ReadFile(stem + e)).FirstOrDefault(b => b is not null) ?? TagReader.ReadCover(path);
        }
        return ReadFile(HiddenFile(folder, t)) ?? ReadFile(SidecarFile(folder, t)) ?? EmbeddedCover(folder, t);
    }

    static byte[]? ReadFile(string? path)
    {
        if (path is null) return null;
        try { return File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static byte[]? EmbeddedCover(LibraryFolder folder, Track t) => t.HasEmbeddedCover ? TagReader.ReadCover(folder.Root + t.RelativePath) : null;

    public static string HiddenDir(LibraryFolder folder) => Path.Combine(folder.Root, ".jeekmusic", CoversDir);

    string? HiddenFile(LibraryFolder folder, Track t)
    {
        var dir = HiddenDir(folder);
        if (!Directory.Exists(dir)) return null;
        return HiddenNames(t).Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
    }

    static string? SidecarFile(LibraryFolder folder, Track t)
    {
        var stem = Path.Combine(folder.Root + t.Dir, Path.GetFileNameWithoutExtension(t.FileName));
        return ImageExtensions.Select(e => stem + e).FirstOrDefault(File.Exists);
    }

    byte[]? FolderImage(Track t)
    {
        if (FolderOf(t) is not { } folder) return null;
        var dir = folder.Root + t.Dir;
        foreach (var name in CoverNames)
            foreach (var ext in ImageExtensions)
                if (ReadFile(Path.Combine(dir, name + ext)) is { } bytes) return bytes;
        return null;
    }

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1f]")]
    private static partial Regex Illegal();

    /// <summary>
    /// 隐藏目录里的文件名（同移动端 hiddenNames）：有专辑时是"专辑艺术家（或第一位艺术家） - 专辑.jpg"，同一专辑共用；
    /// 没有专辑时是"艺术家 - 歌名.jpg"。第一个是保存时用的名字。
    /// </summary>
    public static IReadOnlyList<string> HiddenNames(Track t)
    {
        var artist = !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist! : CoverMatcher.PrimaryArtist(t.ArtistDisplay);
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(t.Album)) list.Add(FileName(artist, t.Album!));
        list.Add(FileName(t.ArtistDisplay, t.TitleTag ?? t.Title));
        return list;
    }

    static string FileName(params string[] parts)
    {
        var joined = string.Join(" - ", parts.Select(p => p.Trim()).Where(p => p.Length > 0));
        var name = Illegal().Replace(joined, "_").Trim('.', ' ');
        if (name.Length > 120) name = name[..120];
        return (name.Length == 0 ? "_" : name) + ".jpg";
    }

    // ── 保存（需求 C-3、C-4）──

    /// <summary>实际的保存位置；写入文件或同名图片时 <see cref="Files"/> 是写了几个；<see cref="AlsoDone"/> 是同时写入了歌词的文件。</summary>
    public sealed record Saved(CoverSaveMode Mode, int Files, IReadOnlySet<string> AlsoDone);

    /// <summary>这首歌按 <paramref name="mode"/> 有没有自己的封面：写入文件看文件里有没有，同名图片看有没有图片，隐藏目录看整张专辑。</summary>
    public async Task<bool> HasOwnCoverAsync(Track t, CoverSaveMode mode)
    {
        var folder = FolderOf(t);
        return mode switch
        {
            CoverSaveMode.Embed => folder is not null && await Task.Run(() => EmbeddedCover(folder, t) is not null),
            CoverSaveMode.Sidecar => folder is not null && SidecarFile(folder, t) is not null,
            _ => await HasCoverAsync(t),
        };
    }

    /// <summary>
    /// 把封面保存给同一专辑的 <paramref name="tracks"/>，第一首决定隐藏目录里的文件名。图片统一转成 JPEG。
    /// 隐藏目录：整张专辑存一张；写入文件 / 同名图片：每个文件都写，有写不进去的就再在隐藏目录存一张。
    /// <paramref name="alsoLyrics"/>：写入文件时这些文件（完整路径 → 歌词）同一次一起写入歌词。
    /// 正在播放的文件不写（需求 P-14 ④），先存隐藏目录，等播放器不再用它再写。
    /// </summary>
    public Saved? Save(IReadOnlyList<Track> tracks, byte[] image, CoverSaveMode mode, IReadOnlyDictionary<string, string>? alsoLyrics = null, bool preserveShared = false)
    {
        var first = tracks.FirstOrDefault();
        if (first is null || CoverImages.ToJpeg(image) is not { } jpeg) return null;
        var files = 0;
        var failed = false;
        var alsoDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mode != CoverSaveMode.HiddenFolder)
        {
            foreach (var t in tracks.DistinctBy(x => x.FolderId + "|" + x.RelativePath.ToLowerInvariant()))
            {
                var folder = FolderOf(t);
                if (folder is null) { failed = true; continue; }
                var path = folder.Root + t.RelativePath;
                bool ok;
                if (mode == CoverSaveMode.Embed)
                {
                    if (!TagWriter.CanWrite(path)) ok = false;
                    else if (IsInUse?.Invoke(t) == true)
                    {
                        // 正在播放：先存隐藏目录（马上能显示），播放器不再用这个文件后再写进去
                        DeferEmbed?.Invoke(t);
                        failed = true;
                        continue;
                    }
                    else
                    {
                        var lyrics = alsoLyrics?.GetValueOrDefault(path);
                        _library.Watcher.Ignore(path);
                        ok = TagWriter.Write(path, jpeg, lyrics);
                        if (ok)
                        {
                            MarkEmbedded(folder, t, lyrics is not null);
                            if (lyrics is not null) alsoDone.Add(path);
                        }
                    }
                }
                else ok = WriteSidecar(folder, t, jpeg);
                // 隐藏目录里的封面优先级最高：存到别处时删掉这首歌原来的，否则新封面不会显示；写入文件时同名图片也排在前面，同样删掉（C-7）
                if (ok)
                {
                    if (mode == CoverSaveMode.Embed) DeleteSidecar(folder, t);
                    files++;
                }
                else failed = true;
            }
        }
        if (mode != CoverSaveMode.HiddenFolder && !failed && !preserveShared)
            foreach (var t in tracks) if (FolderOf(t) is { } folder) DeleteHidden(folder, t);
        var hidden = false;
        if (mode == CoverSaveMode.HiddenFolder || failed)
            foreach (var t in tracks.DistinctBy(t => t.FolderId))
                if (FolderOf(t) is { } folder) hidden |= WriteHidden(folder, t, jpeg);
        Saved? saved = files > 0 ? new Saved(mode, files, alsoDone) : hidden ? new Saved(CoverSaveMode.HiddenFolder, 0, alsoDone) : null;
        if (saved is not null) ReplaceCache(first, jpeg);
        return saved;
    }

    static void WriteImage(string path, byte[] jpeg)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, jpeg);
        File.Move(temp, path, true);
    }

    bool WriteSidecar(LibraryFolder folder, Track t, byte[] jpeg)
    {
        var path = Path.Combine(folder.Root + t.Dir, Path.GetFileNameWithoutExtension(t.FileName) + ".jpg");
        try
        {
            _library.Watcher.Ignore(path);
            WriteImage(path, jpeg);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("cover", $"写同名图片失败：{path}", e);
            return false;
        }
    }

    bool WriteHidden(LibraryFolder folder, Track t, byte[] jpeg)
    {
        try
        {
            var dir = HiddenDir(folder);
            Directory.CreateDirectory(dir);
            WriteImage(Path.Combine(dir, HiddenNames(t)[0]), jpeg);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("cover", "写隐藏目录失败", e);
            return false;
        }
    }

    void DeleteHidden(LibraryFolder folder, Track t)
    {
        foreach (var n in HiddenNames(t))
        {
            var p = Path.Combine(HiddenDir(folder), n);
            try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { }
        }
    }

    void DeleteSidecar(LibraryFolder folder, Track t)
    {
        if (SidecarFile(folder, t) is not { } p) return;
        try { _library.Watcher.Ignore(p); File.Delete(p); } catch (IOException) { }
    }

    /// <summary>写入后记下文件里有封面（以及歌词）、新的大小和修改时间：扫描不必重新读这个文件。</summary>
    void MarkEmbedded(LibraryFolder folder, Track t, bool lyrics)
    {
        try
        {
            var info = new FileInfo(folder.Root + t.RelativePath);
            _library.Db.MarkEmbedded(t.FolderId, t.Dir, t.FileName, cover: true, lyrics: lyrics, info.Length, info.LastWriteTimeUtc.Ticks);
            LibraryRefresh.Soon(_library);
        }
        catch (IOException) { }
    }

    void ReplaceCache(Track t, byte[] jpeg)
    {
        var key = CacheKey(t);
        try { File.Delete(Path.Combine(_cacheDir, key + ".none")); } catch (IOException) { }
        foreach (var size in new[] { SmallPx, LargePx })
            if (CoverImages.Scaled(jpeg, size) is { } scaled) TryWrite(CachePath(key, size), scaled);
        Forget(key);
    }

    /// <summary>清掉某个键的缓存（来源变了），下次重新找。</summary>
    public void Invalidate(Track t)
    {
        var key = CacheKey(t);
        foreach (var p in new[] { CachePath(key, SmallPx), CachePath(key, LargePx), Path.Combine(_cacheDir, key + ".none") })
            try { File.Delete(p); } catch (IOException) { }
        Forget(key);
    }

    void Forget(string key)
    {
        lock (_lruLock)
            for (var n = _lru.First; n is not null;)
            {
                var next = n.Next;
                if (n.Value.key.StartsWith(key, StringComparison.Ordinal)) _lru.Remove(n);
                n = next;
            }
        Dispatcher.UIThread.Post(() => Changed?.Invoke(key));
    }

    /// <summary>
    /// 把已经下载成文件的封面写入音乐文件（需求 C-7）：每个文件用它自己的（隐藏目录 > 同名图片），写入成功后删掉同名图片；
    /// 隐藏目录里的一张由整张专辑共用，专辑里的文件都写入成功才删。目录里的 cover.jpg 等不是下载的，不动。返回写入了几个文件。
    /// </summary>
    public bool HasDownloaded(Track t) => FolderOf(t) is { } folder && (HiddenFile(folder, t) is not null || SidecarFile(folder, t) is not null);

    public int EmbedDownloaded(IReadOnlyList<Track> tracks, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? alsoLyrics = null, Action<Track>? lyricsSaved = null)
    {
        var hiddenUsers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<Track>();
        foreach (var t in tracks.SelectMany(AlbumOf).DistinctBy(x => x.FolderId + "|" + x.RelativePath.ToLowerInvariant()).OrderBy(x => x.FolderId).ThenBy(x => x.Dir))
        {
            if (ct.IsCancellationRequested) break;
            if (FolderOf(t) is not { } folder) continue;
            var path = folder.Root + t.RelativePath;
            var hidden = HiddenFile(folder, t);
            var side = SidecarFile(folder, t);
            var source = hidden ?? side;
            if (source is null) continue;
            var ok = false;
            if (!TagWriter.CanWrite(path)) { if (hidden is not null) hiddenUsers[hidden] = false; continue; }
            if (IsInUse?.Invoke(t) == true) DeferEmbed?.Invoke(t);
            else if (ReadFile(source) is { } bytes && CoverImages.ToJpeg(bytes) is { } jpeg)
            {
                _library.Watcher.Ignore(path);
                var lyrics = t.IsCue ? null : alsoLyrics?.GetValueOrDefault(path);
                ok = TagWriter.Write(path, jpeg, lyrics);
                if (ok)
                {
                    MarkEmbedded(folder, t, lyrics is not null);
                    if (lyrics is not null) lyricsSaved?.Invoke(t);
                }
            }
            if (hidden is not null) hiddenUsers[hidden] = hiddenUsers.GetValueOrDefault(hidden, true) && ok;
            if (!ok) continue;
            if (side is not null) DeleteSidecar(folder, t);
            changed.Add(t);
        }
        foreach (var (hidden, allDone) in hiddenUsers)
            if (allDone && !ct.IsCancellationRequested) try { File.Delete(hidden); } catch (IOException) { }
        foreach (var t in changed.DistinctBy(CacheKey)) Invalidate(t);
        return changed.Count;
    }
}
