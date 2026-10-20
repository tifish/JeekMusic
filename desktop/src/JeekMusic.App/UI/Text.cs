using System.Globalization;
using JeekMusic.Library;

namespace JeekMusic.UI;

/// <summary>界面上的文字格式。</summary>
public static class Text
{
    public const string UnknownArtist = "未知艺术家";

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public static string Duration(long ms) => Duration(TimeSpan.FromMilliseconds(Math.Max(0, ms)));

    public static string Artist(Track t) => t.ArtistDisplay.Length == 0 ? UnknownArtist : t.ArtistDisplay;

    public static string Modified(long ticks) => new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string Count(int n) => $"{n:N0} 首";

    /// <summary>智能标签的中文名（需求 M-1）。</summary>
    public static string TagName(string? tag) => tag switch
    {
        "SPORT" => "运动",
        "NON_SPORT" => "非运动",
        "VOCAL" => "人声",
        "INSTRUMENTAL" => "纯音乐",
        "CLASSICAL" => "古典",
        "NON_CLASSICAL" => "非古典",
        _ => "",
    };

    public static string Tags(SongTags? tags)
    {
        if (tags is null) return "";
        return string.Join(" · ", new[] { tags.Mood, tags.Voice, tags.Genre }.Where(t => t is not null).Select(TagName));
    }
}
