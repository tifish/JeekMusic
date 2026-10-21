using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JeekMusic.Core.Online;

namespace JeekMusic.Online;

/// <summary>
/// 网易云音乐的网页接口（非官方，可能随时失效；同移动端 data/online/NetEaseMusic.kt）：国内访问稳定，中文歌收录全，歌词常带翻译。
/// 只用搜索和歌词两个不需要登录的接口。
/// </summary>
public sealed class NetEaseMusic
{
    const string Base = "https://music.163.com";
    readonly Http.RateLimit _limit = new(TimeSpan.FromMilliseconds(300));
    static readonly Dictionary<string, string> Headers = new() { ["User-Agent"] = Http.BrowserAgent, ["Referer"] = "https://music.163.com/" };

    public async Task<IReadOnlyList<OnlineSong>> SearchAsync(string query, int count = 10, CancellationToken ct = default)
    {
        var body = await _limit.RunAsync(() => Http.TextAsync($"{Base}/api/cloudsearch/pc", Headers, $"s={Http.Encode(query)}&type=1&limit={count}&offset=0", ct: ct), ct);
        return body is null ? [] : ParseSearch(body);
    }

    internal static IReadOnlyList<OnlineSong> ParseSearch(string body)
    {
        using var doc = JsonDocument.Parse(body);
        JsonElement? root = doc.RootElement;
        return root.Prop("result").Prop("songs").Items().Select(s =>
        {
            JsonElement? song = s;
            var album = song.Prop("al");
            // 图片地址是 http 的，换成 https；?param=宽y高 指定尺寸
            var pic = album.Prop("picUrl").StrOrNull() is { } p && p.StartsWith("http", StringComparison.Ordinal) ? "https://" + p[(p.IndexOf("://", StringComparison.Ordinal) + 3)..] : null;
            var dt = song.Prop("dt").Long();
            return new OnlineSong(OnlineSource.NETEASE, song.Prop("id").Long().ToString(CultureInfo.InvariantCulture), song.Prop("name").Str(),
                string.Join(", ", song.Prop("ar").Items().Select(a => ((JsonElement?)a).Prop("name").Str())), album.Prop("name").StrOrNull(),
                dt > 0 ? dt : null, pic is null ? null : pic + "?param=300y300", pic is null ? null : pic + "?param=1000y1000");
        }).ToList();
    }

    /// <summary>LRC 歌词；有翻译时把翻译行接在后面（同一时间戳的两行显示成原文加翻译，需求 LY-5）。没有歌词时返回 null。</summary>
    public async Task<string?> LyricsAsync(string id, CancellationToken ct = default)
    {
        var body = await _limit.RunAsync(() => Http.TextAsync($"{Base}/api/song/lyric?id={Http.Encode(id)}&lv=1&tv=-1", Headers, ct: ct), ct);
        return body is null ? null : ParseLyrics(body);
    }

    internal static string? ParseLyrics(string body)
    {
        using var doc = JsonDocument.Parse(body);
        JsonElement? root = doc.RootElement;
        var lrc = root.Prop("lrc").Prop("lyric").StrOrNull();
        if (string.IsNullOrWhiteSpace(lrc)) return null;
        var translation = root.Prop("tlyric").Prop("lyric").StrOrNull();
        return string.IsNullOrWhiteSpace(translation) ? lrc : lrc.TrimEnd() + "\n" + translation;
    }
}

/// <summary>
/// QQ 音乐的网页接口（非官方，可能随时失效；同移动端 data/online/QqMusic.kt）：国内访问稳定，收录了网易云没有的周杰伦等。
/// 封面直接按专辑 id 拼出图片地址。
/// </summary>
public sealed partial class QqMusic
{
    const string Cover = "https://y.gtimg.cn/music/photo_new";
    static readonly string[] SearchEndpoints =
    [
        "https://c.y.qq.com/soso/fcgi-bin/search_for_qq_cp",
        "https://c6.y.qq.com/soso/fcgi-bin/client_search_cp",
        "https://c.y.qq.com/soso/fcgi-bin/client_search_cp",
    ];
    static readonly Dictionary<string, string> Headers = new() { ["User-Agent"] = Http.BrowserAgent, ["Referer"] = "https://y.qq.com/" };
    readonly Http.RateLimit _limit = new(TimeSpan.FromMilliseconds(300));
    /// <summary>搜索接口时不时会对某个地址返回 500：几个返回格式相同的地址依次试，记住上次能用的。</summary>
    volatile int _working;

    public async Task<IReadOnlyList<OnlineSong>> SearchAsync(string query, int count = 10, CancellationToken ct = default)
    {
        var parameters = $"format=json&p=1&n={count}&w={Http.Encode(query)}";
        Exception? error = null;
        for (var attempt = 0; attempt < SearchEndpoints.Length; attempt++)
        {
            var index = (_working + attempt) % SearchEndpoints.Length;
            try
            {
                var body = await _limit.RunAsync(() => Http.TextAsync($"{SearchEndpoints[index]}?{parameters}", Headers, ct: ct), ct);
                _working = index;
                return body is null ? [] : ParseSearch(body);
            }
            catch (Exception e) when (e is IOException or HttpRequestException or JsonException)
            {
                error = e;
            }
        }
        throw error ?? new IOException("QQ 音乐搜索失败");
    }

