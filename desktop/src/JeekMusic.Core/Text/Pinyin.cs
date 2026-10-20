using System.Collections.Frozen;
using System.Text;

namespace JeekMusic.Core.Text;

/// <summary>
/// 汉字转拼音，结果和移动端 Android ICU 的 <c>Transliterator.getInstance("Han-Latin; Latin-ASCII")</c> 完全一致
/// （移动端 SortKeys.kt、ChineseText.kt；desktop/docs/design.md §6.4）。用于排序键（需求 A-1、U-8）和拼音首字母搜索（需求 S-2）。
///
/// ICU 的这个转写由几轮组成，这里逐轮复现，每轮用到的表和字符集合都由 shared/pinyin/Generate.java 从 ICU4J 导出：
/// <list type="number">
/// <item>Han-Spacedhan 前两轮：汉字、中文标点等的 NFKC，然后全角转半角（片假名也转成半角）。逐字查表 spacedhan-pre.tsv。</item>
/// <item>Han-Spacedhan 第三轮：中文标点映射（。→ .、「→ ‘ 等，spacedhan-punct.tsv），以及两条插空格的规则：
///   ① 前一个字符是汉字、句读标点（. , : ; ? ! 和全角形式）或收尾括号引号，后一个是字母（汉字也算字母）时插入空格；
///   ② 前面是字母（后面可以跟附加符号），后一个是汉字或开括号、开引号时插入空格。
///   同一轮里标点映射排在前面：一个字符有标点映射时直接替换，不在它前面插空格。前文看已经输出的结果，后文看原字符。</item>
/// <item>Han-Latin：多音字的上下文规则（如"重"后面是"庆"读 chóng，han-latin-context.tsv），再逐字查带声调的拼音（han-latin.tsv）。</item>
/// <item>Latin-ASCII：只处理拉丁、通用、继承文字（其余字符原样保留，并把文本分成几段分别处理）：NFD 分解，
///   去掉紧跟在拉丁字母或数字后面的非间距附加符号，NFC 合成，再逐字查表（latin-ascii.tsv，如 ō → o、ß → ss、« → &lt;&lt;）。</item>
/// </list>
/// 所以 "周杰伦" → "zhou jie lun"，"周杰伦Jay" → "zhou jie lun Jay"，"晴天.mp3" → "qing tian. mp3"。
/// 表在第一次使用时加载（约 50ms），之后线程安全、无锁。
/// </summary>
public static class Pinyin
{
    private sealed class Tables
    {
        public required FrozenDictionary<int, string> Pre { get; init; }
        public required FrozenDictionary<int, string> Punct { get; init; }
        public required FrozenDictionary<int, (int Next, string Output)[]> Context { get; init; }
        public required FrozenDictionary<int, string> HanLatin { get; init; }
        public required FrozenDictionary<int, string> LatinAscii { get; init; }
        public required CodePointSet SpacingPrev { get; init; }
        public required CodePointSet SpacingNext { get; init; }
        public required CodePointSet Letter { get; init; }
        public required CodePointSet Mark { get; init; }
        public required CodePointSet LatinAsciiFilter { get; init; }
        public required CodePointSet LatinOrDigit { get; init; }
        public required CodePointSet Mn { get; init; }
    }

    private static readonly Lazy<Tables> Data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Tables Load()
    {
        var sets = CodePointSet.Load("sets.tsv");
        var context = new Dictionary<int, List<(int, string)>>();
        foreach (var row in TsvTables.Read("han-latin-context.tsv"))
        {
            var key = Rune.GetRuneAt(row[0], 0).Value;
            if (!context.TryGetValue(key, out var list)) context[key] = list = [];
            list.Add((Rune.GetRuneAt(row[1], 0).Value, row[2]));
        }
        return new Tables
        {
            Pre = TsvTables.CharMap("spacedhan-pre.tsv").ToFrozenDictionary(),
            Punct = TsvTables.CharMap("spacedhan-punct.tsv").ToFrozenDictionary(),
            Context = context.ToFrozenDictionary(e => e.Key, e => e.Value.ToArray()),
            HanLatin = TsvTables.CharMap("han-latin.tsv").ToFrozenDictionary(),
            LatinAscii = TsvTables.CharMap("latin-ascii.tsv").ToFrozenDictionary(),
            SpacingPrev = sets["SpacingPrev"],
            SpacingNext = sets["SpacingNext"],
            Letter = sets["Letter"],
            Mark = sets["Mark"],
            LatinAsciiFilter = sets["LatinAsciiFilter"],
            LatinOrDigit = sets["LatinOrDigit"],
            Mn = sets["Mn"],
        };
    }

