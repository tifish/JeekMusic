using System.Text;

namespace JeekMusic.Core.Text;

/// <summary>严格解码用的编码（遇到非法字节抛异常）；GBK、Big5 需要 CodePagesEncodingProvider。</summary>
public static class Encodings
{
    // 注意：静态属性的初始化器比静态构造函数的函数体先执行，所以不能在静态构造里注册（GetEncoding(936) 会先抛
    // NotSupportedException）；在 Strict 里、取代码页之前注册。RegisterProvider 重复调用没有副作用。

    /// <summary>注册 GBK、Big5、Shift-JIS 等代码页（.NET 默认只有 UTF-8、UTF-16、Latin-1 等）。用到这些编码之前调用。</summary>
    public static void EnsureCodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>GBK（Windows 代码页 936）。</summary>
    public static Encoding Gbk { get; } = Strict(936);

    /// <summary>Big5（Windows 代码页 950）。</summary>
    public static Encoding Big5 { get; } = Strict(950);

    /// <summary>Shift-JIS（Windows 代码页 932），设置里可以强制指定的旧标签编码之一。</summary>
    public static Encoding ShiftJis { get; } = Strict(932);

    public static Encoding Utf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static Encoding Strict(int codePage)
    {
        EnsureCodePages();
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    /// <summary>按名字取严格解码的编码（设置里保存的旧标签编码："GBK"、"Big5"、"Shift_JIS"、"UTF-8"）；不认识时返回 null。</summary>
    public static Encoding? ByName(string? name) => name?.ToUpperInvariant() switch
    {
        "GBK" or "GB2312" or "GB18030" => Gbk,
        "BIG5" => Big5,
        "SHIFT_JIS" or "SHIFT-JIS" or "SJIS" => ShiftJis,
        "UTF-8" or "UTF8" => Utf8,
        _ => null,
    };

    /// <summary>严格解码；字节不合法时返回 null（同移动端的 decodeStrict）。</summary>
    public static string? DecodeStrict(byte[] bytes, Encoding encoding, int offset = 0)
    {
        try
        {
            return encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}

/// <summary>
/// 文本文件（.cue、.lrc）的编码识别（移动端 CharsetDetector.kt，移动端设计 §8.3）：
/// BOM → 严格 UTF-8 → GBK / Big5 → UTF-8 替换模式。
///
/// Big5 的字节序列往往也是合法的 GBK，所以不能只看能否解码：
/// 所有双字节都落在 GB2312 区（两个字节都 ≥ 0xA1）时按 GBK；否则先试 Big5（它的第二个字节常小于 0xA1），再试 GBK。
/// </summary>
public static class CharsetDetector
{
    public static string Decode(byte[] bytes)
    {
        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF)) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (StartsWith(bytes, 0xFF, 0xFE)) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (StartsWith(bytes, 0xFE, 0xFF)) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (Encodings.DecodeStrict(bytes, Encodings.Utf8) is { } utf8) return utf8;
        var order = AllPairsInGb2312(bytes) ? new[] { Encodings.Gbk, Encodings.Big5 } : [Encodings.Big5, Encodings.Gbk];
        foreach (var encoding in order)
        {
            if (Encodings.DecodeStrict(bytes, encoding) is { } decoded) return decoded;
        }
        return Encoding.UTF8.GetString(bytes);
    }

    internal static bool AllPairsInGb2312(byte[] bytes)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                i++;
                continue;
            }
            if (b is < 0xA1 or > 0xF7 || i + 1 >= bytes.Length) return false;
            if (bytes[i + 1] is < 0xA1 or > 0xFE) return false;
            i += 2;
        }
        return true;
    }

    private static bool StartsWith(byte[] bytes, params int[] prefix)
    {
        if (bytes.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i]) return false;
        }
        return true;
    }
}

/// <summary>
/// 修复 ID3v2.3 / ID3v1 中"声明为 ISO-8859-1、实际存的是 GBK 字节"的乱码（移动端 CharsetFixer.kt，移动端设计 §4.3）。
///
/// 标签库把这类标签按 Latin-1 原样解码，所以每个字符正好对应一个原始字节，可以无损还原。
/// </summary>
public static class CharsetFixer
{
    /// <param name="value">标签值</param>
    /// <param name="forced">用户在设置里强制指定的旧标签编码；为 null 时自动判断（只尝试 GBK）。</param>
    public static string Fix(string value, Encoding? forced = null)
    {
        // 含有 Latin-1 以外的字符，说明已经是正确的 Unicode
        foreach (var c in value)
        {
            if (c > 0xFF) return value;
        }
        // 纯 ASCII，不需要修复
        var hasHigh = false;
        foreach (var c in value)
        {
            if (c >= 0x80)
            {
                hasHigh = true;
                break;
            }
        }
        if (!hasHigh) return value;

        var bytes = Encoding.Latin1.GetBytes(value);
        if (forced is not null) return Encodings.DecodeStrict(bytes, Strict(forced)) ?? value;

        if (!CharsetDetector.AllPairsInGb2312(bytes)) return value;
        var decoded = Encodings.DecodeStrict(bytes, Encodings.Gbk);
        if (decoded is null) return value;
        return UnicodeScripts.ContainsHan(decoded) ? decoded : value;
    }

    public static IReadOnlyList<string> FixAll(IEnumerable<string> values, Encoding? forced = null) =>
        values.Select(v => Fix(v, forced)).ToList();

    /// <summary>调用方传进来的编码不一定是严格模式，统一换成严格解码。</summary>
    private static Encoding Strict(Encoding encoding)
    {
        if (encoding.DecoderFallback is DecoderExceptionFallback) return encoding;
        Encodings.EnsureCodePages();
        return Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}

/// <summary>Unicode 文字（Script）判断。.NET 没有文字属性，用 ICU 导出的集合（shared/pinyin/sets.tsv）。</summary>
public static class UnicodeScripts
{
    private static readonly Lazy<CodePointSet> Han = new(() => CodePointSet.Load("sets.tsv")["Han"]);

    /// <summary>移动端的 Character.UnicodeScript.of(c) == HAN（按 UTF-16 码元判断，和 Kotlin 的 Char 一致）。</summary>
    public static bool IsHan(char c) => Han.Value.Contains(c);

    public static bool ContainsHan(string text)
    {
        foreach (var c in text)
        {
            if (IsHan(c)) return true;
        }
        return false;
    }
}
