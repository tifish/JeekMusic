using System.Collections.Concurrent;
using JeekMusic.Infrastructure;
using JeekMusic.Platform;

namespace JeekMusic.Library;

public sealed record ScanResult(int Added, int Updated, int Deleted, int Unsupported, int Failed)
{
    public bool Changed => Added + Updated + Deleted > 0;
}

/// <summary>
/// 曲库扫描（需求 L-5 ~ L-7，设计 §5.1）。枚举文件 → 和数据库比较（修改时间、大小）→ 新增和变化的并行解析 → 分批写入。
/// 解析在若干个低优先级线程上进行（读文件、读标签），写数据库只在调用线程上（SQLite 单连接）。
/// </summary>
public sealed class Scanner(LibraryDb db)
{
    /// <summary>支持的格式（需求 O-5）：libmpv 能解码的常见音频格式。</summary>
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".aif", ".aiff", ".ape", ".wma", ".tak", ".wv", ".mpc",
    };

    /// <summary>看起来是音频、但还不支持的格式（DSD 是 P2）：统计个数在设置里显示。</summary>
    static readonly HashSet<string> UnsupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".dsf", ".dff", ".dts", ".ac3", ".mka", ".amr", ".mid", ".midi" };

    const int BatchSize = 500;

    /// <summary>进度：已处理数、总数（在调用线程上触发）。</summary>
    public event Action<int, int>? Progress;

    /// <summary>
    /// 扫描一个音乐文件夹。<paramref name="scopes"/> 为 null 时扫整个文件夹，否则只扫这些目录（相对路径，"\" 结尾，递归）。
    /// <paramref name="full"/> 为 true 时所有文件都重新解析（"重建曲库"）。
    /// </summary>
    public ScanResult Scan(LibraryFolder folder, TrackBuilder builder, IReadOnlyCollection<string>? scopes, bool full, CancellationToken ct)
    {
        // Enumeration and missing-record deletion must not observe the name gap
        // of a live in-process tag commit after the recovery check returned.
        lock (Online.TagWriteCommit.Gate) return ScanCore(folder, builder, scopes, full, ct);
    }

    ScanResult ScanCore(LibraryFolder folder, TrackBuilder builder, IReadOnlyCollection<string>? scopes, bool full, CancellationToken ct)
    {
        var root = folder.Root;
        if (!Online.TagWriteRecovery.RecoverBeforeScan(root)) return new(0, 0, 0, 0, 1);
        var audio = new Dictionary<string, (long mtime, long size)>(StringComparer.OrdinalIgnoreCase);
        var cues = new Dictionary<string, (long mtime, long size)>(StringComparer.OrdinalIgnoreCase);
        var dirFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unsupported = 0;
        var scopeList = scopes?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [""];
        // 嵌套的范围只保留最外层
        scopeList = scopeList.Where(s => !scopeList.Any(o => o.Length < s.Length && s.StartsWith(o, StringComparison.OrdinalIgnoreCase))).ToList();

        foreach (var scope in scopeList)
        {
            var dir = new DirectoryInfo(root + scope);
            if (!dir.Exists) continue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
                ReturnSpecialDirectories = false,
            };
            foreach (var f in dir.EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                var rel = f.FullName[root.Length..];
                if (rel.Contains(@"\.jeekmusic\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(@".jeekmusic\", StringComparison.OrdinalIgnoreCase)) continue;
                var ext = f.Extension;
                var slash = rel.LastIndexOf('\\');
                var relDir = slash < 0 ? "" : rel[..(slash + 1)];
                if (!dirFiles.TryGetValue(relDir, out var names)) dirFiles[relDir] = names = new();
                names.Add(f.Name);
                var state = (f.LastWriteTimeUtc.Ticks, f.Length);
                if (AudioExtensions.Contains(ext)) audio[rel] = state;
                else if (ext.Equals(".cue", StringComparison.OrdinalIgnoreCase)) cues[rel] = state;
                else if (UnsupportedExtensions.Contains(ext)) unsupported++;
            }
        }

        bool InScope(string rel) => scopeList.Any(s => rel.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        var existing = db.FileStates(folder.Id).Where(kv => InScope(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var existingCues = db.SideFiles(folder.Id).Where(kv => InScope(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        // .cue 新增、改了或删了的目录：里面的整轨文件要重新解析
        var cueDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, state) in cues)
            if (!existingCues.TryGetValue(rel, out var old) || old != state) cueDirs.Add(DirOf(rel));
        foreach (var rel in existingCues.Keys)
            if (!cues.ContainsKey(rel)) cueDirs.Add(DirOf(rel));

        var toParse = audio.Where(kv => full || !existing.TryGetValue(kv.Key, out var old) || old != kv.Value
                                        || cueDirs.Contains(DirOf(kv.Key)) && CueLocator.ImageExtensions.Contains(Path.GetExtension(kv.Key)))
            .Select(kv => kv.Key).ToList();
        var toDelete = existing.Keys.Where(k => !audio.ContainsKey(k)).ToList();
        Log.Info("scan", $"{folder.DisplayName}{(scopes is null ? "" : $"（{scopeList.Count} 个目录）")}：{audio.Count} 个文件，解析 {toParse.Count}，删除 {toDelete.Count}");

        var added = toParse.Count(k => !existing.ContainsKey(k));
        var failed = 0;
        var done = 0;
        var total = toParse.Count;
        Progress?.Invoke(0, total);

        // 解析：工作线程读标签，结果交给这里写数据库
        var results = new BlockingCollection<(string rel, TrackTags? tags, (Core.Library.CueSheet, Core.Library.CueFile)? cue)>(BatchSize * 2);
        var queue = new ConcurrentQueue<string>(toParse);
        var workers = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount / 2, 1, 6)).Select(_ => new Thread(() =>
        {
            SystemState.BeginBackgroundThread();
            try
            {
                while (!ct.IsCancellationRequested && queue.TryDequeue(out var rel))
                {
                    TrackTags? tags = null;
                    try { tags = TagReader.Read(root + rel); }
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref failed);
                        Log.Warn("scan", $"读取标签失败：{rel}：{e.GetType().Name} {e.Message}");
                    }
                    var (mtime, size) = audio[rel];
                    var file = MakeFile(folder, rel, mtime, size);
                    var cue = CueLocator.Resolve(file, tags, dirFiles.GetValueOrDefault(file.Dir) ?? []);
                    results.Add((rel, tags, cue), ct);
                }
            }
            catch (OperationCanceledException) { }
            finally { SystemState.EndBackgroundThread(); }
        }) { IsBackground = true, Name = "scan-worker" }).ToList();
        foreach (var w in workers) w.Start();
        _ = Task.Run(() =>
        {
            foreach (var w in workers) w.Join();
            results.CompleteAdding();
        });

        var batch = new List<(string rel, TrackTags? tags, (Core.Library.CueSheet, Core.Library.CueFile)? cue)>(BatchSize);
        void Flush()
        {
            if (batch.Count == 0) return;
            lock (db.Gate)
            {
                using var tx = db.Begin();
                foreach (var (rel, tags, cue) in batch)
                {
                    var (mtime, size) = audio[rel];
                    var file = MakeFile(folder, rel, mtime, size);
                    var ids = db.ExistingIds(tx, folder.Id, file.Dir, file.FileName);
                    IReadOnlyList<Track> tracks = cue is var (sheet, cueFile)
                        ? builder.BuildCue(file, tags, sheet, cueFile, ids)
                        : [builder.Build(file, tags, ids.GetValueOrDefault(Volumes.TrackKey(folder.VolumeId, folder.PathInVolume, rel)))];
                    db.ReplaceFile(tx, folder.Id, file.Dir, file.FileName, tracks);
                }
                tx.Commit();
            }
            done += batch.Count;
            batch.Clear();
            Progress?.Invoke(done, total);
        }
        foreach (var r in results.GetConsumingEnumerable())
        {
            batch.Add(r);
            if (batch.Count >= BatchSize) Flush();
        }
        ct.ThrowIfCancellationRequested();
        Flush();

        lock (db.Gate)
        {
            using var tx = db.Begin();
            foreach (var rel in toDelete) db.DeleteFile(tx, folder.Id, rel);
            foreach (var (rel, state) in cues)
                if (!existingCues.TryGetValue(rel, out var old) || old != state) db.SetSideFile(tx, folder.Id, rel, state.mtime, state.size);
            foreach (var rel in existingCues.Keys)
                if (!cues.ContainsKey(rel)) db.DeleteSideFile(tx, folder.Id, rel);
            tx.Commit();
        }
        return new ScanResult(added, toParse.Count - added, toDelete.Count, unsupported, failed);
    }

    static AudioFile MakeFile(LibraryFolder folder, string rel, long mtime, long size) => new(folder, DirOf(rel), rel[(rel.LastIndexOf('\\') + 1)..], mtime, size);

    static string DirOf(string rel)
    {
        var slash = rel.LastIndexOf('\\');
        return slash < 0 ? "" : rel[..(slash + 1)];
    }
}
