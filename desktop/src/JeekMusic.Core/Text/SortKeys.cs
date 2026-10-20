using System.Text.RegularExpressions;

namespace JeekMusic.Core.Text;

/// <summary>
/// 排序键和字母索引：中文转拼音后按 A–Z 排序（需求 A-1、U-8，移动端 SortKeys.kt）。
/// 转写用 <see cref="Pinyin.Transliterate"/>（等价于移动端的 ICU "Han-Latin; Latin-ASCII"）；测试里可以传入别的转写函数。
/// </summary>
public static partial class SortKeys
{
    /// <summary>"周杰伦" → "zhou jie lun"：转写、小写（不变区域）、合并连续空白、去掉首尾空白。</summary>
    public static string SortKey(string name) => SortKey(name, Pinyin.Transliterate);

    public static string SortKey(string name, Func<string, string> transliterate) =>
        Whitespace().Replace(transliterate(name.Trim()).ToLowerInvariant(), " ").Trim();

    /// <summary>排序键的第一个字母（A–Z），其他都归到 "#"。</summary>
    public static string IndexLetter(string sortKey)
    {
        if (sortKey.Length == 0) return "#";
        var first = char.ToUpperInvariant(sortKey[0]);
        return first is >= 'A' and <= 'Z' ? first.ToString() : "#";
    }

    // 移动端在手机上运行时 java.util.regex 由 ICU 实现，\s 匹配 Unicode 空白，和 .NET 的 \s 一致
    // （JVM 上的单元测试里 \s 只匹配 ASCII 空白；共用测试用例不依赖这个差别）
    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
