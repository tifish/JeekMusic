using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JeekMusic.Core.Lyrics;
using JeekMusic.Core.Online;
using JeekMusic.Core.Text;
using JeekMusic.Settings;
using OnlineSource = JeekMusic.Core.Online.OnlineSource;

namespace JeekMusic.Online;

/// <summary>
/// 在线封面（需求 C-3，同移动端 data/cover/CoverClient.kt）。来源由用户在设置里选，按固定顺序尝试：
/// 网易云音乐、QQ 音乐（国内稳定）→ iTunes（含汉字时转成繁体、查台湾区，限速约 20 次 / 分钟）→
/// MusicBrainz（每秒不超过 1 次，封面取 Cover Art Archive 发行组的代表封面）。
/// </summary>
public sealed class CoverClient(SettingsStore settings, NetEaseMusic netEase, QqMusic qq)
{
    readonly CoverMatcher _matcher = CoverMatcher.Default;
    readonly Http.RateLimit _iTunes = new(TimeSpan.FromSeconds(3));
    readonly Http.RateLimit _musicBrainz = new(TimeSpan.FromMilliseconds(1100));
    const int MinImageBytes = 1_000, MaxImageBytes = 8_000_000;

    /// <summary>设置里启用的封面来源，按尝试的顺序。</summary>
    public IReadOnlyList<OnlineSource> EnabledSources()
    {
        var enabled = settings.Current.CoverSources.Select(Sources.ToCore).ToHashSet();
        return OnlineSources.ForCovers.Where(enabled.Contains).ToList();
    }

    /// <summary>手动搜索一个来源：对得上歌名和艺术家的排在前面。网络出错时抛出异常。</summary>
    public async Task<IReadOnlyList<CoverCandidate>> SearchAsync(OnlineSource source, string title, string? artist, CancellationToken ct = default) =>
        (await CandidatesAsync(source, title, artist, strict: false, ct)).Take(8)
            .Select(c => c with { Score = Score(c, title, artist) }).OrderByDescending(c => c.Score).ToList();

    public Task<byte[]?> ThumbnailAsync(CoverCandidate c, CancellationToken ct = default) => DownloadAsync(c.ThumbUrl, ct);

    /// <summary>下载候选的大图；都失败时返回 null。</summary>
    public async Task<byte[]?> DownloadImageAsync(CoverCandidate c, CancellationToken ct = default)
    {
        foreach (var url in c.ImageUrls)
            if (await DownloadAsync(url, ct) is { } bytes) return bytes;
        return null;
    }

    /// <summary>
    /// 批量获取：按启用的来源依次找第一个歌名和艺术家都对得上的结果，返回封面图片；都找不到返回 null。
    /// 某个来源出错（比如国内打不开）时跳过它试下一个；全部出错时抛出最后一个异常（不算"没找到"，下次还会再试）。
    /// </summary>
    public async Task<byte[]?> FetchBestAsync(string title, string artist, string? album, CancellationToken ct = default)
    {
        Exception? error = null;
        var anySucceeded = false;
        foreach (var source in EnabledSources())
        {
            IReadOnlyList<CoverCandidate> found;
            try { found = await CandidatesAsync(source, title, artist, strict: true, ct); }
            catch (Exception e) when (e is IOException or HttpRequestException or JsonException) { error = e; continue; }
            anySucceeded = true;
            var ordered = found.Where(c => Matches(c, title, artist))
                .OrderByDescending(c => album is not null && c.Album is not null && _matcher.TitleMatches(album, c.Album))
                .ThenByDescending(c => Score(c, title, artist));
            foreach (var c in ordered.Take(4))
                if (await DownloadImageAsync(c, ct) is { } image) return image;
        }
        if (!anySucceeded && error is not null) throw error;
        return null;
    }

