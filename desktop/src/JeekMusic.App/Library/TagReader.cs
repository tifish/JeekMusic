using System.Text;
using System.Text.RegularExpressions;

namespace JeekMusic.Library;

/// <summary>从音频文件读出的原始标签（还没修复乱码、没拆分艺术家）。</summary>
public sealed record TrackTags(
    string? Title,
    IReadOnlyList<string> Artists,
    string? Album,
    string? AlbumArtist,
    int? TrackNo,
    int? DiscNo,
    int? Year,
    string? Genre,
    long DurationMs,
    int? Bitrate,
    int? SampleRate,
    int? BitsPerSample,
    int? Channels,
    string Codec,
    bool HasLyrics,
    bool HasCover,
    string? EmbeddedCue);

/// <summary>
/// 标签读取（设计 §5.3）：ATL.NET。D1 对比验证（tools/TagCompare）：作者曲库 1671 个文件，ATL 能读 TagLib# 读不了的 WMA，
/// 没有 VBR 头的 MP3 时长和采样率 ATL 是对的（TagLib# 错了 41 个），乱码标签两边读出的字节一致；
/// 每个文件约 1ms（文件已在缓存里）。ATL 的设置是全局的，在 <see cref="Configure"/> 里设一次。
/// </summary>
public static partial class TagReader
{
    static int _configured;

    /// <summary>
    /// 多值字段用 \0 连接（和移动端 artist_raw 的格式一致）。ATL 按 ID3 编码字节读取 Latin-1，
    /// CharsetFixer 因而仍可还原误标的 GBK；DefaultTextEncoding 也控制保存，必须使用 Unicode 编码。
    /// </summary>
    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1) return;
        ATL.Settings.DisplayValueSeparator = LibraryDb.MultiValueSeparator;
        ATL.Settings.DefaultTextEncoding = Encoding.UTF8;
        ATL.Settings.UseFileNameWhenNoTitle = false;
        ATL.Settings.NullAbsentValues = true;
        ATL.Settings.OutputStacktracesToConsole = false;
    }

    public static TrackTags Read(string path)
    {
        Configure();
        var t = new ATL.Track(path);
        var artists = Split(t.Artist);
        return new TrackTags(
            Title: Clean(t.Title),
            Artists: artists,
            Album: Clean(t.Album),
            AlbumArtist: Clean(t.AlbumArtist)?.Replace(LibraryDb.MultiValueSeparator.ToString(), " / "),
            TrackNo: t.TrackNumber is > 0 ? t.TrackNumber : null,
            DiscNo: t.DiscNumber is > 0 ? t.DiscNumber : null,
            Year: YearOf(t),
            Genre: Clean(t.Genre)?.Replace(LibraryDb.MultiValueSeparator.ToString(), " / "),
            DurationMs: (long)Math.Round(t.DurationMs),
            Bitrate: t.Bitrate > 0 ? t.Bitrate : null,
            SampleRate: t.SampleRate > 0 ? (int)t.SampleRate : null,
            BitsPerSample: t.BitDepth > 0 ? t.BitDepth : null,
            Channels: t.ChannelsArrangement?.NbChannels is > 0 ? t.ChannelsArrangement.NbChannels : null,
            Codec: CodecOf(t, path),
            HasLyrics: t.Lyrics.Any(l => !string.IsNullOrWhiteSpace(l.UnsynchronizedLyrics) || l.SynchronizedLyrics.Count > 0),
            HasCover: t.EmbeddedPictures.Count > 0,
            EmbeddedCue: t.AdditionalFields.TryGetValue("CUESHEET", out var cue) && !string.IsNullOrWhiteSpace(cue) ? cue : null);
    }

    static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Trim(LibraryDb.MultiValueSeparator).Trim();
        return s.Length == 0 ? null : s;
    }

    static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(LibraryDb.MultiValueSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>年份：ATL 解析不了不标准的写法（如 TYER = "2013-07-29"），从原始字段里取前 4 位数字。</summary>
    static int? YearOf(ATL.Track t)
    {
        if (t.Year is > 0) return t.Year;
        if (t.Date is { } date && date.Year > 1) return date.Year;
        foreach (var key in new[] { "TYER", "TDRC", "DATE", "YEAR", "TORY", "©day" })
            if (t.AdditionalFields.TryGetValue(key, out var raw) && FourDigits().Match(raw) is { Success: true } m)
                return int.Parse(m.Value);
        return null;
    }

    [GeneratedRegex(@"(1[89]|20)\d\d")]
    private static partial Regex FourDigits();

    static string CodecOf(ATL.Track t, string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        // m4a 里可能是 AAC 或 ALAC：用 ATL 识别出的格式名区分
        // ATL 的 CodecFamily：0 有损，1 无损
        if (ext is "m4a" or "mp4") return t.CodecFamily == 1 ? "alac" : "aac";
        return ext;
    }

    /// <summary>内嵌歌词（ID3 USLT/SYLT、Vorbis LYRICS、MP4 ©lyr），需求 LY-2。同步歌词转成 LRC 文本。</summary>
    public static string? ReadLyrics(string path)
    {
        Configure();
        try
        {
            var t = new ATL.Track(path);
            foreach (var l in t.Lyrics)
            {
                if (l.SynchronizedLyrics.Count > 0)
                    return string.Join('\n', l.SynchronizedLyrics.Select(p => $"[{p.TimestampStart / 60000:00}:{p.TimestampStart / 1000 % 60:00}.{p.TimestampStart % 1000 / 10:00}]{p.Text}"));
                if (!string.IsNullOrWhiteSpace(l.UnsynchronizedLyrics)) return l.UnsynchronizedLyrics;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>内嵌封面的图片数据（第一张，优先封面类型）。</summary>
    public static byte[]? ReadCover(string path)
    {
        Configure();
        try
        {
            var t = new ATL.Track(path);
            var pic = t.EmbeddedPictures.FirstOrDefault(p => p.PicType == ATL.PictureInfo.PIC_TYPE.Front) ?? t.EmbeddedPictures.FirstOrDefault();
            return pic?.PictureData;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
