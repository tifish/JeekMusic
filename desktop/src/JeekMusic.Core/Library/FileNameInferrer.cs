using System.Text.RegularExpressions;

namespace JeekMusic.Core.Library;

/// <summary>文件名的格式（需求 A-9）。</summary>
public enum FileNameFormat
{
    ARTIST_TITLE,
    TITLE_ARTIST,
}

public sealed record InferredName(string Artist, string Title);

/// <summary>艺术家标签为空时，从文件名推断艺术家和标题（需求 A-9，移动端 FileNameInferrer.kt，移动端设计 §4.5）。</summary>
public static partial class FileNameInferrer
{
    public static InferredName? Infer(string fileName, FileNameFormat format = FileNameFormat.ARTIST_TITLE)
    {
        var dot = fileName.LastIndexOf('.');
        var name = dot >= 0 ? fileName[..dot] : fileName;
        var track = TrackNumber().Match(name);
        if (track.Success)
        {
            var stripped = name[(track.Index + track.Length)..];
            // 去掉音轨号后没有分隔符，说明文件名只是"音轨号 - 标题"，推断不出艺术家
            if (!Separator().IsMatch(stripped)) return null;
            name = stripped;
        }
        var separator = Separator().Match(name);
        if (!separator.Success) return null;
        var left = name[..separator.Index].Trim();
        var right = name[(separator.Index + separator.Length)..].Trim();
        if (left.Length == 0 || right.Length == 0) return null;
        return format switch
        {
            FileNameFormat.ARTIST_TITLE => new InferredName(Artist: left, Title: right),
            _ => new InferredName(Artist: right, Title: left),
        };
    }

    /// <summary>开头的音轨号，例如 "01. "、"01 - "、"1_"、"03)"。</summary>
    [GeneratedRegex("^\\d{1,3}\\s*[.\\-_)、]\\s*")]
    private static partial Regex TrackNumber();

    /// <summary>普通连字符两侧必须有空格（避免拆开 "Jay-Z"），破折号和全角连字符两侧空格可选。</summary>
    [GeneratedRegex("\\s+-\\s+|\\s*[–—－]\\s*")]
    private static partial Regex Separator();
}
