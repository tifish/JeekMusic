using System.Globalization;
using JeekMusic.Library;

namespace JeekMusic.UI;

/// <summary>界面上的文字格式。</summary>
public static class Text
{
    public static string UnknownArtist => L.T("未知艺术家");

    public static string PlaybackStatus(bool playing) => playing ? L.T("正在播放") : L.T("当前歌曲（已暂停）");

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public static string Duration(long ms) => Duration(TimeSpan.FromMilliseconds(Math.Max(0, ms)));

    public static string Artist(Track t) => t.ArtistDisplay.Length == 0 ? UnknownArtist : t.ArtistDisplay;

    public static string Modified(long ticks) => FileModified.Utc(ticks)?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "—";

    public static string Count(int n) => L.F($"{n:N0} 首");

    /// <summary>智能标签的中文名（需求 M-1）。</summary>
    public static string TagName(string? tag) => tag switch
    {
        "SPORT" => L.T("运动"),
        "NON_SPORT" => L.T("非运动"),
        "VOCAL" => L.T("人声"),
        "INSTRUMENTAL" => L.T("纯音乐"),
        "CLASSICAL" => L.T("古典"),
        "NON_CLASSICAL" => L.T("非古典"),
        _ => "",
    };

    public static string Tags(SongTags? tags)
    {
        if (tags is null) return "";
        return string.Join(" · ", new[] { tags.Mood, tags.Voice, tags.Genre }.Where(t => t is not null).Select(TagName));
    }
}
