using JeekMusic.Core.Analysis;

namespace JeekMusic.Core.Tests.Analysis;

public sealed class WorkoutEvidenceTests
{
    [Theory] [InlineData(-0.000001,Tag.NON_SPORT)] [InlineData(0.000001,Tag.SPORT)]
    public void NormalLegacyWeightedBoundary(double offset,Tag expected)
    {
        // .35 loudness + .20 tempo + .05 low-frequency = .60; onset contribution zero.
        var f=new AudioFeatures(130,2,-7,0,2.5,1000,.2+(.05+offset)/.30*.4);
        foreach(var s in Enum.GetValues<Sensitivity>()) Assert.Equal(expected,LegacyMoodClassifier.Classify(f,s));
    }
    [Fact] public void OldSensitivityInputsAndSamplingAlwaysUseNormal()
    {
        foreach(var sensitivity in Enum.GetValues<Sensitivity>()) { Assert.Equal(3.25,sensitivity.Sport()); Assert.Equal(.60,sensitivity.Legacy()); }
        var m=new ModelScores(-3,-2,0,.003*(Math.Exp(3.3)-1),0);
        var evidence=new AnalysisEvidence(6,Enumerable.Range(0,2).Select(i=>new SegmentEvidence(i*40000,i*40000+10000,false,160000,1,1,m,Enumerable.Repeat(m,12).ToArray(),null)).ToArray(),[],true,null,0,1,"not_needed");
        Assert.Contains("mood_near_threshold",evidence.Reasons()); Assert.DoesNotContain("mood_segment_conflict",evidence.Reasons());
        Assert.False(WorkoutEvidence.NeedsPulse(evidence)); Assert.Empty(WorkoutEvidence.Plan(evidence,0,100000));
        foreach(var sensitivity in Enum.GetValues<Sensitivity>()) Assert.Equal(Tag.SPORT,WorkoutEvidence.Classify(evidence,sensitivity));
    }
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
        var e = AnalysisEvidence.Parse(SharedCases.Load("workout-v8.json")["cases"]!.AsArray().Last()!["evidence"]!.ToJsonString())!;
        var plan = WorkoutEvidence.Plan(e, 20000, 80000);
        Assert.Equal(2, plan.Count);
        Assert.All(plan, p => { Assert.InRange(p.StartMs, 20000, 100000); Assert.InRange(p.EndMs, 20000, 100000); Assert.Equal(30000, p.EndMs-p.StartMs); });
        Assert.All(plan.SelectMany((a,i) => plan.Skip(i+1).Select(b => (a,b))), pair => Assert.False(pair.a.StartMs < pair.b.EndMs && pair.a.EndMs > pair.b.StartMs));
        var bad = e with { RhythmContract = 0 };
        Assert.Null(AnalysisEvidence.Parse(bad.ToJson()));
        Assert.Null(AnalysisEvidence.Parse((e with { Contract = 5 }).ToJson()));
    }
}
