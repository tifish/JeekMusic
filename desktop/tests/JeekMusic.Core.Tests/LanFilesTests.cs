using JeekMusic.Core.Library;
using JeekMusic.Core.Sync;

namespace JeekMusic.Core.Tests;

public sealed class LanFilesTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "jeek-lan-tests-" + Guid.NewGuid().ToString("N"));
    public LanFilesTests() => Directory.CreateDirectory(_root);
    void Put(string path, string text) { var full = Path.Combine(_root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text); }
    [Theory]
    [InlineData("../secret.wav")][InlineData("/absolute")][InlineData("C:/Music")][InlineData("a\\b")]
    [InlineData("a/../b")][InlineData("a//b")][InlineData("CON.wav")][InlineData("a./x")][InlineData("x /.cue")][InlineData(".jeekmusic/state.json")]
    public void RejectsNonportableOrEscapingPaths(string path) => Assert.Throws<IOException>(() => LanFiles.Relative(path));
    [Fact]
    public async Task SelectionNeverEnumeratesOrTransfersUnselectedLibrary()
    {
        Put("Karajan/Album/song.wav", "audio"); Put("Celtic/song.flac", "other"); Put("Private/song.wav", "never"); Put("Karajan/Album/notes.txt", "not music");
        var files = await LanFiles.Manifest(_root, ["Karajan", "Celtic"]);
        Assert.Equal(["Celtic/song.flac", "Karajan/Album/song.wav"], files.Select(f => f.Path));
        Assert.All(files, f => Assert.Equal(64, f.Hash.Length));
    }
    [Fact]
    public async Task HashDetectsSameSizeSameTimeAndTimestampHasIndependentVersion()
    {
        Put("one.wav", "aaaa"); var full = Path.Combine(_root, "one.wav");
        var initial = Assert.Single(await LanFiles.Manifest(_root, [""])); var time = File.GetLastWriteTimeUtc(full);
        File.WriteAllText(full, "bbbb"); File.SetLastWriteTimeUtc(full, time);
        var content = Assert.Single(await LanFiles.Manifest(_root, [""]));
        Assert.Equal(initial.ModifiedNs, content.ModifiedNs); Assert.Equal(initial.Size, content.Size); Assert.NotEqual(initial.Hash, content.Hash);
        File.SetLastWriteTimeUtc(full, time.AddHours(-1)); var timestamp = Assert.Single(await LanFiles.Manifest(_root, [""]));
        Assert.Equal(content.Hash, timestamp.Hash); Assert.NotEqual(content.ModifiedNs, timestamp.ModifiedNs);
    }
    [Fact]
    public async Task CueDependenciesAndSidecarsAreIncludedWithoutWholeLibrary()
    {
        Put("Chosen/album.cue", "FILE \"../Shared/disc.wav\" WAVE\nTRACK 01 AUDIO\n INDEX 01 00:00:00\n");
        Put("Shared/disc.wav", "audio"); Put("Shared/cover.jpg", "cover"); Put("Other/unselected.wav", "never");
        var files = await LanFiles.Manifest(_root, ["Chosen"]);
        Assert.Contains(files, f => f.Path == "Shared/disc.wav"); Assert.Contains(files, f => f.Path == "Shared/cover.jpg"); Assert.DoesNotContain(files, f => f.Path.StartsWith("Other/"));
        Assert.Contains("Shared/disc.wav", files.Single(f => f.Path.EndsWith(".cue")).Dependencies!);
    }
    [Theory]
    [InlineData("../outside.wav")][InlineData("C:\\outside.wav")][InlineData("/outside.wav")][InlineData("..\\outside.wav")]
    public async Task CueCannotEscapeRoot(string dependency)
    {
        Put("album.cue", $"FILE \"{dependency}\" WAVE\n");
        await Assert.ThrowsAsync<IOException>(() => LanFiles.Manifest(_root, [""]));
    }
    [Fact]
    public void RejectsCaseAndUnicodeCollisions()
    {
        Assert.Throws<IOException>(() => LanFiles.CheckCollisions(["A.wav", "a.wav"]));
        Assert.Throws<IOException>(() => LanFiles.CheckCollisions(["é.wav", "e\u0301.wav"]));
    }
    [Fact]
    public void RootReferencesNeverUseSuffixFallbackInEitherDirection()
    {
        var matcher = new BackupRefs.Matcher(new Dictionary<string, string> { ["root:aaa/A/song.wav"] = "managed", ["Other/song.wav"] = "ordinary" });
        Assert.Null(matcher.Match("root:bbb/A/song.wav"));
        Assert.Equal("managed", matcher.Match("root:aaa/A/song.wav"));
        Assert.Equal("ordinary", matcher.Match("song.wav"));
        Assert.Null(new BackupRefs.Matcher(new Dictionary<string, string> { ["root:aaa/A/song.wav"] = "managed" }).Match("song.wav"));
    }
    [Fact]
    public async Task JunctionIsNotFollowedOrAcceptedAsRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Path.Combine(_root, "Real"); Directory.CreateDirectory(target); Put("Real/song.wav", "audio");
        var link = Path.Combine(_root, "Link");
        // A junction does not need administrator/developer-mode privileges on Windows.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { Arguments = $"/c mklink /J \"{link}\" \"{target}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        process!.WaitForExit(); Assert.Equal(0, process.ExitCode);
        Assert.Throws<IOException>(() => LanFiles.Resolve(_root, "Link/song.wav"));
        Assert.Throws<IOException>(() => LanFiles.Resolve(link, "", true));
        var files = await LanFiles.Manifest(_root, [""]); Assert.Single(files);
        Directory.Delete(link);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