    async Task<IReadOnlyList<CoverCandidate>> CandidatesAsync(OnlineSource source, string title, string? artist, bool strict, CancellationToken ct)
    {
        var query = string.Join(' ', new[] { string.IsNullOrWhiteSpace(artist) ? null : CoverMatcher.PrimaryArtist(artist), title }.Where(s => s is not null));
        IEnumerable<CoverCandidate> list = source switch
        {
            OnlineSource.NETEASE => (await netEase.SearchAsync(query, ct: ct)).Select(FromSong).OfType<CoverCandidate>(),
            OnlineSource.QQ => (await qq.SearchAsync(query, ct: ct)).Select(FromSong).OfType<CoverCandidate>(),
            OnlineSource.ITUNES => await ITunesAsync(query, 12, ct),
            OnlineSource.MUSICBRAINZ => await MusicBrainzAsync(title, artist, strict ? 90 : 80, ct),
            _ => [],
        };
        return list.DistinctBy(c => c.ThumbUrl).ToList();
    }

    static CoverCandidate? FromSong(OnlineSong song) =>
        song.ThumbUrl is { } thumb && song.ImageUrl is { } image ? new CoverCandidate(song.Source, song.Title, song.Artist, song.Album, thumb, [image, thumb]) : null;

    bool Matches(CoverCandidate c, string title, string? artist) =>
        _matcher.TitleMatches(title, c.Title) && (string.IsNullOrWhiteSpace(artist) || _matcher.ArtistMatches(artist, c.Artist));

    /// <summary>对不上为 0；对得上为 1；连括号里的附注也一样（不是 Remix、Live 等其他版本）为 2。</summary>
    int Score(CoverCandidate c, string title, string? artist) =>
        !Matches(c, title, artist) ? 0 : _matcher.Normalize(title) == _matcher.Normalize(c.Title) ? 2 : 1;

    async Task<IReadOnlyList<CoverCandidate>> ITunesAsync(string term, int limit, CancellationToken ct)
    {
        var cjk = CoverMatcher.HasCjk(term);
        var query = "term=" + Http.Encode(cjk ? ChineseText.ToTraditional(term) : term) + $"&entity=song&limit={limit}&country=" + (cjk ? "tw" : "us");
        var body = await _iTunes.RunAsync(() => Http.TextAsync("https://itunes.apple.com/search?" + query, ct: ct), ct);
        return body is null ? [] : ParseITunes(body);
    }

    internal static IReadOnlyList<CoverCandidate> ParseITunes(string body)
    {
        using var doc = JsonDocument.Parse(body);
        JsonElement? root = doc.RootElement;
        return root.Prop("results").Items().Select(r =>
        {
            JsonElement? j = r;
            var art = j.Prop("artworkUrl100").Str();
            if (!art.Contains("100x100", StringComparison.Ordinal)) return null;
            return new CoverCandidate(OnlineSource.ITUNES, j.Prop("trackName").Str(), j.Prop("artistName").Str(), j.Prop("collectionName").StrOrNull(),
                art.Replace("100x100", "300x300"), [art.Replace("100x100", "1000x1000"), art.Replace("100x100", "600x600")]);
        }).OfType<CoverCandidate>().ToList();
    }

    async Task<IReadOnlyList<CoverCandidate>> MusicBrainzAsync(string title, string? artist, int minScore, CancellationToken ct)
    {
        static string Escape(string v) => v.Replace("\\", " ").Replace("\"", " ").Trim();
        var query = new StringBuilder("recording:\"").Append(Escape(title)).Append('"');
        if (!string.IsNullOrWhiteSpace(artist)) query.Append(" AND artist:\"").Append(Escape(CoverMatcher.PrimaryArtist(artist))).Append('"');
        var body = await _musicBrainz.RunAsync(() => Http.TextAsync("https://musicbrainz.org/ws/2/recording?fmt=json&limit=10&query=" + Http.Encode(query.ToString()), ct: ct), ct);
        return body is null ? [] : ParseMusicBrainz(body, minScore);
    }

