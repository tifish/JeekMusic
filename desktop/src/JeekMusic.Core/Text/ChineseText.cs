using System.Collections.Frozen;
using System.Text;
using JeekMusic.Core.Online;

namespace JeekMusic.Core.Text;

/// <summary>
/// 繁简转换，结果和移动端 Android ICU 的 "Traditional-Simplified" / "Simplified-Traditional" 一致（移动端 ChineseText.kt）：
/// 比较在线结果时统一转成简体；iTunes 的中文只有繁体能搜到。
///
/// 规则表由 shared/pinyin/Generate.java 从 ICU4J 导出（traditional-simplified.tsv、simplified-traditional.tsv），
/// 有词组（"單于" → "单于"）和少量带上下文的规则（"三隻"：数字后面的"只"转成"隻"）。匹配方式同 ICU：
/// 在每个位置按规则顺序尝试以这个字开头的规则，第一条原文、前文、后文都对得上的生效，然后跳过原文继续。
/// </summary>
public static class ChineseText
{
    private sealed record PhraseRule(string Key, string Value, string? Before, string? After);

    private sealed class Table(FrozenDictionary<char, PhraseRule[]> byFirst)
    {
        public string Apply(string text)
        {
            StringBuilder? sb = null;
            var i = 0;
            while (i < text.Length)
            {
                if (byFirst.TryGetValue(text[i], out var rules))
                {
                    var rule = Match(rules, text, i, sb);
                    if (rule is not null)
                    {
                        sb ??= new StringBuilder(text.Length).Append(text, 0, i);
                        sb.Append(rule.Value);
                        i += rule.Key.Length;
                        continue;
                    }
                }
                sb?.Append(text[i]);
                i++;
            }
            return sb?.ToString() ?? text;
        }

        private static PhraseRule? Match(PhraseRule[] rules, string text, int index, StringBuilder? output)
        {
            foreach (var rule in rules)
            {
                if (string.CompareOrdinal(text, index, rule.Key, 0, rule.Key.Length) != 0 || index + rule.Key.Length > text.Length) continue;
                if (rule.After is { } after && !ContextMatches(after, text, index + rule.Key.Length)) continue;
                // 前文看已经转换过的结果（ICU 的语义）；还没有发生替换时就是原文
                if (rule.Before is { } before)
                {
                    var done = output?.ToString() ?? text[..index];
                    if (!EndsWithContext(before, done)) continue;
                }
                return rule;
            }
            return null;
        }

        /// <summary>"[...]" 是字符集合（逐字列出），否则是字符串。</summary>
        private static bool ContextMatches(string context, string text, int at)
        {
            if (IsSet(context))
            {
                if (at >= text.Length) return false;
                var rune = Rune.GetRuneAt(text, at);
                return context.AsSpan(1, context.Length - 2).IndexOf(rune.ToString()) >= 0;
            }
            return string.CompareOrdinal(text, at, context, 0, context.Length) == 0 && at + context.Length <= text.Length;
        }

        private static bool EndsWithContext(string context, string done)
        {
            if (!IsSet(context)) return done.EndsWith(context, StringComparison.Ordinal);
            if (done.Length == 0) return false;
            var last = char.IsLowSurrogate(done[^1]) && done.Length >= 2 ? done[^2..] : done[^1..];
            return context.AsSpan(1, context.Length - 2).IndexOf(last) >= 0;
        }

        private static bool IsSet(string context) => context.Length >= 2 && context[0] == '[' && context[^1] == ']';
    }

    private static readonly Lazy<Table> Simplified = new(() => Load("traditional-simplified.tsv"));
    private static readonly Lazy<Table> Traditional = new(() => Load("simplified-traditional.tsv"));

    private static Table Load(string name)
    {
        var byFirst = new Dictionary<char, List<PhraseRule>>();
        foreach (var row in TsvTables.Read(name))
        {
            var rule = new PhraseRule(row[0], row[1], row.Length > 2 && row[2].Length > 0 ? row[2] : null, row.Length > 3 && row[3].Length > 0 ? row[3] : null);
            if (!byFirst.TryGetValue(rule.Key[0], out var list)) byFirst[rule.Key[0]] = list = [];
            list.Add(rule);
        }
        return new Table(byFirst.ToFrozenDictionary(e => e.Key, e => e.Value.ToArray()));
    }

    /// <summary>繁 → 简；没有汉字时原样返回（同移动端，不加载表）。</summary>
    public static string ToSimplified(string text) => CoverMatcher.HasCjk(text) ? Simplified.Value.Apply(text) : text;

    /// <summary>简 → 繁。</summary>
    public static string ToTraditional(string text) => Traditional.Value.Apply(text);
}
