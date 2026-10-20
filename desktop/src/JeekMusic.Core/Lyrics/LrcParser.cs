using System.Text.RegularExpressions;
using JeekMusic.Core.Library;

namespace JeekMusic.Core.Lyrics;

/// <summary>一行歌词；<see cref="Translation"/> 是同一时间戳的其他行（需求 LY-5）。</summary>
public sealed record LyricLine(long TimeMs, string Text, string? Translation = null);

/// <summary><see cref="Synced"/> 为 false 时是没有时间轴的纯文本歌词（需求 LY-4）。</summary>
public sealed record LyricsText(IReadOnlyList<LyricLine> Lines, bool Synced);

/// <summary>
/// LRC 解析（移动端 LrcParser.kt，移动端设计 §8.2）：
/// <c>[mm:ss.xx]</c>、<c>[mm:ss.xxx]</c>、<c>[mm:ss]</c>，一行多个时间戳；<c>[offset:+/-ms]</c>；<c>[ti:]</c>、<c>[ar:]</c> 等元信息忽略；
/// 同一时间戳出现两行时合并为原文 + 翻译（需求 LY-5）；逐字歌词（Enhanced LRC <c>&lt;mm:ss.xx&gt;</c>）去掉逐字时间标记，按行显示。
/// </summary>
public static partial class LrcParser
{
    public static LyricsText Parse(string text)
    {
        long offset = 0;
        var timed = new List<(long Time, string Text)>();
        var plain = new List<string>();

        foreach (var raw in CueParser.Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith('﻿')) line = line[1..];
            if (line.Length == 0)
            {
                plain.Add("");
                continue;
            }
            // 元信息标签（字母开头，时间标签是数字开头，不会匹配）
            var meta = MetaTag().Match(line);
            if (meta.Success)
            {
                if (meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase))
                {
                    var value = meta.Groups[2].Value.Trim();
                    if (value.StartsWith('+')) value = value[1..];
                    offset = long.TryParse(value, out var parsed) ? parsed : 0;
                }
                continue;
            }

            var rest = line;
            var times = new List<long>();
            while (true)
            {
                var match = TimeTag().Match(rest);
                if (!match.Success || match.Index != 0) break;
                times.Add(ToMillis(match));
                rest = rest[match.Length..];
            }
            var content = WordTag().Replace(rest, "").Trim();
            if (times.Count == 0) plain.Add(content);
            else foreach (var time in times) timed.Add((time, content));
        }

        if (timed.Count == 0)
        {
            var start = 0;
            while (start < plain.Count && plain[start].Length == 0) start++;
            var end = plain.Count;
            while (end > start && plain[end - 1].Length == 0) end--;
            return new LyricsText(plain[start..end].Select(t => new LyricLine(0, t)).ToList(), Synced: false);
        }

        // 同一时间戳的多行：第一行为原文，其余为翻译。按时间稳定排序，保留原文在前
        var lines = timed
            .Select((value, index) => (value, index))
            .OrderBy(e => e.value.Time).ThenBy(e => e.index)
            .GroupBy(e => e.value.Time)
            .Select(group =>
            {
                var texts = group.Select(e => e.value.Text).ToList();
                var translation = string.Join(" / ", texts.Skip(1).Where(t => t.Length > 0));
                // [offset] 为正时歌词提前显示（LRC 约定）
                return new LyricLine(Math.Max(0, group.Key - offset), texts[0], translation.Length == 0 ? null : translation);
            })
            .ToList();
        return new LyricsText(lines, Synced: true);
    }

    private static long ToMillis(Match match)
    {
        var minutes = long.Parse(match.Groups[1].Value);
        var seconds = long.Parse(match.Groups[2].Value);
        var fraction = match.Groups[3].Value;
        long ms = fraction.Length switch
        {
            0 => 0,
            1 => long.Parse(fraction) * 100,
            2 => long.Parse(fraction) * 10,
            _ => long.Parse(fraction[..3]),
        };
        return minutes * 60_000 + seconds * 1_000 + ms;
    }

    /// <summary>当前应高亮的行：最后一个时间 ≤ 播放进度的行；还没到第一行时返回 -1。二分查找。</summary>
    public static int CurrentIndex(IReadOnlyList<LyricLine> lines, long positionMs)
    {
        int low = 0, high = lines.Count - 1, result = -1;
        while (low <= high)
        {
            var mid = (low + high) >>> 1;
            if (lines[mid].TimeMs <= positionMs)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return result;
    }

    [GeneratedRegex("\\[(\\d{1,3}):(\\d{1,2})(?:[.:](\\d{1,3}))?]")]
    private static partial Regex TimeTag();

    /// <summary>Kotlin 的 matchEntire：整行都要匹配。</summary>
    [GeneratedRegex("^\\[([a-zA-Z#]+):(.*)]\\z")]
    private static partial Regex MetaTag();

    [GeneratedRegex("<\\d{1,3}:\\d{1,2}(?:[.:]\\d{1,3})?>")]
    private static partial Regex WordTag();
}
