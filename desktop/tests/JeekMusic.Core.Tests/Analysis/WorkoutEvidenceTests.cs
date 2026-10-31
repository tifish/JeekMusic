using JeekMusic.Core.Analysis;

namespace JeekMusic.Core.Tests.Analysis;

public sealed class WorkoutEvidenceTests
{
    [Fact]
    public void SharedConjunctionFailuresAndSensitivityPreserveIndependentDimensions()
    {
        foreach (var item in SharedCases.Load("workout-v8.json")["cases"]!.AsArray())
        {
            var e = AnalysisEvidence.Parse(item!["evidence"]!.ToJsonString())!;
            Assert.NotNull(e); Assert.True(e.Complete);
            foreach (var sensitivity in Enum.GetValues<Sensitivity>())
                Assert.Equal(item["expected"]![sensitivity.ToString()]?.GetValue<string>(), WorkoutEvidence.Classify(e, sensitivity)?.ToString());
            var m = e.Segments[0].Mean!;
            TrackFeatures Features(AnalysisEvidence source) => new(source.Contract, null, 0, -20, 0, 0, 0, 0,
                m.Vocal, m.Classical, m.Calm, m.Energy, m.Orchestra, source.ToJson());
            var legacy = e with { Contract = 5, RhythmContract = 0, RhythmState = null,
                Segments = e.Segments.Select(s => s with { Pulse = null }).ToArray() };
            var a = TagClassifier.Classify(Features(e), Sensitivity.LOOSE);
            var b = TagClassifier.Classify(Features(legacy), Sensitivity.LOOSE);
            foreach (var dimension in new[] { TagDimension.VOICE, TagDimension.GENRE, TagDimension.CALMNESS }) Assert.Equal(b[dimension], a[dimension]);
            Assert.Equal(Tag.CLASSICAL, TagClassifier.Classify(Features(e), Sensitivity.LOOSE, true)[TagDimension.GENRE]);
        }
    }

    [Fact]
    public void ContextPlanIsBoundedAndKeepsLogicalCueBoundaries()
    {
        var e = AnalysisEvidence.Parse(SharedCases.Load("workout-v8.json")["cases"]![0]!["evidence"]!.ToJsonString())!;
        var plan = WorkoutEvidence.Plan(e, 20000, 80000);
        Assert.Equal(2, plan.Count);
        Assert.All(plan, p => { Assert.InRange(p.StartMs, 20000, 100000); Assert.InRange(p.EndMs, 20000, 100000); Assert.Equal(30000, p.EndMs-p.StartMs); });
        Assert.All(plan.SelectMany((a,i) => plan.Skip(i+1).Select(b => (a,b))), pair => Assert.False(pair.a.StartMs < pair.b.EndMs && pair.a.EndMs > pair.b.StartMs));
        var bad = e with { RhythmContract = 0 };
        Assert.Null(AnalysisEvidence.Parse(bad.ToJson()));
        Assert.Null(AnalysisEvidence.Parse((e with { Contract = 5 }).ToJson()));
    }
}
