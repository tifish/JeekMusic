namespace JeekMusic.Core.Text;

/// <summary>
/// 自然排序："2.mp3" 排在 "10.mp3" 前面（需求 F-4，移动端 NaturalOrderComparator.kt）。
/// 数字段按数值比较，其余部分忽略大小写逐字比较（按 UTF-16 码元，和 Kotlin 一致）。
/// </summary>
public sealed class NaturalOrderComparer : IComparer<string>
{
    public static NaturalOrderComparer Instance { get; } = new();

    private NaturalOrderComparer()
    {
    }

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            var ca = a[i];
            var cb = b[j];
            if (IsAsciiDigit(ca) && IsAsciiDigit(cb))
            {
                var endA = DigitRunEnd(a, i);
                var endB = DigitRunEnd(b, j);
                var cmp = CompareDigitRuns(a.AsSpan(i, endA - i), b.AsSpan(j, endB - j));
                if (cmp != 0) return cmp;
                i = endA;
                j = endB;
            }
            else
            {
                var cmp = LowercaseChar(ca).CompareTo(LowercaseChar(cb));
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
        }
        var remaining = (a.Length - i).CompareTo(b.Length - j);
        return remaining != 0 ? remaining : string.CompareOrdinal(a, b);
    }

    /// <summary>Kotlin 的 Char.lowercaseChar()（Character.toLowerCase(char)），对单个 UTF-16 码元按不变区域转换。</summary>
    private static char LowercaseChar(char c) => char.ToLowerInvariant(c);

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    private static int DigitRunEnd(string s, int start)
    {
        var k = start;
        while (k < s.Length && IsAsciiDigit(s[k])) k++;
        return k;
    }

    private static int CompareDigitRuns(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        var tx = x.TrimStart('0');
        var ty = y.TrimStart('0');
        if (tx.Length != ty.Length) return tx.Length.CompareTo(ty.Length);
        var cmp = tx.SequenceCompareTo(ty);
        // 数值相同时，前导零少的排前面（"2" < "02"），保证结果稳定
        return cmp != 0 ? Math.Sign(cmp) : x.Length.CompareTo(y.Length);
    }
}
