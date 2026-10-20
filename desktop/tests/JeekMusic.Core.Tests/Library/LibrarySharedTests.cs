using System.Text;
using System.Text.Json.Nodes;
using JeekMusic.Core.Library;
using JeekMusic.Core.Tests.Text;
using JeekMusic.Core.Text;

namespace JeekMusic.Core.Tests.Library;

/// <summary>shared/testcases 里曲库相关的用例：艺术家拆分、文件名推断、专辑键、CUE、目录树、m3u、备份引用。</summary>
public class LibrarySharedTests
{
    [Fact]
    public void ArtistSplit()
    {
        var file = SharedCases.Load("artist-splitter.json");
        foreach (var c in file.Cases("split"))
        {
            var separators = c["separators"] is JsonArray s ? s.Strings().ToHashSet() : ArtistSplitConfig.DefaultSeparators.ToHashSet();
            if (c["extraSeparators"] is JsonArray extra) separators.UnionWith(extra.Strings());
            var config = new ArtistSplitConfig(separators, c["exceptions"] is JsonArray e ? e.Strings() : ArtistSplitConfig.DefaultExceptions);
            var result = ArtistSplitter.Split(c["values"].Strings(), config);
            Assert.True(c["expected"].Strings().SequenceEqual(result), $"{c.Description()}: [{string.Join(" | ", result)}]");
        }
        foreach (var c in file.Cases("nameKey"))
        {
            Assert.Equal(c.Str("expected"), ArtistSplitter.NameKey(c.Str("input")!));
        }
    }

    [Fact]
    public void FileNameInfer()
    {
        foreach (var c in SharedCases.Load("file-name-inferrer.json").Cases())
        {
            var result = FileNameInferrer.Infer(c.Str("input")!, Enum.Parse<FileNameFormat>(c.Str("format")!));
            if (c["expected"] is JsonObject e)
            {
                Assert.True(result is not null, c.Description());
                Assert.Equal(e.Str("artist"), result.Artist);
                Assert.Equal(e.Str("title"), result.Title);
            }
            else
            {
                Assert.True(result is null, $"{c.Description()}: {result}");
            }
        }
    }

    private static string? AlbumKey(JsonObject o) =>
        AlbumKeys.Of(o.Str("album"), o.Str("albumArtist"), o.Str("volume")!, o.Str("dir")!);

    [Fact]
    public void AlbumKeyCases()
    {
        var file = SharedCases.Load("album-keys.json");
        foreach (var c in file.Cases("nullKeys")) Assert.True(AlbumKey(c) is null, c.Description());
        foreach (var c in file.Cases("pairs"))
        {
            var a = AlbumKey(c["a"]!.AsObject());
            var b = AlbumKey(c["b"]!.AsObject());
            Assert.NotNull(a);
            Assert.True((a == b) == c.Flag("same"), $"{c.Description()}: {a} / {b}");
        }
        foreach (var c in file.Cases("albumDirectory"))
        {
            Assert.Equal(c.Str("expected"), AlbumKeys.AlbumDirectory(c.Str("input")!));
        }
    }

    [Fact]
    public void Cue()
    {
        var file = SharedCases.Load("cue-parser.json");
        foreach (var c in file.Cases())
        {
            var text = c.Text();
            if (c.Str("encoding") is { } encoding) text = CharsetDetector.Decode(TextSharedTests.Enc(encoding).GetBytes(text));
            var sheet = CueParser.Parse(text);
            var e = c["expected"]!.AsObject();
            var d = c.Description();
            if (e.ContainsKey("title")) Assert.True(e.Str("title") == sheet.Title, d);
            if (e.ContainsKey("performer")) Assert.True(e.Str("performer") == sheet.Performer, d);
            if (e.ContainsKey("year")) Assert.True(e["year"]?.GetValue<int>() == sheet.Year, d);
            if (e.ContainsKey("genre")) Assert.True(e.Str("genre") == sheet.Genre, d);
            if (e.ContainsKey("isPerTrack")) Assert.True(e.Flag("isPerTrack") == sheet.IsPerTrack, d);
            if (e["files"] is not JsonArray files) continue;
            Assert.True(files.Count == sheet.Files.Count, d);
            for (var i = 0; i < files.Count; i++)
            {
                var ef = files[i]!.AsObject();
                Assert.Equal(ef.Str("name"), sheet.Files[i].Name);
                var tracks = ef["tracks"]!.AsArray();
                Assert.True(tracks.Count == sheet.Files[i].Tracks.Count, d);
                for (var t = 0; t < tracks.Count; t++)
                {
                    var et = tracks[t]!.AsObject();
                    var at = sheet.Files[i].Tracks[t];
                    Assert.Equal(new CueTrack(et["number"]!.GetValue<int>(), et.Str("title"), et.Str("performer"), et["startMs"]!.GetValue<long>()), at);
                }
            }
        }
        foreach (var c in file.Cases("parseTime"))
        {
            Assert.Equal(c["expected"]?.GetValue<long>(), CueParser.ParseTime(c.Str("input")!));
        }
    }

    [Fact]
    public void Folders()
    {
        var file = SharedCases.Load("folder-tree.json");
        var counts = file["counts"]!.AsArray().Select(n => new DirectoryCount(n![0]!.GetValue<string>(), n[1]!.GetValue<int>())).ToList();
        foreach (var c in file.Cases())
        {
            var expected = c["expected"]!.AsArray()
                .Select(n => new FolderNode(n!["name"]!.GetValue<string>(), n["relativePath"]!.GetValue<string>(), n["trackCount"]!.GetValue<int>()));
            Assert.Equal(expected, FolderTree.Children(c.Str("path")!, counts));
        }
    }

    [Fact]
    public void Playlists()
    {
        var file = SharedCases.Load("m3u.json");
        foreach (var c in file.Cases("format"))
        {
            var entries = c["entries"]!.AsArray().Select(n => new M3u.Entry(n!["path"]!.GetValue<string>(), n["durationSec"]!.GetValue<long>(), n["title"]!.GetValue<string>()));
            Assert.Equal(c.Str("expected"), M3u.Format(entries));
        }
        foreach (var c in file.Cases("parse"))
        {
            var expected = c["expected"]!.AsArray().Select(n => n.Strings()).ToList();
            var actual = M3u.Parse(c.Text());
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);
        }
        foreach (var c in file.Cases("storagePath"))
        {
            Assert.Equal(c.Str("expected"), M3u.StoragePath(c.Str("volume")!, c.Str("relativePath")!, c.Str("fileName")!));
        }
    }

    [Fact]
    public void BackupReferences()
    {
        var file = SharedCases.Load("backup-refs.json");
        foreach (var c in file.Cases("refOf"))
        {
            var roots = c["roots"]!.AsArray().Select(n => new BackupRefs.Root(n![0]!.GetValue<string>(), n[1]!.GetValue<string>()));
            Assert.True(c.Str("expected") == BackupRefs.RefOf(c.Str("trackKey")!, roots), c.Description());
        }
        foreach (var c in file.Cases("match"))
        {
            var refs = c["refs"]!.AsObject().ToDictionary(e => e.Key, e => e.Value!.GetValue<string>());
            Assert.True(c.Str("expected") == new BackupRefs.Matcher(refs).Match(c.Str("ref")!), c.Description());
        }
    }
}
