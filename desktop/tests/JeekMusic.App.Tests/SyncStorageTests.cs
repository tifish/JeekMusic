using JeekMusic.Library;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

public sealed class SyncStorageTests
{
    [Fact]
    public void LanInboxNormalizesExtendedOverridesAndRejectsMalformedState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-calmness-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string state = """{"format":2,"device":{"id":"phone","name":"Phone"},"written_at":30,"tag_overrides":{"song":{"MOOD":{"value":"QUIET","updated_at":10}}},"extended_tag_overrides":{"song":{"CALMNESS":{"value":null,"updated_at":30}}}}""";
            File.WriteAllText(Path.Combine(dir, "phone.json"), state);
            File.WriteAllText(Path.Combine(dir, "bad.json"), state.Replace("\"updated_at\":30", "\"updated_at\":-1"));
            File.WriteAllText(Path.Combine(dir, "phone.features.json"), """{"format":2,"analyzer_version":5,"items":{}}""");
            var result = Assert.Single(LanSyncService.ReadIncoming(dir, false));
            Assert.Null(result["extended_tag_overrides"]);
            var tags = result["tag_overrides"]!["song"]!;
            Assert.Equal("QUIET", tags["MOOD"]!["value"]!.GetValue<string>());
            Assert.Null(tags["CALMNESS"]!["value"]);
            Assert.Equal(30, tags["CALMNESS"]!["updated_at"]!.GetValue<long>());
            Assert.Equal(5, Assert.Single(LanSyncService.ReadIncoming(dir, true))["analyzer_version"]!.GetValue<int>());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EmbeddingTagsKeepsCurrentAudioAnalysisButDoesNotReviveStaleAnalysis()
    {
        using var db = new LibraryDb(":memory:");
        var folder = db.AddFolder("volume", "\\music\\", "C:\\music\\", "Music");
        var track = new Track { TrackKey = "song", FolderId = folder, Dir = "", FileName = "song.flac", Title = "Song", ArtistDisplay = "Artist", Mtime = 123, Size = 456 };
        using (var tx = db.Begin()) { db.ReplaceFile(tx, folder, "", "song.flac", [track]); tx.Commit(); }
        db.SaveFeatures(new FeaturesRow("song", "123:456", 4, "{}", false));
        db.MarkEmbedded(folder, "", "song.flac", true, true, 789, 123);
        Assert.Equal("123:789", db.Features("song")!.Signature);
        Assert.True(Assert.Single(db.AllTracks()).HasEmbeddedLyrics);
        db.SaveFeatures(new FeaturesRow("song", "stale", 4, "{}", false));
        db.MarkEmbedded(folder, "", "song.flac", true, false, 900, 123);
        Assert.Equal("stale", db.Features("song")!.Signature);
    }

    [Fact]
    public void FailedMergeRollsBackListsOffsetsHistoryAndNavigationTogether()
    {
        using var db = new LibraryDb(":memory:");
        var id = db.CreatePlaylist("Original", "list", 10);
        db.SetPlaylistItems(id, [("old", null)], 10);
        Assert.Throws<IOException>(() => db.InTransaction(() =>
        {
            db.SetPlaylistItems(id, [("new", null)], 20, "New");
            db.SetNavItems([new NavItem("ARTIST", "Artist")], 20);
            db.SetLyricsOffset("new", 123, 20);
            db.AddSyncHistory("PLAYLIST", "list", "{}", "Phone");
            db.DeletePlaylist(id, 30);
            throw new IOException("Interrupted merge");
        }));
        Assert.Equal("Original", Assert.Single(db.Playlists()).Name);
        Assert.Equal("old", Assert.Single(db.PlaylistItems(id)).TrackKey);
        Assert.Empty(db.NavItems());
        Assert.Empty(db.LyricsOffsets());
        Assert.Empty(db.SyncHistory());
        Assert.Empty(db.Tombstones("PLAYLIST"));
        db.InTransaction(() => db.SetPlaylistItems(id, [("committed", null)], 40));
        Assert.Equal("committed", Assert.Single(db.PlaylistItems(id)).TrackKey);
    }

    [Fact]
    public void AtomicReplacementKeepsPreviousVersionAndOtherDevices()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-sync-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var own = Path.Combine(dir, "pc.json");
            var other = Path.Combine(dir, "phone.json");
            File.WriteAllText(other, "phone");
            SyncFeature.AtomicWrite(own, "first");
            SyncFeature.AtomicWrite(own, "second");
            Assert.Equal("second", File.ReadAllText(own));
            Assert.Equal("first", File.ReadAllText(own + ".prev"));
            Assert.Equal("phone", File.ReadAllText(other));
            Assert.False(File.Exists(own + ".tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
