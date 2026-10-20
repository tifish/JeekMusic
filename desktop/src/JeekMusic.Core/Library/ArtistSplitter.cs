using System.Text.RegularExpressions;

namespace JeekMusic.Core.Library;

/// <summary>艺术家拆分的配置（需求 A-3、A-4，移动端 ArtistSplitter.kt 的 ArtistSplitConfig）。</summary>
/// <param name="Separators">启用的分隔符（取自 <see cref="AllSeparators"/>）</param>
/// <param name="Exceptions">不拆分的例外名单（需求 A-4）</param>
public sealed record ArtistSplitConfig(IReadOnlySet<string> Separators, IReadOnlyList<string> Exceptions)
{
    /// <summary>所有可选的分隔符，设置页按这个顺序展示。</summary>
    public static readonly IReadOnlyList<string> AllSeparators = [";", "/", "、", "feat.", "ft.", "featuring", "&", ",", "x"];

    /// <summary>依据真实曲库统计确定的默认值（移动端设计 §4.4）。</summary>
    public static readonly IReadOnlySet<string> DefaultSeparators = new HashSet<string> { ";", "/", "、", "feat.", "ft.", "featuring" };

    public static readonly IReadOnlyList<string> DefaultExceptions = ["AC/DC", "Earth, Wind & Fire", "Simon & Garfunkel"];

    /// <summary>用词语作分隔符时，两侧必须有空白，避免把 "Fitz" 中的 "ft" 拆开。</summary>
    internal static readonly IReadOnlySet<string> WordSeparators = new HashSet<string> { "feat.", "ft.", "featuring", "x" };

    /// <summary>默认配置：<see cref="DefaultSeparators"/> + <see cref="DefaultExceptions"/>。</summary>
    public static ArtistSplitConfig Default { get; } = new(DefaultSeparators, DefaultExceptions);
}

/// <summary>把艺术家标签拆成多个艺术家（需求 A-2 ~ A-5，移动端 ArtistSplitter.kt，移动端设计 §4.4）。</summary>
public static partial class ArtistSplitter
{
    public static IReadOnlyList<string> Split(IEnumerable<string> values, ArtistSplitConfig? config = null)
    {
        config ??= ArtistSplitConfig.Default;
        var regex = BuildRegex(config.Separators);
        var keys = new HashSet<string>();
        var result = new List<string>();
        foreach (var value in values)
        {
            foreach (var name in SplitOne(value, regex, config.Exceptions))
            {
                if (keys.Add(NameKey(name))) result.Add(name);
            }
        }
        return result;
    }

    /// <summary>归一化的艺术家键：忽略大小写、首尾空格和连续空白（需求 A-5）。</summary>
    public static string NameKey(string name) => Whitespace().Replace(name.Trim(), " ").ToLowerInvariant();

    private static IEnumerable<string> SplitOne(string value, Regex? regex, IReadOnlyList<string> exceptions)
    {
        // 先把例外名单里的名字替换成占位符，拆完再换回来
        var protectedText = value;
        var placeholders = new List<string>();
        foreach (var exception in exceptions)
        {
            if (string.IsNullOrWhiteSpace(exception)) continue;
            protectedText = Regex.Replace(protectedText, Regex.Escape(exception), match =>
            {
                placeholders.Add(match.Value);
                return $"\u0001{placeholders.Count - 1}\u0001";
            }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        // "A (feat. B)" → "A feat. B"
        protectedText = BracketedFeat().Replace(protectedText, m => $" {m.Groups[1].Value} {m.Groups[2].Value}");

        var parts = regex?.Split(protectedText) ?? [protectedText];
        foreach (var part in parts)
        {
            var name = Placeholder().Replace(part, m => placeholders[int.Parse(m.Groups[1].Value)])
                .Trim()
                .Trim('(', ')', '（', '）', '[', ']')
                .Trim();
            if (name.Length > 0) yield return name;
        }
    }

    private static Regex? BuildRegex(IReadOnlySet<string> separators)
    {
        var symbols = separators.Where(s => !ArtistSplitConfig.WordSeparators.Contains(s)).ToList();
        var words = separators.Where(s => ArtistSplitConfig.WordSeparators.Contains(s)).ToList();
        var alternatives = new List<string>();
        if (symbols.Count > 0) alternatives.Add($"\\s*(?:{string.Join("|", symbols.Select(Regex.Escape))})\\s*");
        if (words.Count > 0)
        {
            var wordPattern = string.Join("|", words.Select(w =>
                Regex.Escape(w.EndsWith('.') ? w[..^1] : w) + (w.EndsWith('.') ? "\\.?" : "")));
            alternatives.Add($"\\s+(?:{wordPattern})\\s+");
        }
        if (alternatives.Count == 0) return null;
        return new Regex(string.Join("|", alternatives), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("\u0001(\\d+)\u0001")]
    private static partial Regex Placeholder();

    [GeneratedRegex("\\s*[(（\\[]\\s*(feat\\.?|ft\\.?|featuring)\\s+([^)）\\]]+)[)）\\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketedFeat();
}
