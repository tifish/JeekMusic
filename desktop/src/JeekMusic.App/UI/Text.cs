using System.Globalization;
using JeekMusic.Core.Analysis;
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
        "CALM" => L.T("安静"),
        "NON_CALM" => L.T("非安静"),
        "VOCAL" => L.T("人声"),
        "INSTRUMENTAL" => L.T("纯音乐"),
        "CLASSICAL" => L.T("古典"),
        "NON_CLASSICAL" => L.T("非古典"),
        _ => "",
    };

    public static Tag? TagValue(SongTags? tags, TagDimension dimension) => Core.Analysis.Tags.Of(dimension, tags?[dimension.ToString()]);

    public static bool TagManual(SongTags? tags, TagDimension dimension) => tags is not null && dimension switch
    {
        TagDimension.MOOD => tags.MoodManual, TagDimension.VOICE => tags.VoiceManual,
        TagDimension.GENRE => tags.GenreManual, TagDimension.CALMNESS => tags.CalmnessManual, _ => false,
    };

    public static string TagCell(SongTags? tags, TagDimension dimension) => TagValue(tags, dimension) is { } value ? TagName(value.ToString()) : "—";
    public static string TagSource(SongTags? tags, TagDimension dimension) => TagValue(tags, dimension) is null
        ? L.T("无分析结果") : TagManual(tags, dimension) ? L.T("手动修正") : L.T("自动");

    public static string Tags(SongTags? tags)
    {
        if (tags is null) return "";
        return string.Join(" · ", new[] { tags.Mood, tags.Voice, tags.Genre, tags.Calmness }.Where(t => t is not null).Select(TagName));
    }
}
