// 用法：dotnet run -c Release -- <目录>...
// 对每个音频文件分别用 ATL 和 TagLib# 读标签，打印不一致的字段统计和样例，以及两边的耗时。
using System.Diagnostics;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;
var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav", ".wma", ".ape", ".wv" };
var files = args.SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)).Where(f => exts.Contains(Path.GetExtension(f))).ToList();
Console.WriteLine($"{files.Count} files");

ATL.Settings.DisplayValueSeparator = '\u0000';
ATL.Settings.DefaultTextEncoding = Encoding.Latin1;
ATL.Settings.UseFileNameWhenNoTitle = false;
ATL.Settings.NullAbsentValues = true;

var atl = new Dictionary<string, Dictionary<string, string>>();
var tl = new Dictionary<string, Dictionary<string, string>>();
var sw = Stopwatch.StartNew();
foreach (var f in files) atl[f] = Atl(f);
var atlMs = sw.Elapsed.TotalMilliseconds;
sw.Restart();
foreach (var f in files) tl[f] = TagLibRead(f);
var tlMs = sw.Elapsed.TotalMilliseconds;
// 第二遍（文件已在缓存里）
sw.Restart();
foreach (var f in files) Atl(f);
var atlWarm = sw.Elapsed.TotalMilliseconds;
Console.WriteLine($"ATL {atlMs:F0} ms (warm {atlWarm:F0} ms, {atlWarm / files.Count:F2} ms/file), TagLib# {tlMs:F0} ms");

var diffs = new Dictionary<string, List<string>>();
foreach (var f in files)
{
    var a = atl[f]; var t = tl[f];
    foreach (var key in a.Keys.Union(t.Keys))
    {
        a.TryGetValue(key, out var av); t.TryGetValue(key, out var tv);
        if (Norm(key, av) == Norm(key, tv)) continue;
        if (!diffs.TryGetValue(key, out var list)) diffs[key] = list = new();
        list.Add($"{Path.GetFileName(f)}: ATL={Show(av)} TagLib={Show(tv)}");
    }
}
foreach (var (key, list) in diffs.OrderByDescending(d => d.Value.Count))
{
    Console.WriteLine($"== {key}: {list.Count} 个文件不一致");
    foreach (var line in list.Take(6)) Console.WriteLine("   " + line);
}

static string Show(string? s) => s is null ? "∅" : "\"" + s.Replace("\u0000", " | ") + "\"";

static string? Norm(string key, string? v)
{
    if (string.IsNullOrWhiteSpace(v)) return null;
    v = v.Trim();
    if (key == "duration") return v; // 已经按 100ms 取整
    return v;
}

static Dictionary<string, string> Atl(string path)
{
    var d = new Dictionary<string, string>();
    try
    {
        var t = new ATL.Track(path);
        void Put(string k, string? v) { if (!string.IsNullOrEmpty(v)) d[k] = v; }
        Put("title", t.Title); Put("artist", t.Artist); Put("album", t.Album); Put("albumArtist", t.AlbumArtist);
        Put("track", t.TrackNumber?.ToString()); Put("disc", t.DiscNumber?.ToString()); Put("year", t.Year?.ToString()); Put("genre", t.Genre);
        Put("duration", (Math.Round(t.DurationMs / 1000.0, 0)).ToString());
        Put("sampleRate", ((int)t.SampleRate).ToString()); Put("bits", t.BitDepth > 0 ? t.BitDepth.ToString() : null);
        Put("lyrics", t.Lyrics.Any(l => !string.IsNullOrEmpty(l.UnsynchronizedLyrics) || l.SynchronizedLyrics.Count > 0) ? "yes" : null);
        Put("cover", t.EmbeddedPictures.Count > 0 ? "yes" : null);
        if (t.AdditionalFields.TryGetValue("CUESHEET", out var cue)) Put("cuesheet", cue.Length.ToString());
    }
    catch (Exception e) { d["error"] = e.GetType().Name; }
    return d;
}

static Dictionary<string, string> TagLibRead(string path)
{
    var d = new Dictionary<string, string>();
    try
    {
        using var f = TagLib.File.Create(path);
        var t = f.Tag;
        void Put(string k, string? v) { if (!string.IsNullOrEmpty(v)) d[k] = v; }
        Put("title", t.Title); Put("artist", t.Performers.Length > 0 ? string.Join('\u0000', t.Performers) : null); Put("album", t.Album);
        Put("albumArtist", t.AlbumArtists.Length > 0 ? string.Join('\u0000', t.AlbumArtists) : null);
        Put("track", t.Track > 0 ? t.Track.ToString() : null); Put("disc", t.Disc > 0 ? t.Disc.ToString() : null);
        Put("year", t.Year > 0 ? t.Year.ToString() : null); Put("genre", t.Genres.Length > 0 ? string.Join('\u0000', t.Genres) : null);
        Put("duration", Math.Round(f.Properties.Duration.TotalSeconds, 0).ToString());
        Put("sampleRate", f.Properties.AudioSampleRate.ToString()); Put("bits", f.Properties.BitsPerSample > 0 ? f.Properties.BitsPerSample.ToString() : null);
        Put("lyrics", string.IsNullOrEmpty(t.Lyrics) ? null : "yes");
        Put("cover", t.Pictures.Length > 0 ? "yes" : null);
        if (f.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment x && x.GetFirstField("CUESHEET") is { } cue) Put("cuesheet", cue.Length.ToString());
    }
    catch (Exception e) { d["error"] = e.GetType().Name; }
    return d;
}