    internal static IReadOnlyList<CoverCandidate> ParseMusicBrainz(string body, int minScore)
    {
        using var doc = JsonDocument.Parse(body);
        JsonElement? root = doc.RootElement;
        var seen = new HashSet<string>();
        var list = new List<CoverCandidate>();
        foreach (var r in root.Prop("recordings").Items())
        {
            JsonElement? rec = r;
            if (rec.Prop("score").Long() < minScore) continue;
            var artistName = string.Join(", ", rec.Prop("artist-credit").Items().Select(a => ((JsonElement?)a).Prop("name").Str()));
            foreach (var rel in rec.Prop("releases").Items())
            {
                JsonElement? release = rel;
                var group = release.Prop("release-group").Prop("id").StrOrNull();
                if (group is null || !seen.Add(group)) continue;
                var b = $"https://coverartarchive.org/release-group/{group}";
                list.Add(new CoverCandidate(OnlineSource.MUSICBRAINZ, rec.Prop("title").Str(), artistName, release.Prop("title").StrOrNull(),
                    b + "/front-250", [b + "/front-1200", b + "/front-500"]));
            }
        }
        return list;
    }

    /// <summary>下载图片；失败或大小不像图片时返回 null。</summary>
    static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct) =>
        await Http.BytesAsync(url, new Dictionary<string, string> { ["User-Agent"] = Http.BrowserAgent }, ct: ct) is { Length: >= MinImageBytes and <= MaxImageBytes } b ? b : null;
}

/// <summary>
/// 在线歌词（需求 LY-8，同移动端 OnlineLyricsClient.kt）：LRCLIB 直接按歌名、艺术家、时长查；
/// 网易云音乐、QQ 音乐先搜歌，再取那首歌的歌词（常带翻译）。
/// </summary>
public sealed class OnlineLyricsClient(SettingsStore settings, LrcLibClient lrcLib, NetEaseMusic netEase, QqMusic qq)
{
    readonly CoverMatcher _matcher = CoverMatcher.Default;
    const int DurationToleranceSec = 5, SearchCount = 6;

    public IReadOnlyList<OnlineSource> EnabledSources()
    {
        var enabled = settings.Current.LyricsSources.Select(Sources.ToCore).ToHashSet();
        return OnlineSources.ForLyrics.Where(enabled.Contains).ToList();
    }

    /// <summary>
    /// 自动获取用：按启用的来源依次找歌名、艺术家都对得上、时长相差不到 5 秒的歌词。
    /// 某个来源出错时试下一个；都没找到返回 null；全部出错时抛出最后一个异常（不算"没找到"）。
    /// </summary>
    public async Task<OnlineLyrics?> BestAsync(string title, string artist, string? album, long durationSec, CancellationToken ct = default)
    {
        Exception? error = null;
        var anySucceeded = false;
        foreach (var source in EnabledSources())
        {
            try
            {
                OnlineLyrics? found = null;
                if (source == OnlineSource.LRCLIB) found = await lrcLib.GetAsync(title, artist, album, durationSec, ct);
                else
                {
                    var songs = (await SongsAsync(source, title, artist, ct))
                        .Where(s => Matches(s, title, artist) && DurationClose(s.DurationMs, durationSec)).Take(2);
                    foreach (var s in songs)
                        if ((found = await LyricsOfAsync(s, ct)) is not null) break;
                }
                anySucceeded = true;
                if (found is not null) return found;
            }
            catch (Exception e) when (e is IOException or HttpRequestException or JsonException) { error = e; }
        }
        if (!anySucceeded && error is not null) throw error;
        return null;
    }

    /// <summary>手动搜索一个来源；网易云、QQ 音乐取前几首的歌词一起返回，没有歌词的不列出。网络出错时抛出异常。</summary>
    public async Task<IReadOnlyList<OnlineLyrics>> SearchAsync(OnlineSource source, string title, string? artist, CancellationToken ct = default)
    {
        IReadOnlyList<OnlineLyrics> found;
        if (source == OnlineSource.LRCLIB) found = await lrcLib.SearchAsync(title, artist, ct);
        else
        {
            var songs = (await SongsAsync(source, title, artist, ct)).Take(SearchCount);
            var tasks = songs.Select(async s => { try { return await LyricsOfAsync(s, ct); } catch (Exception e) when (e is IOException or HttpRequestException or JsonException) { return null; } });
            found = (await Task.WhenAll(tasks)).OfType<OnlineLyrics>().ToList();
        }
        return found.Select(l => l with
        {
            Matched = _matcher.TitleMatches(title, l.TrackName) && (string.IsNullOrWhiteSpace(artist) || _matcher.ArtistMatches(artist, l.ArtistName)),
        }).ToList();
    }

