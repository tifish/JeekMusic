using System.Text.Json.Nodes;
using JeekMusic.Core.Analysis;
using JeekMusic.Core.Lyrics;
using JeekMusic.Core.Online;

namespace JeekMusic.Core.Tests.Analysis;

/// <summary>shared/testcases 里歌词、智能标签、在线匹配的用例。</summary>
public class AnalysisSharedTests
{
    [Fact]
    public void Lrc()
    {
        var file = SharedCases.Load("lrc-parser.json");
        foreach (var c in file.Cases())
        {
            var lyrics = LrcParser.Parse(c.Text());
            var e = c["expected"]!.AsObject();
            Assert.True(e.Flag("synced") == lyrics.Synced, c.Description());
            var expected = e["lines"]!.AsArray()
                .Select(n => new LyricLine(n!["timeMs"]!.GetValue<long>(), n["text"]!.GetValue<string>(), n.AsObject().Str("translation")))
                .ToList();
            Assert.Equal(expected, lyrics.Lines);
        }
        foreach (var c in file.Cases("currentIndex"))
        {
            var lines = c["lines"]!.AsArray().Select(n => new LyricLine(n!.GetValue<long>(), "")).ToList();
            Assert.True(c["expected"]!.GetValue<int>() == LrcParser.CurrentIndex(lines, c["positionMs"]!.GetValue<long>()), c.Description());
        }
    }

    [Fact]
    public void Cover()
    {
        var file = SharedCases.Load("cover-matcher.json");
        var map = file["fold"]!.AsObject().ToDictionary(e => e.Key[0], e => e.Value!.GetValue<string>()[0]);
        var matcher = new CoverMatcher(text => new string(text.Select(ch => map.GetValueOrDefault(ch, ch)).ToArray()));
        foreach (var c in file.Cases("titleMatches"))
        {
            Assert.True(c.Flag("result") == matcher.TitleMatches(c.Str("expected")!, c.Str("actual")!), c.Description());
        }
        foreach (var c in file.Cases("artistMatches"))
        {
            Assert.True(c.Flag("result") == matcher.ArtistMatches(c.Str("expected")!, c.Str("actual")!), c.Description());
        }
        foreach (var c in file.Cases("primaryArtist"))
        {
            Assert.Equal(c.Str("expected"), CoverMatcher.PrimaryArtist(c.Str("input")!));
        }
        foreach (var c in file.Cases("hasCjk"))
        {
            Assert.True(c.Flag("expected") == CoverMatcher.HasCjk(c.Str("input")!), c.Description());
        }
    }

    [Fact]
    public void ClassicalTitle()
    {
        var file = SharedCases.Load("classical-titles.json");
        foreach (var c in file.Cases("matches")) Assert.True(ClassicalTitles.Matches(c.Str("title"), c.Str("album")), c.Str("title"));
        foreach (var c in file.Cases("notMatches")) Assert.False(ClassicalTitles.Matches(c.Str("title"), c.Str("album")), c.Str("title"));
    }

    private static double? D(JsonObject o, string key) => o[key] is JsonValue v ? v.GetValue<double>() : null;

    [Fact]
    public void Classifier()
    {
        var file = SharedCases.Load("tag-classifier.json");
        var scores = file["scores"]!.AsObject().ToDictionary(e => e.Key, e =>
        {
            var o = e.Value!.AsObject();
            return new ModelScores(D(o, "vocal")!.Value, D(o, "classical")!.Value, D(o, "calm")!.Value, D(o, "energy")!.Value, D(o, "orchestra")!.Value);
        });
        foreach (var c in file.Cases("mood"))
        {
            Assert.True(Enum.Parse<Tag>(c.Str("expected")!) == TagClassifier.Mood(scores[c.Str("scores")!], Enum.Parse<Sensitivity>(c.Str("sensitivity")!)), c.Description());
        }
        foreach (var c in file.Cases("voice"))
        {
            Assert.True(Enum.Parse<Tag>(c.Str("expected")!) == TagClassifier.Voice(scores[c.Str("scores")!]), c.Description());
        }
        foreach (var c in file.Cases("genre"))
        {
            Assert.True(Enum.Parse<Tag>(c.Str("expected")!) == TagClassifier.Genre(scores[c.Str("scores")!]), c.Description());
        }
        foreach (var c in file.Cases("classify"))
        {
            var f = c["features"]!.AsObject();
            var features = new TrackFeatures(
                f["analyzerVersion"]!.GetValue<int>(), D(f, "bpm"), D(f, "bpmConfidence")!.Value, D(f, "loudness")!.Value, D(f, "loudnessRange")!.Value,
                D(f, "onsetRate")!.Value, D(f, "centroidHz")!.Value, D(f, "lowRatio")!.Value,
                D(f, "vocalScore"), D(f, "classicalScore"), D(f, "calmLevel"), D(f, "energyLevel"), D(f, "orchestraLevel"));
            var tags = TagClassifier.Classify(features, Enum.Parse<Sensitivity>(c.Str("sensitivity")!), c.Flag("classicalTitle"));
            foreach (var (dimension, value) in c["expected"]!.AsObject())
            {
                var expected = value is null ? (Tag?)null : Enum.Parse<Tag>(value.GetValue<string>());
                Assert.True(expected == tags.GetValueOrDefault(Enum.Parse<TagDimension>(dimension)), $"{c.Description()} {dimension}");
            }
        }
        foreach (var c in file.Cases("tagOf"))
        {
            var expected = c.Str("expected") is { } e ? Enum.Parse<Tag>(e) : (Tag?)null;
            Assert.True(expected == Tags.Of(Enum.Parse<TagDimension>(c.Str("dimension")!), c.Str("value")), c.Description());
        }
        foreach (var c in file.Cases("tagsOfDimension"))
        {
            Assert.Equal(c["expected"].Strings(), Tags.Of(Enum.Parse<TagDimension>(c.Str("dimension")!)).Select(t => t.StorageName()));
        }
    }
}
