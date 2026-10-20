using System.Text.RegularExpressions;

namespace JeekMusic.Core.Library;

/// <summary>
/// 专辑归属键（需求 AL-4，移动端 AlbumKeys.kt，移动端设计 §5.4）：
/// 有专辑艺术家：专辑名 + 专辑艺术家；没有专辑艺术家：专辑名 + 所在目录（CD1/CD2 这类碟片目录取上一级）。
/// 目录分隔符 "/"（移动端）和 "\"（桌面端）都可以，以 relativePath 里出现的为准。
/// </summary>
public static partial class AlbumKeys
{
    public static string? Of(string? album, string? albumArtist, string volume, string relativePath)
    {
        var name = album?.Trim() ?? "";
        if (name.Length == 0) return null;
        var artist = albumArtist?.Trim() ?? "";
        return artist.Length > 0
            ? $"a:{name.ToLowerInvariant()}|{artist.ToLowerInvariant()}"
            : $"d:{name.ToLowerInvariant()}|{volume}:{AlbumDirectory(relativePath)}";
    }

    public static string AlbumDirectory(string relativePath)
    {
        var separator = relativePath.Contains('\\') ? '\\' : '/';
        var segments = relativePath.TrimEnd('/', '\\').Split('/', '\\');
        if (segments.Length > 1 && DiscDirectory().IsMatch(segments[^1]))
        {
            return string.Join(separator, segments[..^1]) + separator;
        }
        return relativePath;
    }

    [GeneratedRegex("^(cd|disc|disk)\\s*\\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscDirectory();
}