    /// <summary>提前加载表（比如在后台线程预热），避免第一次排序时卡一下。</summary>
    public static void Preload() => _ = Data.Value;

    public static string Transliterate(string text)
    {
        if (text.Length == 0) return text;
        var t = Data.Value;

        // 1. NFKC（限定字符集）+ 全角转半角
        var pass1 = new List<int>(text.Length + 8);
        foreach (var c in CodePoints(text))
        {
            if (t.Pre.TryGetValue(c, out var mapped)) AddRunes(pass1, mapped);
            else pass1.Add(c);
        }

        // 2. 标点映射 + 插空格
        var pass2 = new List<int>(pass1.Count + 8);
        foreach (var c in pass1)
        {
            if (t.Punct.TryGetValue(c, out var punct))
            {
                AddRunes(pass2, punct);
                continue;
            }
            if (pass2.Count > 0 && NeedsSpace(t, pass2, c)) pass2.Add(' ');
            pass2.Add(c);
        }

        // 3. Han-Latin
        var pass3 = new StringBuilder(pass2.Count * 3);
        for (var i = 0; i < pass2.Count; i++)
        {
            var c = pass2[i];
            if (t.Context.TryGetValue(c, out var rules) && ContextOutput(rules, pass2, i) is { } output) pass3.Append(output);
            else if (t.HanLatin.TryGetValue(c, out var pinyin)) pass3.Append(pinyin);
            else AppendRune(pass3, c);
        }

        // 4. Latin-ASCII
        return LatinAscii(t, pass3.ToString());
    }

    private static bool NeedsSpace(Tables t, List<int> output, int next)
    {
        var prev = output[^1];
        if (t.SpacingPrev.Contains(prev) && t.Letter.Contains(next)) return true;
        if (!t.SpacingNext.Contains(next)) return false;
        var k = output.Count - 1;
        while (k >= 0 && t.Mark.Contains(output[k])) k--;
        return k >= 0 && t.Letter.Contains(output[k]);
    }

    /// <summary>上下文规则 {字}' '?后字：后面（可以隔一个空格）是指定的字。</summary>
    private static string? ContextOutput((int Next, string Output)[] rules, List<int> text, int index)
    {
        foreach (var (next, output) in rules)
        {
            var j = index + 1;
            if (j < text.Count && text[j] == ' ' && j + 1 < text.Count && text[j + 1] == next) return output;
            if (j < text.Count && text[j] == next) return output;
        }
        return null;
    }

    private static string LatinAscii(Tables t, string text)
    {
        var result = new StringBuilder(text.Length);
        var run = new StringBuilder();
        foreach (var c in CodePoints(text))
        {
            // 孤立的代理项不在过滤集合里，不会进入 Normalize（它遇到孤立代理项会抛异常）
            if (t.LatinAsciiFilter.Contains(c))
            {
                AppendRune(run, c);
                continue;
            }
            FlushLatinAsciiRun(t, run, result);
            AppendRune(result, c);
        }
        FlushLatinAsciiRun(t, run, result);
        return result.ToString();
    }

    private static void FlushLatinAsciiRun(Tables t, StringBuilder run, StringBuilder result)
    {
        if (run.Length == 0) return;
        var decomposed = run.ToString().Normalize(NormalizationForm.FormD);
        run.Clear();
        // 去掉紧跟在拉丁字母或数字后面的非间距附加符号（连续的一串都去掉）
        var stripped = new StringBuilder(decomposed.Length);
        var last = -1;
        foreach (var rune in decomposed.EnumerateRunes())
        {
            if (t.Mn.Contains(rune.Value) && last >= 0 && t.LatinOrDigit.Contains(last)) continue;
            stripped.Append(rune.ToString());
            last = rune.Value;
        }
        foreach (var rune in stripped.ToString().Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (t.LatinAscii.TryGetValue(rune.Value, out var ascii)) result.Append(ascii);
            else result.Append(rune.ToString());
        }
    }

    private static void AddRunes(List<int> target, string s)
    {
        foreach (var c in CodePoints(s)) target.Add(c);
    }

    /// <summary>逐个码位；孤立的代理项原样作为一个"码位"返回（ICU 也原样保留它们，EnumerateRunes 会换成 U+FFFD）。</summary>
    internal static IEnumerable<int> CodePoints(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(c, s[i + 1]);
                i++;
            }
            else
            {
                yield return c;
            }
        }
    }

    private static void AppendRune(StringBuilder sb, int codePoint)
    {
        if (codePoint < 0x10000) sb.Append((char)codePoint);
        else sb.Append(char.ConvertFromUtf32(codePoint));
    }
}
