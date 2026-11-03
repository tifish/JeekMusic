using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JeekMusic.Core.Sync;

/// <summary>Read-only source boundary. No request ever supplies an absolute filesystem path.</summary>
public static partial class LanFiles
{
    public sealed record Budget(int Files = 100_000, long Bytes = 500L * 1024 * 1024 * 1024, int Visited = 250_000, long Memory = 64L * 1024 * 1024);
    public sealed record Entry(string Path, long Size, long ModifiedNs, string Hash, IReadOnlyList<string>? Dependencies = null);
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".flac", ".m4a", ".mp4", ".aac", ".ogg", ".oga", ".opus", ".wav", ".mka", ".amr", ".cue", ".lrc", ".jpg", ".jpeg", ".png", ".webp" };

    public static string Relative(string value, bool allowRoot = false)
    {
        if (value == "" && allowRoot) return value;
        if (value.Length > 2048 || value.Contains('\\') || value.StartsWith('/') || value.Contains(':')) throw new IOException("Unsafe relative path");
        foreach (var part in value.Split('/'))
            if (part.Length == 0 || part is "." or ".." || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>\"|?*".Contains(c)) ||
                Reserved().IsMatch(part)) throw new IOException("Unsafe path component");
        return value;
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reserved();
    [GeneratedRegex("^\\s*FILE\\s+(?:\"([^\"]+)\"|(\\S+))\\s+\\S+\\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CueFile();

    public static string Resolve(string root, string relative, bool allowRoot = false)
    {
        Relative(relative, allowRoot);
        root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        var path = root;
        // Check ancestors too: an explicitly selected root inside a junction is still an escape.
        for (var dir = new DirectoryInfo(root); dir != null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse-point roots are not shared");
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = System.IO.Path.Combine(path, part);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Links are not shared");
        }
        return path;
    }

    public static IReadOnlyList<string> Directories(string root, string relative, CancellationToken cancel = default)
    {
        var path = Resolve(root, relative, true);
        var names = new List<string>(); var visited = 0;
        foreach (var child in Directory.EnumerateDirectories(path))
        {
            cancel.ThrowIfCancellationRequested();
            if (++visited > 100_000) throw new IOException("Directory budget exceeded; use smaller folders");
            var name = System.IO.Path.GetFileName(child); if (!name.StartsWith('.')) names.Add(name);
        }
        CheckCollisions(names);
        return names.Where(n => { try { Resolve(root, Join(relative, n)); return true; } catch (IOException) { return false; } }).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string Join(string directory, string name) => directory.Length == 0 ? name : directory + "/" + name;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    public static FileStream OpenRead(string root, string relative)
    {
        var path = Resolve(root, relative);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var buffer = new StringBuilder(32768);
                var count = GetFinalPathNameByHandleW(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
                var actual = buffer.ToString();
                if (actual.StartsWith(@"\\?\UNC\")) actual = @"\\" + actual[8..];
                else if (actual.StartsWith(@"\\?\")) actual = actual[4..];
                if (count == 0 || count >= buffer.Capacity || !actual.Equals(System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Opened source handle escaped its shared path");
            }
            Resolve(root, relative); // Recheck links after opening; the handle then excludes delete/rename.
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    public static void CheckCollisions(IEnumerable<string> paths)
    {
        if (paths.GroupBy(p => p.Normalize(NormalizationForm.FormC), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new IOException("Case or Unicode path collision");
    }

    public static async Task<IReadOnlyList<Entry>> Manifest(string root, IEnumerable<string> selection, CancellationToken cancel = default, Budget? budget = null, bool metadataOnly = true, string? cueCacheDirectory = null)
    {
        budget ??= new();
        long workMemory = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = new Dictionary<string, List<string>>();
        int visited = 0, dependencyCount = 0;
        var walked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            cancel.ThrowIfCancellationRequested();
            if (paths.Add(path)) workMemory += 192L + path.Length * 2L;
            if (workMemory > budget.Memory) throw new IOException("Manifest memory budget exceeded; choose smaller folders");
            if (paths.Count > budget.Files) throw new IOException("Selection exceeds 100000 files; choose smaller folders");
        }
        void Walk(string relative, int depth = 0)
        {
            cancel.ThrowIfCancellationRequested();
            if (depth > 64) throw new IOException("Directory depth exceeded");
            if (!walked.Add(relative)) return;
            var dir = Resolve(root, relative, true);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in Directory.EnumerateFileSystemEntries(dir))
            {
                cancel.ThrowIfCancellationRequested();
                if (++visited > budget.Visited) throw new IOException("Enumeration budget exceeded; choose smaller folders");
                var name = System.IO.Path.GetFileName(child);
                if (name.StartsWith('.')) continue;
                if (!names.Add(name.Normalize(NormalizationForm.FormC))) throw new IOException("Case or Unicode path collision");
                var rel = Join(relative, name);
                var attrs = File.GetAttributes(child);
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                Relative(rel);
                if ((attrs & FileAttributes.Directory) != 0) Walk(rel, depth + 1);
                else if (Extensions.Contains(System.IO.Path.GetExtension(child))) Add(rel);
            }
        }
        Resolve(root,"",true); // An unavailable root must never become an empty snapshot.
        foreach (var selected in selection.Distinct()) {
            var path=Relative(selected,true);
            try { Resolve(root,path,true); }
            catch(Exception e) when(path.Length>0 && e is FileNotFoundException or DirectoryNotFoundException) { continue; }
            Walk(path);
        }
        // CUE dependencies may be outside the selected subdirectory, but never outside its root.
        foreach (var cue in paths.Where(p => (!metadataOnly || cueCacheDirectory!=null) && p.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var required = new List<string>(); dependencies[cue] = required;
            var full = Resolve(root, cue);
            if (new FileInfo(full).Length > 2_000_000) throw new IOException("CUE too large");
            string text;
            if(cueCacheDirectory!=null)text=await LanCueMetadataCache.Text(cueCacheDirectory,root,cue,cancel);
            else { using var cueStream = OpenRead(root, cue);using var cueReader = new StreamReader(cueStream, Encoding.UTF8, true);text = await cueReader.ReadToEndAsync(cancel);Interlocked.Add(ref FileMetadata.SidecarParseBytes,cueStream.Position); }
            if (text.Contains('\uFFFD')) throw new IOException("CUE encoding cannot be safely resolved; convert to UTF-8");
            if (Regex.Matches(text, @"^\s*FILE\b", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count != CueFile().Matches(text).Count)
                throw new IOException("Malformed CUE FILE declaration");
            var cueModel = JeekMusic.Core.Library.CueParser.Parse(text);
            foreach (Match m in CueFile().Matches(text))
            {
                var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                name = name.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains(':')) throw new IOException("Rooted CUE FILE path");
                var dependency = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full)!, name));
                var dep = System.IO.Path.GetRelativePath(System.IO.Path.GetFullPath(root), dependency).Replace('\\', '/');
                // Dot segments are permitted only after normalization proves they remain inside this root.
                Relative(dep);
                var audio = Resolve(root, dep);
                var audioExtension = System.IO.Path.GetExtension(audio).ToLowerInvariant();
                if (!File.Exists(audio) || !Extensions.Contains(audioExtension) || audioExtension is ".cue" or ".lrc" or ".jpg" or ".jpeg" or ".png" or ".webp") throw new IOException("CUE audio dependency unavailable");
                Add(dep);
                if (++dependencyCount > 100_000 || required.Count >= 1000 || (workMemory += 32L) > budget.Memory) throw new IOException("CUE dependency budget exceeded");
                required.Add(dep);
                var directory = System.IO.Path.GetDirectoryName(dep)?.Replace('\\', '/') ?? "";
                var stem = System.IO.Path.GetFileNameWithoutExtension(audio);
                var lyricNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stem + ".lrc" };
                foreach (var track in cueModel.Files.SelectMany(f => f.Tracks).Where(t => t.Title != null))
                {
                    var title = Regex.Replace(track.Title!, "[\\\\/:*?\"<>|]", "_").Trim();
                    lyricNames.Add(title + ".lrc"); lyricNames.Add($"{track.Number:00}. {title}.lrc");
                }
                foreach (var sidecar in Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(audio)!))
                {
                    cancel.ThrowIfCancellationRequested(); if (++visited > budget.Visited) throw new IOException("Enumeration budget exceeded");
                    if (lyricNames.Contains(System.IO.Path.GetFileName(sidecar)) ||
                        new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(System.IO.Path.GetExtension(sidecar).ToLowerInvariant()) &&
                        (System.IO.Path.GetFileNameWithoutExtension(sidecar).Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                            new[] { "cover", "folder", "front", "albumart", "album" }.Contains(System.IO.Path.GetFileNameWithoutExtension(sidecar).ToLowerInvariant())))
                    { var relative = Join(directory, System.IO.Path.GetFileName(sidecar)); Resolve(root, relative); Add(relative); if (++dependencyCount > 100_000 || required.Count >= 1000 || (workMemory += 32L) > budget.Memory) throw new IOException("CUE dependency budget exceeded"); required.Add(relative); }
                }
            }
        }
        CheckCollisions(paths);
        if (paths.Count > budget.Files) throw new IOException("Selection exceeds 100000 files; choose smaller folders");
        long bytes = 0;
        var entries = new List<Entry>();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            cancel.ThrowIfCancellationRequested();
            var full = Resolve(root, path);
            using var stream = metadataOnly ? null : OpenRead(root, path);
            var info = new FileInfo(full);
            var size = info.Length; var ticks = info.LastWriteTimeUtc.Ticks;
            bytes = checked(bytes + size);
            if (bytes > budget.Bytes) throw new IOException("Selection exceeds 500 GiB; choose smaller folders");
            Interlocked.Increment(ref FileMetadata.MetadataObservations);
            var hash = metadataOnly ? "" : Convert.ToHexStringLower(await FileMetadata.Digest(stream!, cancel));
            info.Refresh();
            if (info.Length != size || info.LastWriteTimeUtc.Ticks != ticks) throw new IOException("Source changed during verification; retry");
            entries.Add(new(path, size, checked((ticks - DateTime.UnixEpoch.Ticks) * 100), hash, dependencies.GetValueOrDefault(path)));
        }
        return entries;
    }
}
