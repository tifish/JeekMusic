using JeekMusic.Core.Analysis;

namespace JeekMusic.Core.Tests.Analysis;

public sealed class CalmnessTests
{
    [Fact]
    public void SharedWindowBoundariesAndIndependentDimensions()
    {
        foreach (var file in new[] { "calmness-v6.json", "calmness-v7.json" })
        foreach (var item in SharedCases.Load(file)["cases"]!.AsArray())
        {
            var e = AnalysisEvidence.Parse(item!["evidence"]!.ToJsonString())!;
            var expected = item["expected"]?.GetValue<string>();
            var calm = CalmnessEvidence.From(e);
            Assert.True(expected == calm?.Tag.ToString(), item["name"]!.ToString());
            if (item["expected_support"] is { } support) Assert.Equal(support.GetValue<double>(), calm!.Support, 12);
            if (item["expected_minimum_segment_support"] is { } minimum) Assert.Equal(minimum.GetValue<double>(), calm!.MinimumSegmentSupport, 12);
            if (item["expected_strict_support"] is { } strict) Assert.Equal(strict.GetValue<double>(), calm!.StrictSupport, 12);
            if (calm is not null)
            {
                Assert.Equal(calm.Windows, calm.Segments.Sum(s => s.Windows));
                Assert.InRange(calm.Support, Math.Max(calm.StrictSupport, calm.RelativeSupport), 1);
            }
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

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.01)]
    public void InvalidWindowCannotSupplyEvidence(double value)
    {
        var f = new ModelScores(-3, -1, value, .01, 0);
        var s = new SegmentEvidence(0, 3000, false, 48000, 1, 1, f, [f, f, f], null);
        Assert.Null(CalmnessEvidence.From(new AnalysisEvidence(5, [s], [], true, null, 0)));
        f = f with { Calm = .04, Energy = value };
        Assert.Null(CalmnessEvidence.From(new AnalysisEvidence(5, [s with { Frames = [f, f, f] }], [], true, null, 0)));
    }
}
