using System.Text;
using Avalonia.Threading;
using JeekMusic.Core.Library;
using JeekMusic.Infrastructure;
using JeekMusic.Platform;
using JeekMusic.Playback;
using JeekMusic.Settings;

namespace JeekMusic.Library;

public enum AddFolderStatus { Added, AlreadyContained, ContainsExisting, NotFound, AlreadyAdded }

public sealed record AddFolderResult(AddFolderStatus Status, LibraryFolder? Folder, IReadOnlyList<LibraryFolder> Children);

/// <summary>
/// 曲库（设计 §2、§5）：音乐文件夹、扫描、文件监听、内存快照。后台的工作（扫描、重建快照）排成一个队列依次执行；
/// <see cref="Snapshot"/> 只在 UI 线程上替换，<see cref="SnapshotChanged"/> 也在 UI 线程上触发。
/// </summary>
public sealed class LibraryService : ITrackSource, IDisposable
{
    readonly SettingsStore _settings;
    readonly Scanner _scanner;
    readonly SemaphoreSlim _work = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    readonly MemoryMaintenance _memory = new();

    public LibraryDb Db { get; }
    public FolderWatcher Watcher { get; } = new();
    public LibrarySnapshot Snapshot { get; private set; } = LibrarySnapshot.Empty;

    public event Action? SnapshotChanged;
    /// <summary>扫描进度（UI 线程）：已处理数、总数；扫描结束时 (0, 0)。</summary>
    public event Action<int, int>? ScanProgress;
    /// <summary>某个音乐文件夹的同步目录有变化（后台线程）。</summary>
    public event Action<long>? SyncFolderChanged;
    /// <summary>添加了音乐文件夹（扫描完成后，UI 线程）：同步服务据此检查备份（需求 SY-9）。</summary>
    public event Action<LibraryFolder>? FolderAdded;

    public bool Scanning { get; private set; }
    public int UnsupportedCount { get; private set; }

    public LibraryService(LibraryDb db, SettingsStore settings)
    {
        Db = db;
        _settings = settings;
        _scanner = new Scanner(db);
        _scanner.Progress += (done, total) => Dispatcher.UIThread.Post(() => ScanProgress?.Invoke(done, total));
        Watcher.DirsChanged += (id, dirs) => _ = Enqueue(() => ScanFolder(id, dirs, full: false));
        Watcher.SyncChanged += id => SyncFolderChanged?.Invoke(id);
        Watcher.Failed += failedId => { _ = Enqueue(RefreshVolumesCore); };
    }

    public ScanConfig CurrentConfig()
    {
        var s = _settings.Current;
        Encoding? legacy = null;
        if (!string.IsNullOrEmpty(s.LegacyCharset))
            try { legacy = Encoding.GetEncoding(s.LegacyCharset); } catch (ArgumentException) { }
        return new ScanConfig(
            new ArtistSplitConfig(s.ArtistSeparators.ToHashSet(), s.ArtistExceptions),
            s.InferFromFileName,
            Enum.TryParse<FileNameFormat>(s.FileNameFormat, out var f) ? f : FileNameFormat.ARTIST_TITLE,
            legacy);
    }

    // ── 启动 ──

    /// <summary>启动时：读入整个曲库生成快照（后台），之后启动文件监听，并在 2 秒后做一次增量扫描（设计 §12）。</summary>
    public Task StartAsync() => Enqueue(() =>
    {
        RefreshVolumesCore();
        RebuildSnapshot();
        foreach (var f in Db.Folders().Where(f => f.Available)) Watcher.Watch(f);
        _ = Task.Delay(2000, _cts.Token).ContinueWith(_ => RescanAsync(full: false), TaskContinuationOptions.OnlyOnRanToCompletion);
    });

