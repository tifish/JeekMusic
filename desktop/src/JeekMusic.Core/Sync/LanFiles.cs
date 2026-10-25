using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JeekMusic.Core.Sync;

/// <summary>Read-only source boundary. No request ever supplies an absolute filesystem path.</summary>
public static partial class LanFiles
{
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

    public static IReadOnlyList<string> Directories(string root, string relative)
    {
        var path = Resolve(root, relative, true);
        var names = Directory.EnumerateDirectories(path).Where(p => !System.IO.Path.GetFileName(p).StartsWith('.'))
            .Select(p => System.IO.Path.GetFileName(p)).ToArray();
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

    public static async Task<IReadOnlyList<Entry>> Manifest(string root, IEnumerable<string> selection, CancellationToken cancel = default)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = new Dictionary<string, List<string>>();
        void Walk(string relative)
        {
            cancel.ThrowIfCancellationRequested();
            var dir = Resolve(root, relative, true);
            var children = Directory.EnumerateFileSystemEntries(dir).Where(p => !System.IO.Path.GetFileName(p).StartsWith('.')).ToArray();
            CheckCollisions(children.Select(System.IO.Path.GetFileName)!);
            foreach (var child in children)
            {
                var rel = Join(relative, System.IO.Path.GetFileName(child));
                var attrs = File.GetAttributes(child);
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                Relative(rel);
                if ((attrs & FileAttributes.Directory) != 0) Walk(rel);
                else if (Extensions.Contains(System.IO.Path.GetExtension(child))) paths.Add(rel);
            }
        }
        foreach (var selected in selection.Distinct()) Walk(Relative(selected, true));
        // CUE dependencies may be outside the selected subdirectory, but never outside its root.
        foreach (var cue in paths.Where(p => p.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var required = new List<string>(); dependencies[cue] = required;
            var full = Resolve(root, cue);
            if (new FileInfo(full).Length > 2_000_000) throw new IOException("CUE too large");
            using var cueStream = OpenRead(root, cue);
            using var cueReader = new StreamReader(cueStream, Encoding.UTF8, true);
            var text = await cueReader.ReadToEndAsync(cancel);
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
                paths.Add(dep);
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
                    if (lyricNames.Contains(System.IO.Path.GetFileName(sidecar)) ||
                        new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(System.IO.Path.GetExtension(sidecar).ToLowerInvariant()) &&
                        (System.IO.Path.GetFileNameWithoutExtension(sidecar).Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                            new[] { "cover", "folder", "front", "albumart", "album" }.Contains(System.IO.Path.GetFileNameWithoutExtension(sidecar).ToLowerInvariant())))
                    { var relative = Join(directory, System.IO.Path.GetFileName(sidecar)); Resolve(root, relative); paths.Add(relative); required.Add(relative); }
            }
        }
        CheckCollisions(paths);
        if (paths.Count > 100_000) throw new IOException("Selection exceeds 100000 files; choose smaller folders");
        var entries = new List<Entry>();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            cancel.ThrowIfCancellationRequested();
            var full = Resolve(root, path);
            using var stream = OpenRead(root, path);
            var info = new FileInfo(full);
            var size = info.Length; var ticks = info.LastWriteTimeUtc.Ticks;
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel));
            info.Refresh();
            if (info.Length != size || info.LastWriteTimeUtc.Ticks != ticks) throw new IOException("Source changed during verification; retry");
            entries.Add(new(path, size, checked((ticks - DateTime.UnixEpoch.Ticks) * 100), hash, dependencies.GetValueOrDefault(path)));
        }
        return entries;
    }
}
