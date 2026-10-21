using System.Text.Json;
using JeekMusic.Core.Online;
using AppSource = JeekMusic.Settings.OnlineSource;

namespace JeekMusic.Online;

/// <summary>一个在线封面候选（同移动端 CoverCandidate）。<see cref="ImageUrls"/> 按清晰度从高到低，前一个下载失败时用后一个。</summary>
/// <param name="Score">和要找的歌对得上的程度：0 对不上，1 歌名和艺术家对得上，2 连括号里的附注也一样。手动搜索时用来跨来源排序。</param>
public sealed record CoverCandidate(OnlineSource Source, string Title, string Artist, string? Album, string ThumbUrl, IReadOnlyList<string> ImageUrls, int Score = 0);

/// <summary>在线歌词（同移动端 OnlineLyrics）。</summary>
/// <param name="Matched">歌名和艺术家对得上；手动搜索时用来排序。</param>
public sealed record OnlineLyrics(OnlineSource Source, string Id, string TrackName, string ArtistName, string? AlbumName, double? DurationSec,
    string? Synced, string? Plain, bool Matched = false)
{
    public string? Text => Synced ?? Plain;
    public bool IsSynced => Synced is not null;
}

/// <summary>批量下载的内容。</summary>
public enum DownloadKind { Covers, Lyrics }

public static class Sources
{
    /// <summary>设置里的来源（App 的枚举）和 Core 的 <see cref="OnlineSource"/> 一一对应。</summary>
    public static OnlineSource ToCore(AppSource s) => s switch
    {
        AppSource.Netease => OnlineSource.NETEASE,
        AppSource.QQ => OnlineSource.QQ,
        AppSource.ITunes => OnlineSource.ITUNES,
        AppSource.MusicBrainz => OnlineSource.MUSICBRAINZ,
        _ => OnlineSource.LRCLIB,
    };

    public static AppSource ToApp(OnlineSource s) => s switch
    {
        OnlineSource.NETEASE => AppSource.Netease,
        OnlineSource.QQ => AppSource.QQ,
        OnlineSource.ITUNES => AppSource.ITunes,
        OnlineSource.MUSICBRAINZ => AppSource.MusicBrainz,
        _ => AppSource.LrcLib,
    };

    public static string Name(OnlineSource s) => s switch
    {
        OnlineSource.NETEASE => L.T("网易云音乐"),
        OnlineSource.QQ => L.T("QQ 音乐"),
        OnlineSource.ITUNES => "Apple iTunes",
        OnlineSource.MUSICBRAINZ => "MusicBrainz",
        _ => "LRCLIB",
    };

    /// <summary>设置里每个来源旁的说明（需求 OL-1）。</summary>
    public static string Note(OnlineSource s) => s switch
    {
        OnlineSource.NETEASE => L.T("国内稳定，中文歌全，歌词常带翻译（非官方接口，可能失效）"),
        OnlineSource.QQ => L.T("国内稳定，有网易云没有的歌（如周杰伦）（非官方接口，可能失效）"),
        OnlineSource.ITUNES => L.T("国内可以访问，英文歌全；中文歌只有繁体能搜到"),
        OnlineSource.MUSICBRAINZ => L.T("资料全但访问慢；封面图片在 archive.org，国内通常无法访问"),
        _ => L.T("开放的歌词库，国内访问不稳定"),
    };
}

/// <summary>JsonDocument 的小工具（兼容 Native AOT，不用反射序列化）。</summary>
static class Json
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static JsonElement? Prop(this JsonElement? e, string name) => e?.Prop(name);

    public static string Str(this JsonElement? e) => e is { } v ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    } : "";

    public static string? StrOrNull(this JsonElement? e) => e.Str() is { Length: > 0 } s && s != "null" ? s : null;

    public static long Long(this JsonElement? e) => e is { } v && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l
        : e is { ValueKind: JsonValueKind.Number } d && d.TryGetDouble(out var dd) ? (long)dd
        : long.TryParse(e.Str(), out var p) ? p : 0;

    public static double? Double(this JsonElement? e) => e is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? d : null;

    public static IEnumerable<JsonElement> Items(this JsonElement? e) =>
        e is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];
}