    Task Enqueue(Action action) => Task.Run(async () =>
    {
        try { await _work.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return; }
        try { if (!_cts.IsCancellationRequested) action(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Error("library", "后台任务出错", e); }
        finally { _work.Release(); }
    });

    /// <summary>重新读数据库生成快照（扫描、同步、改规则之后）。</summary>
    public void RebuildSnapshot()
    {
        var folders = Db.Folders();
        var tracks = Db.AllTracks();
        var tags = Db.AllTags().ToDictionary(kv => kv.Key, kv => new SongTags(
            kv.Value.GetValueOrDefault("MOOD").value, kv.Value.GetValueOrDefault("VOICE").value, kv.Value.GetValueOrDefault("GENRE").value,
            kv.Value.GetValueOrDefault("MOOD").source == "MANUAL", kv.Value.GetValueOrDefault("VOICE").source == "MANUAL",
            kv.Value.GetValueOrDefault("GENRE").source == "MANUAL",
            kv.Value.GetValueOrDefault("CALMNESS").value, kv.Value.GetValueOrDefault("CALMNESS").source == "MANUAL"));
        var snapshot = new LibrarySnapshot(folders, tracks, tags);
        Dispatcher.UIThread.Post(() =>
        {
            if (_cts.IsCancellationRequested) return;
            Snapshot = snapshot;
            SnapshotChanged?.Invoke();
            _memory.AfterLibraryWork(snapshot.Tracks.Count);
        });
    }

    /// <summary>后台排队重建快照（智能标签变了等）。</summary>
    public Task RefreshAsync() => Enqueue(RebuildSnapshot);

    // ── 盘的插拔（需求 L-8）──

    public Task RefreshVolumesAsync() => Enqueue(RefreshVolumesCore);

    void RefreshVolumesCore()
    {
        var changed = false;
        foreach (var f in Db.Folders())
        {
            var root = Volumes.CurrentRoot(f.VolumeId, f.PathInVolume);
            var available = root is not null;
            if (available == f.Available && (root is null || root.Equals(f.Root, StringComparison.OrdinalIgnoreCase))) continue;
            Db.UpdateFolderLocation(f.Id, root ?? f.Root, available);
            changed = true;
            if (available)
            {
                Log.Info("library", $"音乐文件夹可用了：{root}");
                f.Root = root!;
                Watcher.Watch(f);
                ScanFolderCore(f, null, false);
            }
            else
            {
                Log.Info("library", $"音乐文件夹不可用：{f.Root}");
                Watcher.Unwatch(f.Id);
            }
        }
        if (changed) RebuildSnapshot();
    }

    // ── 音乐文件夹（需求 L-1 ~ L-3）──

    public AddFolderResult CheckFolder(string path)
    {
        if (!Directory.Exists(path)) return new(AddFolderStatus.NotFound, null, []);
        var full = Volumes.WithSlash(Path.GetFullPath(path));
        var folders = Db.Folders();
        if (folders.FirstOrDefault(f => f.Root.Equals(full, StringComparison.OrdinalIgnoreCase)) is { } same) return new(AddFolderStatus.AlreadyAdded, same, []);
        if (folders.FirstOrDefault(f => full.StartsWith(f.Root, StringComparison.OrdinalIgnoreCase)) is { } parent) return new(AddFolderStatus.AlreadyContained, parent, []);
        var children = folders.Where(f => f.Root.StartsWith(full, StringComparison.OrdinalIgnoreCase)).ToList();
        return new(children.Count > 0 ? AddFolderStatus.ContainsExisting : AddFolderStatus.Added, null, children);
    }

    /// <summary>添加音乐文件夹并扫描；<paramref name="replaceChildren"/> 为 true 时先移除它里面已经添加的子文件夹（需求 L-3）。</summary>
    public Task AddFolderAsync(string path, bool replaceChildren) => Enqueue(() =>
    {
        var check = CheckFolder(path);
        if (check.Status is AddFolderStatus.NotFound or AddFolderStatus.AlreadyContained or AddFolderStatus.AlreadyAdded) return;
        if (check.Status == AddFolderStatus.ContainsExisting)
        {
            if (!replaceChildren) return;
            foreach (var child in check.Children) { Watcher.Unwatch(child.Id); Db.RemoveFolder(child.Id); }
        }
        var full = Volumes.WithSlash(Path.GetFullPath(path));
        var (volumeId, pathInVolume) = Volumes.Locate(full);
        var name = Path.GetFileName(full.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = full;
        var id = Db.AddFolder(volumeId, pathInVolume, full, name);
        var folder = Db.Folders().First(f => f.Id == id);
        Log.Info("library", $"添加音乐文件夹 {full}（{volumeId}{pathInVolume}）");
        Watcher.Watch(folder);
        ScanFolderCore(folder, null, false);
        RebuildSnapshot();
        Dispatcher.UIThread.Post(() => FolderAdded?.Invoke(folder));
    });

    /// <summary>移除音乐文件夹：其中的歌从曲库消失，播放列表里的条目保留，显示为不可用（需求 L-2）。</summary>
    public Task RemoveFolderAsync(long id) => Enqueue(() =>
    {
        Watcher.Unwatch(id);
        Db.RemoveFolder(id);
        RebuildSnapshot();
    });

    // ── 扫描 ──

    /// <summary>"重新扫描"（增量）或"重建曲库"（全量重新解析，需求 L-6）。</summary>
    public Task RescanAsync(bool full) => Enqueue(() =>
    {
        var changed = full;
        foreach (var f in Db.Folders().Where(f => f.Available))
        {
            if (full) Db.ClearFolder(f.Id);
            changed |= ScanFolderCore(f, null, full).Changed;
        }
        if (changed) RebuildSnapshot();
    });

    void ScanFolder(long id, IReadOnlyCollection<string>? dirs, bool full)
    {
        if (Db.Folders().FirstOrDefault(f => f.Id == id) is not { Available: true } folder) return;
        if (ScanFolderCore(folder, dirs, full).Changed) RebuildSnapshot();
    }

    ScanResult ScanFolderCore(LibraryFolder folder, IReadOnlyCollection<string>? dirs, bool full)
    {
        Scanning = true;
        try
        {
            var result = _scanner.Scan(folder, new TrackBuilder(CurrentConfig()), dirs, full, _cts.Token);
            if (dirs is null) UnsupportedCount = result.Unsupported;
            Log.Info("scan", $"{folder.DisplayName}：新增 {result.Added}，更新 {result.Updated}，删除 {result.Deleted}，读取失败 {result.Failed}");
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("scan", $"扫描 {folder.Root} 出错", e);
            return new ScanResult(0, 0, 0, 0, 0);
        }
        finally
        {
            Scanning = false;
            Dispatcher.UIThread.Post(() => ScanProgress?.Invoke(0, 0));
        }
    }

    /// <summary>曲库规则（分隔符、例外名单、文件名推断）改了：用数据库里的原始字段重算派生字段，不用重读文件（需求 SY-4）。</summary>
    public Task ApplyRulesAsync() => Enqueue(() =>
    {
        var builder = new TrackBuilder(CurrentConfig());
        Db.UpdateDerived(Db.AllTracks().Select(builder.Rebuild));
        RebuildSnapshot();
    });

    // ── ITrackSource ──

    readonly System.Collections.Concurrent.ConcurrentDictionary<long, (Track track, string path)> _external = new();
    long _externalId;

    /// <summary>直接打开的文件仅放在临时队列，不自动把整个目录加入曲库。</summary>
    public Track OpenExternal(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Scanner.AudioExtensions.Contains(Path.GetExtension(full))) throw new NotSupportedException("不支持的音频格式");
        var known = Snapshot.Tracks.FirstOrDefault(t => !t.IsCue && string.Equals(Snapshot.PathOf(t), full, StringComparison.OrdinalIgnoreCase));
        if (known is not null) return known;
        foreach (var entry in _external.Values)
            if (entry.path.Equals(full, StringComparison.OrdinalIgnoreCase)) return entry.track;
        var info = new FileInfo(full);
        var directory = info.DirectoryName!;
        var (volume, relative) = Volumes.Locate(directory);
        var folder = new LibraryFolder { Id = 0, VolumeId = volume, PathInVolume = relative, Root = Volumes.WithSlash(directory), DisplayName = directory };
        TrackTags? tags = null;
        try { tags = TagReader.Read(full); }
        catch (Exception e) { Log.Warn("open", "标签读取失败，仍尝试播放", e); }
        var t = new TrackBuilder(CurrentConfig()).Build(new AudioFile(folder, "", info.Name, info.LastWriteTimeUtc.Ticks, info.Length), tags, Interlocked.Decrement(ref _externalId));
        _external[t.Id] = (t, full);
        return t;
    }

    public Track? ById(long id) => Snapshot.ById.GetValueOrDefault(id) ?? (_external.TryGetValue(id, out var e) ? e.track : null);
    public Track? ByKey(string trackKey) => Snapshot.ByKey.GetValueOrDefault(trackKey) ?? _external.Values.FirstOrDefault(e => e.track.TrackKey == trackKey).track;
    public string? PathOf(Track track) => _external.TryGetValue(track.Id, out var e) ? e.path : Snapshot.PathOf(track);

    public void Dispose()
    {
        _cts.Cancel();
        _memory.Dispose();
        Watcher.Dispose();
        _work.Wait(); // Workers only post UI updates; cancellation makes this independent of the dispatcher.
        _work.Release();
    }
}
