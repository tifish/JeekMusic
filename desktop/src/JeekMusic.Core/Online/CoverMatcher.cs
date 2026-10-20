using System.Text;
using System.Text.RegularExpressions;
using JeekMusic.Core.Text;

namespace JeekMusic.Core.Online;

/// <summary>在线来源（需求 OL-1，移动端 SettingsRepository.kt 的 OnlineSource）。</summary>
public enum OnlineSource
{
    NETEASE,
    QQ,
    ITUNES,
    MUSICBRAINZ,
    LRCLIB,
}

public static class OnlineSources
{
    public static readonly IReadOnlyList<OnlineSource> ForCovers = [OnlineSource.NETEASE, OnlineSource.QQ, OnlineSource.ITUNES, OnlineSource.MUSICBRAINZ];

    public static readonly IReadOnlyList<OnlineSource> ForLyrics = [OnlineSource.NETEASE, OnlineSource.QQ, OnlineSource.LRCLIB];
}

/// <summary>在线音乐服务里搜到的一首歌，用来取封面和歌词（移动端 OnlineSong.kt）。</summary>
/// <param name="Id">各服务自己的歌曲 id（网易云是数字，QQ 音乐是 songmid）。</param>
/// <param name="ThumbUrl">封面缩略图；没有封面时为 null。</param>
/// <param name="ImageUrl">封面大图；没有封面时为 null。</param>
public sealed record OnlineSong(
    OnlineSource Source,
    string Id,
    string Title,
    string Artist,
    string? Album,
    long? DurationMs,
    string? ThumbUrl,
    string? ImageUrl);

/// <summary>
/// 判断在线搜索结果是不是这首歌（需求 C-3，移动端 CoverMatcher.kt）。批量获取时没人把关，只接受歌名和艺术家都对得上的结果。
/// 比较前统一大小写、全半角、繁简（<c>fold</c>），去掉标点空白和括号里的附注（Live、feat.、Remastered 等）。
/// </summary>
public sealed partial class CoverMatcher(Func<string, string>? fold = null)
{
    private readonly Func<string, string> _fold = fold ?? ChineseText.ToSimplified;

    /// <summary>默认的匹配器：繁转简用 <see cref="ChineseText.ToSimplified"/>。</summary>
    public static CoverMatcher Default { get; } = new();

    public string Normalize(string text)
    {
        var folded = _fold(text.Normalize(NormalizationForm.FormKC)).ToLowerInvariant();
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>去掉括号里的附注和 " - Remastered" 之类的后缀后再比较。</summary>
    public string NormalizeTitle(string text)
    {
        var stripped = Brackets().Replace(text, " ");
        var dash = stripped.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0) stripped = stripped[..dash];
        if (string.IsNullOrWhiteSpace(stripped)) stripped = text;
        var normalized = Normalize(stripped);
        return normalized.Length > 0 ? normalized : Normalize(text);
    }

    public bool TitleMatches(string expected, string actual)
    {
        var a = NormalizeTitle(expected);
        var b = NormalizeTitle(actual);
        if (a.Length == 0 || b.Length == 0) return false;
        // 不做前缀匹配："Baby" 和 "Baby Shark" 是两首歌
        return a == b;
    }

    /// <summary>歌曲的任意一位艺术家出现在结果的艺术家里（或反过来）就算对上。</summary>
    public bool ArtistMatches(string expected, string actual)
    {
        var b = Normalize(actual);
        if (b.Length == 0) return false;
        return SplitArtists(expected).Select(Normalize).Where(a => a.Length > 0).Any(a =>
            a == b || (Math.Min(a.Length, b.Length) >= 2 && (b.Contains(a, StringComparison.Ordinal) || a.Contains(b, StringComparison.Ordinal))));
    }

    public static IReadOnlyList<string> SplitArtists(string text) =>
        Separators().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>搜索用的艺术家：只取第一位，多位艺术家放在一起时搜索引擎往往一个都找不到。</summary>
    public static string PrimaryArtist(string text) => SplitArtists(text) is { Count: > 0 } artists ? artists[0] : text.Trim();

    /// <summary>含有汉字（移动端按 UTF-16 码元判断 Character.UnicodeScript.HAN）。</summary>
    public static bool HasCjk(string text) => UnicodeScripts.ContainsHan(text);

    [GeneratedRegex("[(\\[（【「][^)\\]）】」]*[)\\]）】」]")]
    private static partial Regex Brackets();

    [GeneratedRegex("\\s*(?:[,，、/／&;；]|\\bfeat\\.?|\\bft\\.|\\bvs\\.?)\\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Separators();
}
