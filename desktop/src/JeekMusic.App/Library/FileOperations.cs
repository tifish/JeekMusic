using JeekMusic.Infrastructure;
using JeekMusic.Platform;

namespace JeekMusic.Library;

public sealed record FileOpResult(int Done, int Skipped, string? Error)
{
    public string Summary(string verb) =>
        Error ?? (Skipped > 0 ? L.F($"已{verb} {Done} 首，跳过 {Skipped} 首（CUE 整轨里的歌、正在播放的歌不能单独操作）") : L.F($"已{verb} {Done} 首"));
}

/// <summary>
/// 文件操作（需求 F-10，设计 §5.4）：改名、移动、删除（移到回收站）。同名 .lrc 和图片一起处理；改名、移动保持文件的修改时间，
/// 播放列表、手动修正、分析结果、歌词偏移等记录跟着歌曲走（track_key 一起改）。CUE 整轨里的歌共用一个文件，不能单独操作。
/// 正在播放（mpv 打开着）的文件 Windows 不让改名和移动，也跳过。
/// </summary>
public sealed class FileOperations(LibraryService library)
{
    static readonly string[] SidecarExtensions = [".lrc", ".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>同名的附带文件（歌词、图片）。</summary>
    static IEnumerable<string> Sidecars(string audioPath)
    {
        var dir = Path.GetDirectoryName(audioPath)!;
        var stem = Path.GetFileNameWithoutExtension(audioPath);
        foreach (var ext in SidecarExtensions)
        {
            var p = Path.Combine(dir, stem + ext);
            if (File.Exists(p)) yield return p;
        }
    }

    /// <summary>隐藏目录里这首歌的歌词（.jeekmusic\lyrics\&lt;文件名&gt;.lrc，同移动端 LY-11）。</summary>
    static string HiddenLyrics(LibraryFolder folder, string fileName) => Path.Combine(folder.Root, ".jeekmusic", "lyrics", Path.GetFileNameWithoutExtension(fileName) + ".lrc");

    public FileOpResult Rename(Track track, string newStem, long? playingId)
    {
        if (track.IsCue || track.Id == playingId) return new(0, 1, null);
        var snapshot = library.Snapshot;
        if (snapshot.Folder(track.FolderId) is not { Available: true } folder) return new(0, 0, L.T("文件所在的盘不可用"));
        var oldPath = folder.Root + track.RelativePath;
        var newName = newStem.Trim() + Path.GetExtension(track.FileName);
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return new(0, 0, L.T("文件名里有不能用的字符"));
        var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return new(0, 0, null);
        if (File.Exists(newPath) && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return new(0, 0, L.T("已经有同名的文件"));
        try
        {
            MoveKeepingTime(oldPath, newPath);
            foreach (var side in Sidecars(oldPath).ToList())
                MoveKeepingTime(side, Path.Combine(Path.GetDirectoryName(newPath)!, Path.GetFileNameWithoutExtension(newName) + Path.GetExtension(side)));
            var hidden = HiddenLyrics(folder, track.FileName);
            if (File.Exists(hidden)) MoveKeepingTime(hidden, HiddenLyrics(folder, newName));
            UpdateDb(track, folder, folder, track.Dir, newName, newPath);
            return new(1, 0, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("fileops", $"改名失败 {oldPath}", e);
            return new(0, 0, L.T("改名失败：") + e.Message);
        }
    }

    /// <summary>移动到 <paramref name="targetDir"/>（必须在某个音乐文件夹里）。</summary>
    public FileOpResult Move(IReadOnlyList<Track> tracks, string targetDir, long? playingId)
    {
        var snapshot = library.Snapshot;
        targetDir = Volumes.WithSlash(Path.GetFullPath(targetDir));
        var target = snapshot.Folders.Where(f => f.Available && targetDir.StartsWith(f.Root, StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.Root.Length);
        if (target is null) return new(0, 0, L.T("只能移动到音乐文件夹里"));
        var relDir = targetDir[target.Root.Length..];
        int done = 0, skipped = 0;
        string? error = null;
        Directory.CreateDirectory(targetDir);
        foreach (var t in tracks.DistinctBy(t => t.TrackKey))
        {
            if (t.IsCue || t.Id == playingId) { skipped++; continue; }
            if (snapshot.Folder(t.FolderId) is not { Available: true } source) { skipped++; continue; }
            var oldPath = source.Root + t.RelativePath;
            var newPath = targetDir + t.FileName;
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(newPath)) { error = L.F($"目标文件夹里已经有「{t.FileName}」"); skipped++; continue; }
            try
            {
                MoveKeepingTime(oldPath, newPath);
                foreach (var side in Sidecars(oldPath).ToList()) MoveKeepingTime(side, targetDir + Path.GetFileName(side));
                if (source.Id != target.Id)
                {
                    // 移到另一个音乐文件夹：隐藏目录里的歌词也搬过去
                    var hidden = HiddenLyrics(source, t.FileName);
                    if (File.Exists(hidden))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(HiddenLyrics(target, t.FileName))!);
                        MoveKeepingTime(hidden, HiddenLyrics(target, t.FileName));
                    }
                }
                UpdateDb(t, source, target, relDir, t.FileName, newPath);
                done++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn("fileops", $"移动失败 {oldPath}", e);
                error = L.T("移动失败：") + e.Message;
                skipped++;
            }
        }
        library.RebuildSnapshot();
        return new(done, skipped, error);
    }

    /// <summary>移到回收站（同名 .lrc、图片一起）。播放列表里的条目保留，显示为不可用（需求 F-10）。</summary>
    public FileOpResult Delete(IReadOnlyList<Track> tracks, long? playingId, nint owner)
    {
        var snapshot = library.Snapshot;
        var paths = new List<string>();
        var deleted = new List<Track>();
        var skipped = 0;
        foreach (var t in tracks.DistinctBy(t => t.TrackKey))
        {
            if (t.IsCue || t.Id == playingId || snapshot.PathOf(t) is not { } path) { skipped++; continue; }
            paths.Add(path);
            paths.AddRange(Sidecars(path));
            deleted.Add(t);
        }
        if (paths.Count == 0) return new(0, skipped, null);
        foreach (var p in paths) library.Watcher.Ignore(p);
        if (!SystemState.MoveToRecycleBin(paths, owner)) return new(0, skipped, L.T("删除失败（文件可能被其他程序占用）"));
        lock (library.Db.Gate)
        {
            using var tx = library.Db.Begin();
            foreach (var t in deleted) library.Db.DeleteFile(tx, t.FolderId, t.RelativePath);
            tx.Commit();
        }
        library.RebuildSnapshot();
        return new(deleted.Count, skipped, null);
    }

    void MoveKeepingTime(string from, string to)
    {
        var time = File.GetLastWriteTimeUtc(from);
        library.Watcher.Ignore(from);
        library.Watcher.Ignore(to);
        File.Move(from, to);
        File.SetLastWriteTimeUtc(to, time);
    }

    void UpdateDb(Track t, LibraryFolder source, LibraryFolder target, string newDir, string newName, string newPath)
    {
        var mtime = File.GetLastWriteTimeUtc(newPath).Ticks;
        var newKeyBase = Volumes.TrackKey(target.VolumeId, target.PathInVolume, newDir + newName);
        var oldKeyBase = Volumes.TrackKey(source.VolumeId, source.PathInVolume, t.RelativePath);
        lock (library.Db.Gate)
        {
            using var tx = library.Db.Begin();
            library.Db.MoveTrackFile(tx, source.Id, t.RelativePath, target.Id, newDir, newName,
                oldKey => newKeyBase + oldKey[oldKeyBase.Length..], mtime, Core.Text.SortKeys.SortKey(newName));
            tx.Commit();
        }
        if (source.Id == target.Id && newDir == t.Dir) library.RebuildSnapshot();
    }
}
