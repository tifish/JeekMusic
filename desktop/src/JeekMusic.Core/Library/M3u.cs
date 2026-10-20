using System.Net;
using System.Text;

namespace JeekMusic.Core.Library;

/// <summary>
/// m3u / m3u8 读写（需求 PL-4，移动端 M3u.kt）。
/// 导出：扩展 m3u（#EXTM3U + #EXTINF）。导入：路径可能是绝对路径、相对路径、Windows 路径，
/// 统一拆成路径段，交给调用方做后缀匹配（<see cref="BackupRefs.Matcher"/>）。
/// </summary>
public static class M3u
{
    public sealed record Entry(string Path, long DurationSec, string Title);

    public static string Format(IEnumerable<Entry> entries)
    {
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var e in entries)
        {
            sb.Append("#EXTINF:").Append(e.DurationSec).Append(',').Append(e.Title.Replace('\n', ' ')).Append('\n');
            sb.Append(e.Path).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>解析出每个条目的路径段（已去掉 file:// 前缀、统一按 "/" 和 "\" 分隔、去掉 "."、".." 和盘符）。</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        var result = new List<IReadOnlyList<string>>();
        foreach (var raw in CueParser.Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith('﻿')) line = line[1..];
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("file://", StringComparison.Ordinal)) line = line["file://".Length..];
            // Java 的 URLDecoder 也把 "+" 解成空格，WebUtility.UrlDecode 一样
            if (line.Contains('%')) line = WebUtility.UrlDecode(line);
            var segments = line.Replace('\\', '/').Split('/')
                .Where(s => s.Length > 0 && s != "." && s != ".." && !s.EndsWith(':'))
                .ToList();
            if (segments.Count > 0) result.Add(segments);
        }
        return result;
    }

    /// <summary>移动端：卷名 → 文件系统中的挂载路径（导出时用）。</summary>
    public static string StoragePath(string volumeName, string relativePath, string fileName)
    {
        var root = volumeName == "external_primary" ? "/storage/emulated/0" : $"/storage/{volumeName.ToUpperInvariant()}";
        return $"{root}/{relativePath}{fileName}";
    }
}
