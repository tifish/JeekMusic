using System.Globalization;
using System.Text.Json;

namespace JeekMusic;

/// <summary>Chinese source text is the resource key. Language changes apply on restart.</summary>
public static class L
{
    static readonly Lazy<IReadOnlyDictionary<string, string>> English = new(() =>
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("JeekMusic.Strings.en.json")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    });
    public static bool IsEnglish { get; private set; }
    public static void Initialize(string language) => IsEnglish = language == "en" ||
        language == "auto" && !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public static string T(string text) => IsEnglish ? English.Value.GetValueOrDefault(text, text) : text;
    public static string F(FormattableString text) => string.Format(CultureInfo.CurrentCulture, T(text.Format), text.GetArguments());
    internal static IReadOnlyDictionary<string, string> EnglishResources => English.Value;
}
