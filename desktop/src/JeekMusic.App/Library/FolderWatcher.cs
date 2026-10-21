using JeekMusic.Infrastructure;

namespace JeekMusic.Library;

/// <summary>
/// 监听音乐文件夹的变化（需求 L-5，设计 §5.2）。同一目录 2 秒内的多次变化只处理一次（复制大文件时会连续触发 Changed）；
/// 缓冲区溢出时整个文件夹扫一次。<c>.jeekmusic\sync\</c> 下的变化不进曲库，转给同步（设计 §11.3）。
/// 回调都在后台线程上。
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    readonly Dictionary<long, FileSystemWatcher> _watchers = new();
    readonly Dictionary<long, HashSet<string>> _pending = new();
    readonly HashSet<long> _syncPending = new();
    readonly Lock _lock = new();
    readonly Timer _timer;

    /// <summary>音乐文件夹里这些目录（相对路径，"\" 结尾）有变化。</summary>
    public event Action<long, IReadOnlyCollection<string>>? DirsChanged;
    /// <summary>某个音乐文件夹的 .jeekmusic\sync\ 有变化。</summary>
    public event Action<long>? SyncChanged;
    /// <summary>监听出错（盘拔掉了、网络断了）。</summary>
    public event Action<long>? Failed;

    /// <summary>暂时忽略这些路径的变化（自己在改名、移动、写入封面时）。</summary>
    readonly Dictionary<string, long> _ignored = new(StringComparer.OrdinalIgnoreCase);

    public FolderWatcher() => _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

    public void Watch(LibraryFolder folder)
    {
        Unwatch(folder.Id);
        try
        {
            var w = new FileSystemWatcher(folder.Root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            var id = folder.Id;
            var root = folder.Root;
            void OnChange(string fullPath)
            {
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
                lock (_lock)
                    if (_ignored.TryGetValue(fullPath, out var until) && Environment.TickCount64 < until) return;
                var rel = fullPath[root.Length..];
                if (rel.Equals(".jeekmusic", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(@".jeekmusic\", StringComparison.OrdinalIgnoreCase) || rel.Contains(@"\.jeekmusic\", StringComparison.OrdinalIgnoreCase))
                {
                    if (rel.Contains(@"sync\", StringComparison.OrdinalIgnoreCase)) lock (_lock) { _syncPending.Add(id); Arm(); }
                    return;
                }
                var slash = rel.LastIndexOf('\\');
                var dir = slash < 0 ? "" : rel[..(slash + 1)];
                lock (_lock)
                {
                    if (!_pending.TryGetValue(id, out var set)) _pending[id] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(dir);
                    Arm();
                }
            }
            w.Created += (_, e) => OnChange(e.FullPath);
            w.Changed += (_, e) => OnChange(e.FullPath);
            w.Deleted += (_, e) => OnChange(e.FullPath);
            w.Renamed += (_, e) => { OnChange(e.OldFullPath); OnChange(e.FullPath); };
            w.Error += (_, e) =>
            {
                if (e.GetException() is InternalBufferOverflowException)
                {
                    lock (_lock) { _pending[id] = new(StringComparer.OrdinalIgnoreCase) { "" }; Arm(); }
                    return;
                }
                Log.Warn("watch", $"监听出错：{root}", e.GetException());
                Failed?.Invoke(id);
            };
            w.EnableRaisingEvents = true;
            _watchers[folder.Id] = w;
        }
        catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException)
        {
            Log.Warn("watch", $"无法监听 {folder.Root}", e);
        }
    }

    void Arm() => _timer.Change(2000, Timeout.Infinite);

    void Fire()
    {
        List<(long, IReadOnlyCollection<string>)> dirs;
        List<long> sync;
        lock (_lock)
        {
            dirs = _pending.Select(kv => (kv.Key, (IReadOnlyCollection<string>)kv.Value.ToList())).ToList();
            _pending.Clear();
            sync = _syncPending.ToList();
            _syncPending.Clear();
        }
        foreach (var (id, set) in dirs) DirsChanged?.Invoke(id, set);
        foreach (var id in sync) SyncChanged?.Invoke(id);
    }

    /// <summary>接下来几秒内忽略这个路径的变化。</summary>
    public void Ignore(string fullPath, int milliseconds = 5000)
    {
        lock (_lock) _ignored[fullPath] = Environment.TickCount64 + milliseconds;
    }

    public void Unwatch(long folderId)
    {
        if (_watchers.Remove(folderId, out var w)) w.Dispose();
    }

    public void Dispose()
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
        _timer.Dispose();
    }
}
