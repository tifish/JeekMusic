using System.Text.RegularExpressions;

namespace JeekMusic.Core.Library;

public sealed record CueTrack(int Number, string? Title, string? Performer, long StartMs);

public sealed record CueFile(string Name, IReadOnlyList<CueTrack> Tracks);

public sealed record CueSheet(string? Title, string? Performer, int? Year, string? Genre, IReadOnlyList<CueFile> Files)
{
    /// <summary>每个文件只有一首歌的"分轨 CUE"（需求 CUE-8）：这些文件本身就是单曲，忽略这个 CUE。</summary>
    public bool IsPerTrack => Files.Count > 1 && Files.All(f => f.Tracks.Count <= 1);
}

/// <summary>
/// CUE 解析（需求 CUE-3，移动端 CueParser.kt，移动端设计 §4.6）：
/// 顶层 TITLE / PERFORMER → 专辑名 / 专辑艺术家，REM DATE / REM GENRE；
/// TRACK nn AUDIO 下的 TITLE、PERFORMER（缺省继承顶层）、INDEX 01 mm:ss:ff（每秒 75 帧）；
/// 忽略 INDEX 00：曲间间隙归入上一首，连续播放时不丢音频。
/// </summary>
public static partial class CueParser
{
    public static CueSheet Parse(string text)
    {
        string? title = null, performer = null, genre = null;
        int? year = null;
        var files = new List<CueFile>();

        string? fileName = null;
        var tracks = new List<CueTrack>();
        int? trackNumber = null;
        string? trackTitle = null, trackPerformer = null;
        long? trackStart = null;

        void EndTrack()
        {
            if (trackNumber is not { } number) return;
            if (trackStart is { } start) tracks.Add(new CueTrack(number, trackTitle, trackPerformer ?? performer, start));
            trackNumber = null;
            trackTitle = null;
            trackPerformer = null;
            trackStart = null;
        }

        void EndFile()
        {
            EndTrack();
            if (fileName is not null) files.Add(new CueFile(fileName, tracks));
            fileName = null;
            tracks = [];
        }

        foreach (var raw in Lines(text))
        {
            var match = Line().Match(raw);
            if (!match.Success) continue;
            var command = match.Groups[1].Value.ToUpperInvariant();
            var args = match.Groups[2].Value;
            switch (command)
            {
                case "FILE":
                    EndFile();
                    fileName = ParseFileName(args);
                    break;
                case "TRACK":
                    EndTrack();
                    trackNumber = int.TryParse(Before(args, ' '), out var n) ? n : null;
                    break;
                case "TITLE":
                    if (trackNumber is not null) trackTitle = Unquote(args);
                    else title = Unquote(args);
                    break;
                case "PERFORMER":
                    if (trackNumber is not null) trackPerformer = Unquote(args);
                    else performer = Unquote(args);
                    break;
                case "INDEX":
                    var parts = Whitespace().Split(args);
                    if (parts.Length == 2 && int.TryParse(parts[0], out var index) && index == 1) trackStart = ParseTime(parts[1]);
                    break;
                case "REM":
                    var key = Before(args, ' ').ToUpperInvariant();
                    var space = args.IndexOf(' ');
                    var value = Unquote(space >= 0 ? args[(space + 1)..] : "");
                    if (key == "DATE")
                    {
                        var y = Year().Match(value);
                        year = y.Success && int.TryParse(y.Value, out var parsed) ? parsed : null;
                    }
                    else if (key == "GENRE")
                    {
                        genre = value.Length == 0 ? null : value;
                    }
                    break;
            }
        }
        EndFile();
        return new CueSheet(NullIfEmpty(title), NullIfEmpty(performer), year, genre, files);
    }

    /// <summary>"mm:ss:ff" → 毫秒，ff 是 1/75 秒的帧。</summary>
    public static long? ParseTime(string value)
    {
        var m = Time().Match(value);
        if (!m.Success) return null;
        return long.Parse(m.Groups[1].Value) * 60_000 + long.Parse(m.Groups[2].Value) * 1_000 + long.Parse(m.Groups[3].Value) * 1_000 / 75;
    }

    /// <summary>FILE "name with spaces.wav" WAVE → name with spaces.wav</summary>
    private static string ParseFileName(string args)
    {
        var trimmed = args.Trim();
        if (trimmed.StartsWith('"'))
        {
            var rest = trimmed[1..];
            var quote = rest.IndexOf('"');
            return quote >= 0 ? rest[..quote] : rest;
        }
        var lastSpace = trimmed.LastIndexOf(' ');
        var name = lastSpace >= 0 ? trimmed[..lastSpace] : trimmed;
        return name.Length == 0 ? trimmed : name;
    }

    /// <summary>去掉首尾空白和两侧的引号（Kotlin 的 removeSurrounding：两侧都有才去掉）。</summary>
    private static string Unquote(string value)
    {
        var t = value.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') t = t[1..^1];
        return t.Trim();
    }

