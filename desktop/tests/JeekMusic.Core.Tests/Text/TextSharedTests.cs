using System.Text;
using JeekMusic.Core.Online;
using JeekMusic.Core.Text;

namespace JeekMusic.Core.Tests.Text;

/// <summary>shared/testcases 里文本相关的用例：自然排序、排序键、拼音、繁简、编码。</summary>
public class TextSharedTests
{
    static TextSharedTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public void NaturalOrder()
    {
        foreach (var c in SharedCases.Load("natural-order.json").Cases())
        {
            var sorted = c["input"].Strings().OrderBy(s => s, NaturalOrderComparer.Instance).ToList();
            Assert.True(c["expected"].Strings().SequenceEqual(sorted), $"{c.Description()}: {string.Join(", ", sorted)}");
        }
    }

    [Fact]
    public void IndexLetter()
    {
        foreach (var c in SharedCases.Load("sort-keys.json").Cases("indexLetter"))
        {
            Assert.True(c.Str("expected") == SortKeys.IndexLetter(SortKeys.SortKey(c.Str("input")!, s => s)), c.Description());
        }
    }

    [Fact]
    public void SortKey()
    {
        foreach (var c in SharedCases.Load("sort-keys.json").Cases("sortKey"))
        {
            Assert.Equal(c.Str("expected"), SortKeys.SortKey(c.Str("input")!));
        }
    }

    [Fact]
    public void PinyinMatchesIcu()
    {
        foreach (var c in SharedCases.Load("pinyin.json").Cases())
        {
            Assert.Equal(c.Str("expected"), Pinyin.Transliterate(c.Str("input")!));
        }
    }

    [Fact]
    public void ChineseTextMatchesIcu()
    {
        foreach (var c in SharedCases.Load("chinese-text.json").Cases())
        {
            var input = c.Str("input")!;
            // 没有汉字（按 UTF-16 码元）时 toSimplified 直接返回原文（同移动端）
            Assert.Equal(CoverMatcher.HasCjk(input) ? c.Str("simplified") : input, ChineseText.ToSimplified(input));
            Assert.Equal(c.Str("traditional"), ChineseText.ToTraditional(input));
        }
    }

    /// <summary>Java 的字符集名 → .NET 的编码（UTF-16LE / UTF-16BE 在 .NET 里叫法不同）。</summary>
    internal static Encoding Enc(string name) => name.ToUpperInvariant() switch
    {
        "UTF-16LE" => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        "UTF-16BE" => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        "UTF-8" => new UTF8Encoding(false),
        _ => Encoding.GetEncoding(name),
    };

    private static string Mojibake(string text, string charset) => Encoding.Latin1.GetString(Enc(charset).GetBytes(text));

    [Fact]
    public void CharsetFix()
    {
        var file = SharedCases.Load("charset.json");
        foreach (var c in file.Cases("fix"))
        {
            var input = c.Str("mojibake") is { } m ? Mojibake(c.Str("text")!, m) : c.Str("text")!;
            var forced = c.Str("forced") is { } f ? Enc(f) : null;
            Assert.True((c.Str("expected") ?? input) == CharsetFixer.Fix(input, forced), c.Description());
        }
        foreach (var c in file.Cases("fixAll"))
        {
            var values = c["values"]!.AsArray().Select(v => v!.AsObject())
                .Select(v => v.Str("mojibake") is { } m ? Mojibake(v.Str("text")!, m) : v.Str("text")!);
            Assert.Equal(c["expected"].Strings(), CharsetFixer.FixAll(values));
        }
    }

    [Fact]
    public void CharsetDetect()
    {
        foreach (var c in SharedCases.Load("charset.json").Cases("detect"))
        {
            var prefix = Convert.FromHexString(c.Str("prefix")!);
            var bytes = prefix.Concat(Enc(c.Str("charset")!).GetBytes(c.Str("text")!)).ToArray();
            Assert.True(c.Str("expected") == CharsetDetector.Decode(bytes), c.Description());
        }
    }
}