    async Task<IReadOnlyList<OnlineSong>> SongsAsync(OnlineSource source, string title, string? artist, CancellationToken ct)
    {
        var query = string.Join(' ', new[] { string.IsNullOrWhiteSpace(artist) ? null : CoverMatcher.PrimaryArtist(artist), title }.Where(s => s is not null));
        return source switch
        {
            OnlineSource.NETEASE => await netEase.SearchAsync(query, ct: ct),
            OnlineSource.QQ => await qq.SearchAsync(query, ct: ct),
            _ => [],
        };
    }

    async Task<OnlineLyrics?> LyricsOfAsync(OnlineSong song, CancellationToken ct)
    {
        var text = song.Source switch
        {
            OnlineSource.NETEASE => await netEase.LyricsAsync(song.Id, ct),
            OnlineSource.QQ => await qq.LyricsAsync(song.Id, ct),
            _ => null,
        };
        if (text is null) return null;
        var parsed = LrcParser.Parse(text);
        if (parsed.Lines.Count == 0 || IsInstrumentalPlaceholder(parsed)) return null;
        return new OnlineLyrics(song.Source, song.Id, song.Title, song.Artist, song.Album, song.DurationMs / 1000.0,
            parsed.Synced ? text : null, parsed.Synced ? null : text);
    }

    /// <summary>网易云、QQ 音乐给纯音乐的占位歌词（"此歌曲为没有填词的纯音乐，请您欣赏"），不算歌词。</summary>
    internal static bool IsInstrumentalPlaceholder(LyricsText lyrics) =>
        lyrics.Lines.Count(l => !string.IsNullOrWhiteSpace(l.Text)) <= 2 && lyrics.Lines.Any(l => l.Text.Contains("纯音乐", StringComparison.Ordinal));

    bool Matches(OnlineSong s, string title, string artist) => _matcher.TitleMatches(title, s.Title) && _matcher.ArtistMatches(artist, s.Artist);

    static bool DurationClose(long? onlineMs, long localSec) => onlineMs is null || localSec <= 0 || Math.Abs(onlineMs.Value / 1000.0 - localSec) <= DurationToleranceSec;
}

/// <summary>检测各在线来源能不能连上（需求 OL-3）：每个来源发一个最轻的请求，记下耗时；MusicBrainz 另外检测图片服务器 archive.org。</summary>
public sealed class SourceCheck(NetEaseMusic netEase, QqMusic qq, LrcLibClient lrcLib)
{
    public sealed record Result(OnlineSource Source, bool ImageHost, long? Millis);

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public async Task<IReadOnlyList<Result>> CheckAsync(IEnumerable<OnlineSource> sources, CancellationToken ct = default)
    {
        var probes = sources.SelectMany(s => s == OnlineSource.MUSICBRAINZ ? new[] { (s, false), (s, true) } : [(s, false)]);
        return await Task.WhenAll(probes.Select(async p => new Result(p.s, p.Item2, await TimeAsync(() => ProbeAsync(p.s, p.Item2, ct)))));
    }

    async Task ProbeAsync(OnlineSource source, bool imageHost, CancellationToken ct)
    {
        if (imageHost) { await Http.TextAsync("https://archive.org/robots.txt", timeout: Timeout, ct: ct); return; }
        switch (source)
        {
            case OnlineSource.NETEASE: await netEase.SearchAsync("test", 1, ct); break;
            case OnlineSource.QQ: await qq.SearchAsync("test", 1, ct); break;
            case OnlineSource.ITUNES: await Http.TextAsync("https://itunes.apple.com/search?term=test&limit=1", timeout: Timeout, ct: ct); break;
            case OnlineSource.MUSICBRAINZ: await Http.TextAsync("https://musicbrainz.org/ws/2/recording?query=test&limit=1&fmt=json", timeout: Timeout, ct: ct); break;
            default: await lrcLib.GetAsync("test", "test", null, 0, ct); break;
        }
    }

    static async Task<long?> TimeAsync(Func<Task> block)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await block();
            return sw.ElapsedMilliseconds;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or JsonException or OperationCanceledException) { return null; }
    }
}
