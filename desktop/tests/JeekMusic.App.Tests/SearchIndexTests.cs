using JeekMusic.Library;

namespace JeekMusic.App.Tests;

public sealed class SearchIndexTests
{
    [Fact]
    public void MatchesOriginalFieldsAndChineseInitialsWithoutDuplicatingSearchText()
    {
        var folder = new LibraryFolder { Id = 1, VolumeId = "volume", PathInVolume = "\\music\\", Root = "C:\\music\\", DisplayName = "Music" };
        var track = new Track { Id = 1, FolderId = 1, TrackKey = "song", Dir = "", FileName = "01-SIGNAL.flac", Title = "夜曲", ArtistDisplay = "周杰伦", Album = "November", Artists = ["周杰伦"] };
        var index = SearchIndex.For(new LibrarySnapshot([folder], [track], new Dictionary<string, SongTags>()));
        foreach (var query in new[] { "夜曲", "周杰", "november", "signal", "zjl", "yq" }) Assert.True(index.Matches(track, query), query);
        Assert.False(index.Matches(track, "unrelated"));
    }
}
