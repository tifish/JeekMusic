using System.Text;

namespace JeekMusic.Library;

/// <summary>Character-based title filters, independent of audio language and analysis results.</summary>
public static class TitleTextFilter
{
    public static bool Matches(string title, string tag)
    {
        var chinese = tag is "CHINESE" or "NON_CHINESE";
        var contains = false;
        // Width normalization lets full-width English titles follow the same rule.
        foreach (var rune in title.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            var c = rune.Value;
            if (chinese ? IsHan(c) : c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                contains = true;
                break;
            }
        }
        return tag is "NON_CHINESE" or "NON_ENGLISH" ? !contains : contains;
    }

    static bool IsHan(int c) => c is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
        or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2EE5F
        or >= 0x2F800 and <= 0x2FA1F or >= 0x30000 and <= 0x3347F or 0x3007;
}
