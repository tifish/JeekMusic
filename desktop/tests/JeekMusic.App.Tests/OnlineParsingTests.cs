using JeekMusic.Core.Lyrics;
using JeekMusic.Online;

namespace JeekMusic.App.Tests;

public sealed class OnlineParsingTests
{
    [Fact]
    public void NetEasePreservesTranslationsAndNormalizesCoverScheme()
    {
        var song = Assert.Single(NetEaseMusic.ParseSearch("""
            {"code":200,"result":{"songs":[{"id":12,"name":"Signal","dt":123000,
            "ar":[{"name":"Artist"}],"al":{"name":"Album","picUrl":"http://example.test/art.jpg"}}]}}
            """));
        Assert.Equal("12", song.Id);
        Assert.Equal(123000, song.DurationMs);
        Assert.StartsWith("https://", song.ImageUrl);
        var text = NetEaseMusic.ParseLyrics("""{"lrc":{"lyric":"[00:01.00]Original"},"tlyric":{"lyric":"[00:01.00]Translation"}}""");
        Assert.Contains("Original", text);
        Assert.Contains("Translation", text);
    }

    [Fact]
    public void QqDecodesEntitiesAndPreservesAstralUnicode()
    {
        Assert.Equal("A & B ' 🎵", QqMusic.Unescape("A &amp; B &#39; &#x1F3B5;"));
        Assert.Equal("&#99999999;", QqMusic.Unescape("&#99999999;"));
        Assert.Equal("[00:01.00]A & B", QqMusic.ParseLyrics("""{"lyric":"[00:01.00]A &amp; B"}"""));
    }

    [Fact]
    public void InstrumentalPlaceholderIsProviderDataIndependentOfUiLanguage()
    {
        Assert.True(OnlineLyricsClient.IsInstrumentalPlaceholder(LrcParser.Parse("[00:00.00]此歌曲为没有填词的纯音乐，请您欣赏")));
        Assert.False(OnlineLyricsClient.IsInstrumentalPlaceholder(LrcParser.Parse("[00:00.00]A normal lyric")));
    }
}
