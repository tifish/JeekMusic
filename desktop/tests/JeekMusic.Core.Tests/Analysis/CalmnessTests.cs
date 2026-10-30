using JeekMusic.Core.Analysis;

namespace JeekMusic.Core.Tests.Analysis;

public sealed class CalmnessTests
{
    [Fact]
    public void SharedWindowBoundariesAndIndependentDimensions()
    {
        foreach (var item in SharedCases.Load("calmness-v6.json")["cases"]!.AsArray())
        {
            var e = AnalysisEvidence.Parse(item!["evidence"]!.ToJsonString())!;
            var expected = item["expected"]?.GetValue<string>();
            Assert.Equal(expected, CalmnessEvidence.From(e)?.Tag.ToString());
            var f = new TrackFeatures(5, 160, 2, -7, 0, 4, 3000, .5, -3, -1, .04, .01, 0, e.ToJson());
            foreach (var sensitivity in Enum.GetValues<Sensitivity>())
            {
                var tags = TagClassifier.Classify(f, sensitivity, true);
                Assert.Equal(expected, tags[TagDimension.CALMNESS]?.ToString());
                Assert.Equal(Tag.CLASSICAL, tags[TagDimension.GENRE]);
                Assert.Equal(tags[TagDimension.CALMNESS], TagClassifier.Classify(f with { Loudness = -60 }, sensitivity)[TagDimension.CALMNESS]);
            }
            Assert.Null(CalmnessEvidence.From(e with { FeatureComplete = false }));
            Assert.Null(TagClassifier.Classify(f with { AnalyzerVersion = 4 }, Sensitivity.NORMAL)[TagDimension.CALMNESS]);
        }
        Assert.Null(CalmnessEvidence.From(null));
        Assert.Equal(Tag.NON_SPORT, Tags.Of(TagDimension.MOOD, "QUIET"));
        Assert.Null(Tags.Of(TagDimension.CALMNESS, "QUIET"));
    }
}
