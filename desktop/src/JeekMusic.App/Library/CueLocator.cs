using JeekMusic.Core.Library;
using JeekMusic.Core.Text;

namespace JeekMusic.Library;

/// <summary>
/// 为整轨文件找到对应的 CUE（需求 CUE-1、CUE-5、CUE-8；规则同移动端 data/scanner/CueResolver.kt）。
/// 桌面端支持的整轨格式比移动端多：FLAC、WAV、APE、WavPack、TAK（libmpv 都能播）。
/// </summary>
public static class CueLocator
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".ape", ".wv", ".tak" };
    const long MinDurationMs = 5 * 60_000L;

    /// <param name="directoryFiles">同目录的文件名（扫描时按目录缓存）</param>
    public static (CueSheet sheet, CueFile file)? Resolve(AudioFile audio, TrackTags? tags, IReadOnlyList<string> directoryFiles)
    {
        if (!ImageExtensions.Contains(Path.GetExtension(audio.FileName))) return null;

        // 1. 内嵌 cuesheet（FLAC 的 CUESHEET 字段）
        if (tags?.EmbeddedCue is { } embedded && CueParser.Parse(embedded) is var sheetEmbedded && Pick(sheetEmbedded, audio.FileName, single: true) is { } fromEmbedded)
            return (sheetEmbedded, fromEmbedded);
        if ((tags?.DurationMs ?? 0) < MinDurationMs) return null;

        // 2. 外部 .cue
        var cues = directoryFiles.Where(n => n.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)).ToList();
        if (cues.Count == 0) return null;
        var stem = Path.GetFileNameWithoutExtension(audio.FileName);
        var images = directoryFiles.Count(n => ImageExtensions.Contains(Path.GetExtension(n)));

        var byName = cues.FirstOrDefault(c => c.Equals(audio.FileName + ".cue", StringComparison.OrdinalIgnoreCase))
                     ?? cues.FirstOrDefault(c => Path.GetFileNameWithoutExtension(c).Equals(stem, StringComparison.OrdinalIgnoreCase));
        if (byName is not null && Read(audio, byName) is { } named && Pick(named, audio.FileName, single: true) is { } f1)
            return (named, f1);

        // c. 任意 CUE 中 FILE 的文件名去掉扩展名后与音频同名（CUE 里写 .wav、实际是 .flac）
        // d. 目录中只有一个 CUE 和一个整轨文件时直接配对
        foreach (var cue in cues)
        {
            if (cue == byName) continue;
            if (Read(audio, cue) is { } sheet && Pick(sheet, audio.FileName, single: cues.Count == 1 && images == 1) is { } f2)
                return (sheet, f2);
        }
        return null;
    }

    static CueSheet? Read(AudioFile audio, string cueName)
    {
        try
        {
            return CueParser.Parse(CharsetDetector.Decode(File.ReadAllBytes(audio.Folder.Root + audio.Dir + cueName)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>在 CUE 中找到与音频文件对应的 FILE 段；<paramref name="single"/> 为 true 时，只有一个 FILE 段也算匹配。</summary>
    static CueFile? Pick(CueSheet sheet, string fileName, bool single)
    {
        if (sheet.IsPerTrack) return null; // 分轨 CUE（需求 CUE-8）按普通单曲处理
        var stem = Path.GetFileNameWithoutExtension(fileName);
        static string BaseName(string n) => n[(Math.Max(n.LastIndexOf('/'), n.LastIndexOf('\\')) + 1)..];
        var file = sheet.Files.FirstOrDefault(f => BaseName(f.Name).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                   ?? sheet.Files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(BaseName(f.Name)).Equals(stem, StringComparison.OrdinalIgnoreCase))
                   ?? (single && sheet.Files.Count == 1 ? sheet.Files[0] : null);
        return file is { Tracks.Count: >= 2 } ? file : null;
    }
}
