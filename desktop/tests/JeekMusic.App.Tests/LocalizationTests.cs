using System.Text;
using System.Text.RegularExpressions;

namespace JeekMusic.App.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void TranslationsPreserveAllFormatArguments()
    {
        foreach (var (key, translated) in L.EnglishResources)
        {
            Assert.False(string.IsNullOrWhiteSpace(translated), key);
            var original = CompositeFormat.Parse(key);
            var english = CompositeFormat.Parse(translated);
            Assert.Equal(original.MinimumArgumentCount, english.MinimumArgumentCount);
            static IEnumerable<string> Arguments(string text) => Regex.Matches(text, @"\{(\d+)(?:[:},])")
                .Select(m => m.Groups[1].Value).Distinct().Order();
            Assert.Equal(Arguments(key), Arguments(translated));
        }
    }
}