    private static string Before(string s, char c)
    {
        var i = s.IndexOf(c);
        return i >= 0 ? s[..i] : s;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>同 Kotlin 的 lineSequence()：按 \r\n、\n、\r 分行。</summary>
    internal static IEnumerable<string> Lines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) yield return line;
        // Kotlin 的 lineSequence 对以换行结尾的文本最后还会给出一个空行；空行在这里不影响结果
    }

    [GeneratedRegex("^\\s*(\\S+)\\s*(.*?)\\s*$")]
    private static partial Regex Line();

    [GeneratedRegex("^(\\d+):(\\d{1,2}):(\\d{1,2})$")]
    private static partial Regex Time();

    [GeneratedRegex("\\d{4}")]
    private static partial Regex Year();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}

/// <summary>整轨文件和它的 CUE 中对应的 FILE 段。</summary>
public sealed record ResolvedCue(CueSheet Sheet, CueFile File);

/// <summary>
/// 为整轨文件找到对应的 CUE（需求 CUE-1、CUE-5、CUE-8，移动端 CueResolver.kt 中与平台无关的部分）。
/// 文件系统的访问（列目录、读 .cue）由调用方提供。
/// </summary>
public static class CueResolver
{
    /// <summary>移动端只认 FLAC / WAV 整轨（需求 CUE-1）。桌面端还可以加上 APE、WavPack、TAK 等（需求 O-5）。</summary>
    public static readonly IReadOnlySet<string> DefaultImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "flac", "wav" };

    /// <summary>外部 .cue 只给时长至少 5 分钟的整轨文件找（有内嵌 cuesheet 时不限时长），大部分单曲目录不需要额外的文件访问。</summary>
    public const long MinDurationMs = 5 * 60_000L;

    public static bool IsImage(string fileName, IReadOnlySet<string>? imageExtensions = null) =>
        (imageExtensions ?? DefaultImageExtensions).Contains(Extension(fileName));

    public static bool IsCandidate(string fileName, long durationMs, IReadOnlySet<string>? imageExtensions = null) =>
        IsImage(fileName, imageExtensions) && durationMs >= MinDurationMs;

    /// <param name="fileName">整轨音频的文件名</param>
    /// <param name="durationMs">整轨音频的时长</param>
    /// <param name="embeddedCue">内嵌的 cuesheet（FLAC 的 CUESHEET / Vorbis comment），没有为 null</param>
    /// <param name="siblingFileNames">同一目录下的文件名（不含子目录）</param>
    /// <param name="readCue">读取并解码同目录的某个 .cue（用 CharsetDetector 识别编码），读不到返回 null</param>
    /// <param name="imageExtensions">算作整轨文件的扩展名，默认 FLAC / WAV</param>
    public static ResolvedCue? Resolve(
        string fileName,
        long durationMs,
        string? embeddedCue,
        IReadOnlyList<string> siblingFileNames,
        Func<string, string?> readCue,
        IReadOnlySet<string>? imageExtensions = null)
    {
        if (!IsImage(fileName, imageExtensions)) return null;

        // 1. 内嵌 cuesheet
        if (!string.IsNullOrWhiteSpace(embeddedCue))
        {
            if (Pick(CueParser.Parse(embeddedCue), fileName, single: true) is { } embedded) return embedded;
        }
        if (durationMs < MinDurationMs) return null;

        // 2. 外部 .cue
        var cues = siblingFileNames.Where(n => n.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)).ToList();
        if (cues.Count == 0) return null;
        var stem = Stem(fileName);
        var images = siblingFileNames.Count(n => IsImage(n, imageExtensions));

        // a. "专辑.flac.cue"  b. "专辑.cue"
        var byName = cues.FirstOrDefault(c => c.Equals(fileName + ".cue", StringComparison.OrdinalIgnoreCase))
            ?? cues.FirstOrDefault(c => Stem(c).Equals(stem, StringComparison.OrdinalIgnoreCase));
        if (byName is not null && Read(byName) is { } named && Pick(named, fileName, single: true) is { } byNameResult) return byNameResult;

        // c. 任意 CUE 中 FILE 的文件名去掉扩展名后与音频同名（CUE 里写 .wav、实际是 .flac）
        // d. 目录中只有一个 CUE 和一个整轨文件时直接配对
        foreach (var cue in cues)
        {
            if (cue == byName) continue;
            if (Read(cue) is not { } sheet) continue;
            if (Pick(sheet, fileName, single: cues.Count == 1 && images == 1) is { } result) return result;
        }
        return null;

        CueSheet? Read(string cue) => readCue(cue) is { } text ? CueParser.Parse(text) : null;
    }

    /// <summary>在 CUE 中找到与音频文件对应的 FILE 段；<paramref name="single"/> 为 true 时，只有一个 FILE 段也算匹配。</summary>
    public static ResolvedCue? Pick(CueSheet sheet, string fileName, bool single)
    {
        if (sheet.IsPerTrack) return null;
        var stem = Stem(fileName);
        var file = sheet.Files.FirstOrDefault(f => BaseName(f.Name).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            ?? sheet.Files.FirstOrDefault(f => Stem(BaseName(f.Name)).Equals(stem, StringComparison.OrdinalIgnoreCase))
            ?? (single && sheet.Files.Count == 1 ? sheet.Files[0] : null);
        return file is not null && file.Tracks.Count >= 2 ? new ResolvedCue(sheet, file) : null;
    }

    /// <summary>CUE 里的 FILE 可能带路径（/ 或 \）。</summary>
    private static string BaseName(string name)
    {
        var slash = name.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static string Stem(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }

    private static string Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }
}
