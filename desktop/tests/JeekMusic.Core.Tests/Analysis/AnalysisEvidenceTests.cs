using JeekMusic.Core.Analysis;

namespace JeekMusic.Core.Tests.Analysis;

public sealed class AnalysisEvidenceTests
{
    [Fact]
    public void SharedAggregationAndSerialization()
    {
        foreach (var item in SharedCases.Load("analysis-v5.json")["cases"]!.AsArray())
        {
            var e = AnalysisEvidence.Parse(item!["evidence"]!.ToJsonString())!;
            Assert.True(e.Complete);
            var a = e.Aggregate()!;
            Assert.Equal(item["vocal"]!.GetValue<bool>(), a.Vocal >= TagClassifier.VocalThreshold);
            Assert.Equal(item["sport"]!.GetValue<bool>(), a.Sport >= 3.25);
            Assert.Equal(a, AnalysisEvidence.Parse(e.ToJson())!.Aggregate());
            var f = new TrackFeatures(5, null, 0, -20, 0, 0, 0, 0, -5, -5, .1, .1, 0, e.ToJson());
            Assert.Equal(Tag.CLASSICAL, TagClassifier.Classify(f, Sensitivity.NORMAL, true)[TagDimension.GENRE]);
        }
    }
    [Theory]
    [InlineData(40000)] [InlineData(45000)] [InlineData(100000)] [InlineData(300000)]
    public void SamplingIsBoundedAndDoesNotOverlap(long duration)
    {
        var initial = PcmMath.Windows(90000, duration);
        var extra = PcmMath.AdditionalWindows(90000, duration, initial);
        Assert.InRange(extra.Count, 0, 2);
        Assert.InRange(extra.Sum(w => w.EndMs - w.StartMs), 0, 20000);
        var all = initial.Concat(extra).OrderBy(w => w.StartMs).ToArray();
        Assert.All(all, w => { Assert.True(w.StartMs >= 90000); Assert.True(w.EndMs <= 90000 + duration); });
        for (int i = 1; i < all.Length; i++) Assert.True(all[i - 1].EndMs <= all[i].StartMs);
        Assert.Empty(PcmMath.AdditionalWindows(90000, 30000, PcmMath.Windows(90000, 30000)));
    }
    [Fact]
    public void LowRateAndChannelsStayWithinContract()
    {
        Assert.Equal(new float[] { 0, .5f, 1, 1 }, PcmMath.Downsample([0, 1], 8000, 16000));
        Assert.Equal(16000, PcmMath.Downsample(new float[11025], 11025, 16000).Length);
        Assert.Empty(PcmMath.Downsample([], 8000, 16000));
        Assert.Equal(new float[] { 1, 0, .25f }, PcmMath.MixToMono([2, 2, -1, 1, float.NaN, .5f], 2));
    }
}