    internal static IReadOnlyList<OnlineSong> ParseSearch(string body)
    {
        using var doc = JsonDocument.Parse(body);
        JsonElement? root = doc.RootElement;
        return root.Prop("data").Prop("song").Prop("list").Items().Select(s =>
        {
            JsonElement? song = s;
            var albumMid = song.Prop("albummid").StrOrNull();
            var interval = song.Prop("interval").Long();
            return new OnlineSong(OnlineSource.QQ, song.Prop("songmid").Str(), Unescape(song.Prop("songname").Str()),
                string.Join(", ", song.Prop("singer").Items().Select(a => Unescape(((JsonElement?)a).Prop("name").Str()))),
                song.Prop("albumname").StrOrNull() is { } album ? Unescape(album) : null, interval > 0 ? interval * 1000 : null,
                albumMid is null ? null : $"{Cover}/T002R300x300M000{albumMid}.jpg", albumMid is null ? null : $"{Cover}/T002R800x800M000{albumMid}.jpg");
        }).Where(s => s.Id.Length > 0).ToList();
    }

    public async Task<string?> LyricsAsync(string songMid, CancellationToken ct = default)
    {
        var body = await _limit.RunAsync(() => Http.TextAsync(
            $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?format=json&nobase64=1&songmid={Http.Encode(songMid)}", Headers, ct: ct), ct);
        return body is null ? null : ParseLyrics(body);
    }

    internal static string? ParseLyrics(string body)
    {
        using var doc = JsonDocument.Parse(body);
        JsonElement? root = doc.RootElement;
        var lrc = Unescape(root.Prop("lyric").Str());
        if (string.IsNullOrWhiteSpace(lrc)) return null;
        var translation = Unescape(root.Prop("trans").Str());
        return string.IsNullOrWhiteSpace(translation) ? lrc : lrc.TrimEnd() + "\n" + translation;
    }

    [GeneratedRegex(@"&(#\d+|#x[0-9a-fA-F]+|amp|lt|gt|quot|apos);")]
    private static partial Regex Entity();

    /// <summary>接口返回的文本里有 HTML 实体（&amp;#39; &amp;amp; 等）。</summary>
    internal static string Unescape(string text) => Entity().Replace(text, m =>
    {
        var e = m.Groups[1].Value;
        switch (e)
        {
            case "amp": return "&";
            case "lt": return "<";
            case "gt": return ">";
            case "quot": return "\"";
            case "apos": return "'";
        }
        try
        {
            var code = e.StartsWith("#x", StringComparison.Ordinal) ? Convert.ToInt32(e[2..], 16) : int.Parse(e[1..], CultureInfo.InvariantCulture);
            return char.ConvertFromUtf32(code);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException) { return m.Value; }
    });
}

/// <summary>LRCLIB 在线歌词（同移动端 data/lyrics/LrcLibClient.kt）：开放的歌词库，国内访问不稳定。</summary>
public sealed class LrcLibClient
{
    const string Base = "https://lrclib.net";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>精确查询；没有找到时返回 null。</summary>
    public async Task<OnlineLyrics?> GetAsync(string title, string artist, string? album, long durationSec, CancellationToken ct = default)
    {
        var query = $"track_name={Http.Encode(title)}&artist_name={Http.Encode(artist)}"
                    + (string.IsNullOrEmpty(album) ? "" : $"&album_name={Http.Encode(album)}")
                    + (durationSec > 0 ? $"&duration={durationSec}" : "");
        var body = await Http.TextAsync($"{Base}/api/get?{query}", timeout: Timeout, ct: ct);
        if (body is null) return null;
        using var doc = JsonDocument.Parse(body);
        return Parse(doc.RootElement) is { Text: not null } l ? l : null;
    }

    public async Task<IReadOnlyList<OnlineLyrics>> SearchAsync(string title, string? artist, CancellationToken ct = default)
    {
        var query = $"track_name={Http.Encode(title)}" + (string.IsNullOrEmpty(artist) ? "" : $"&artist_name={Http.Encode(artist)}");
        var body = await Http.TextAsync($"{Base}/api/search?{query}", timeout: Timeout, ct: ct);
        return body is null ? [] : ParseSearch(body);
    }

    internal static IReadOnlyList<OnlineLyrics> ParseSearch(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(Parse).Where(l => l.Text is not null).ToList()
            : [];
    }

    internal static OnlineLyrics Parse(JsonElement e)
    {
        JsonElement? j = e;
        return new OnlineLyrics(OnlineSource.LRCLIB, j.Prop("id").Long().ToString(CultureInfo.InvariantCulture), j.Prop("trackName").Str(), j.Prop("artistName").Str(),
            j.Prop("albumName").StrOrNull(), j.Prop("duration").Double(),
            j.Prop("syncedLyrics").StrOrNull() is { } s && !string.IsNullOrWhiteSpace(s) ? s : null,
            j.Prop("plainLyrics").StrOrNull() is { } p && !string.IsNullOrWhiteSpace(p) ? p : null);
    }
}
