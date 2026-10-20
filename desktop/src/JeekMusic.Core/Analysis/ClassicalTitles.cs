using System.Text.RegularExpressions;

namespace JeekMusic.Core.Analysis;

/// <summary>
/// 用曲名补救模型漏判的古典（需求 M-10，移动端 ClassicalTitles.kt，移动端设计 §6.4）：古典作品的名字里常有作品编号
/// （BWV、Op.、K.、RV……）、带编号或调性的曲式（Symphony No. 5、Concerto in D minor）、乐章的速度标记（II. Adagio），
/// 中文的"交响曲""协奏曲"等。模型判为非古典、但歌名或专辑名里有这些时算古典。
///
/// 只收不容易误判的写法：中文流行歌常用的"夜曲""圆舞曲""狂想曲""变奏曲""序曲""组曲"不算，
/// 单独的速度词（一首歌就叫"Presto"）不算，单独的曲式词（流行歌"Symphony"）不算。
/// 和 mobile/tools/calibration/evaluate.py 里的规则保持一致。
/// </summary>
public static class ClassicalTitles
{
    private static readonly string[] Patterns =
    [
        // 作品编号
        @"\bBWV\s*\.?\s*\d", @"\bOp\.?\s*\d", @"\bOpus\s*\d", @"\bK\.?\s*V?\s*\d{2,3}\b", @"\bKV\s*\d", @"\bRV\s*\d",
        @"\bHob\.?\s*[IVX]", @"\bHWV\s*\d", @"\bWoO\s*\d", @"\bD\.?\s*\d{2,3}\b", @"\bS\.\s*\d{2,3}\b", @"\bL\.\s*\d{2,3}\b",
        @"\bMWV\b", @"\bTWV\b", @"\bBuxWV\b", @"\bSz\.?\s*\d",
        // 带编号、调性或作品号的曲式
        @"\b(Symphony|Symphonie|Sinfonie|Concerto|Konzert|Sonata|Sonate|Quartet|Quintet|Trio|Suite|Partita|Mass|Requiem|Cantata|Etude|Étude|" +
        @"Nocturne|Prelude|Prélude|Fugue|Mazurka|Polonaise|Ballade|Scherzo|Impromptu|Rhapsody|Overture|Ouvertüre|Serenade|Divertimento|" +
        @"Variations?|Waltz|Valse)\s+(No\.?\s*\d|Nr\.?\s*\d|in\s+[A-G]\b|op\.?\s*\d)",
        // 调性：in D minor、in E-flat major
        @"\b(in|en)\s+[A-G](\s*(flat|sharp|♭|♯|-flat|-sharp))?\s+(major|minor|Dur|moll|majeur|mineur)\b",
        // 乐章的速度标记：前面要有乐章号或分隔符
        @"(?:\b[IVX]+\.|\b\d+\.|[:：–-])\s*(Allegro|Adagio|Andante|Largo|Presto|Moderato|Allegretto|Larghetto|Vivace|Lento|Grave|Menuetto|Minuet|Rondo)\b",
        // 中文
        @"(交响曲|协奏曲|奏鸣曲|前奏曲|赋格|练习曲|弦乐四重奏|安魂曲|作品\s*\d)",
    ];

    private static readonly Regex Regex = new(string.Join("|", Patterns.Select(p => $"(?:{p})")),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>歌名或专辑名里有古典作品的标志。</summary>
    public static bool Matches(string? title, string? album = null) =>
        (title is not null && Regex.IsMatch(title)) || (album is not null && Regex.IsMatch(album));
}
