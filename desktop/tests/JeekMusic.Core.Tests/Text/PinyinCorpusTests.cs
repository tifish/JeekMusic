using System.Text;
using JeekMusic.Core.Text;
using Xunit.Abstractions;

namespace JeekMusic.Core.Tests.Text;

/// <summary>
/// 大语料对比：和 ICU 逐条比较拼音、繁简转换的结果。语料不进仓库，用 shared/pinyin/generate.sh expect 生成
/// （"原文\t拼音\t繁转简\t简转繁"），由环境变量 JEEK_PINYIN_CORPUS 指定；没有指定时跳过。
/// </summary>
public class PinyinCorpusTests(ITestOutputHelper output)
{
    [Fact]
    public void MatchesIcuOnCorpus()
    {
        var path = Environment.GetEnvironmentVariable("JEEK_PINYIN_CORPUS");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("JEEK_PINYIN_CORPUS 没有指定，跳过");
            return;
        }
        var failures = new List<string>();
        var counts = new int[3];
        var total = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            var cols = line.Split('\t').Select(Unescape).ToArray();
            // 移动端的 toSimplified 先按 UTF-16 码元判断有没有汉字，只有扩展区汉字（代理对）的文本原样返回；桌面端照搬
            if (!Online.CoverMatcher.HasCjk(cols[0])) cols[2] = cols[0];
            total++;
            var actual = new[] { Pinyin.Transliterate(cols[0]), ToSimplifiedRaw(cols[0]), ChineseText.ToTraditional(cols[0]) };
            for (var i = 0; i < 3; i++)
            {
                if (actual[i] == cols[i + 1]) continue;
                counts[i]++;
                if (failures.Count < 40) failures.Add($"[{new[] { "拼音", "繁转简", "简转繁" }[i]}] {Show(cols[0])} → 期望 {Show(cols[i + 1])}，实际 {Show(actual[i])}");
            }
        }
        output.WriteLine($"共 {total} 条；不一致：拼音 {counts[0]}，繁转简 {counts[1]}，简转繁 {counts[2]}");
        foreach (var f in failures) output.WriteLine(f);
        Assert.True(counts.Sum() == 0, string.Join("\n", failures.Prepend($"拼音 {counts[0]}，繁转简 {counts[1]}，简转繁 {counts[2]}")));
    }

    private static string ToSimplifiedRaw(string text) => ChineseText.ToSimplified(text);

    private static string Show(string s) => "\"" + s.Replace("\t", "\\t").Replace("\n", "\\n") + "\" " +
        string.Join(" ", Pinyin.CodePoints(s).Select(c => c.ToString("X4")));

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var n = s[++i];
                sb.Append(n == 't' ? '\t' : n == 'n' ? '\n' : n);
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
