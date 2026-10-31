using JeekMusic.Analysis;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;

namespace JeekMusic.App.Tests;
public sealed class AnalysisPipelineTests
{
    static Track Track(long duration = 10000) => new() { Id = 1, TrackKey = "synthetic", Title = "Synthetic", FileName = "synthetic.wav", Dir = "", ArtistDisplay = "", DurationMs = duration, StartMs = 90000, EndMs = 90000 + duration };
    static readonly ModelScores Score = new(-5, -2, .1, .1, 0);
    [Fact]
    public async Task ModelRetryReusesPcmAndIsTerminalAfterThreeAttempts()
    {
        int decodes = 0, calls = 0;
        var track = Track();
        Task<float[]> Decode(string p, long s, long e, CancellationToken ct) { decodes++; Assert.Equal(90000, s); Assert.Equal(100000, e); return Task.FromResult(new float[160000]); }
        var data = await TrackAnalyzer.Analyze("synthetic", track, default, (pcm, ct) => {
            calls++; if (calls < 3) throw new IOException(); return (Score, Enumerable.Repeat(Score, 10).ToArray());
        }, Decode);
        Assert.True(data.Complete); Assert.Equal(1, decodes); Assert.Equal(3, calls);
        Assert.False(FeaturesData.NeedsAnalysis(new("synthetic", AnalysisFeature.Signature(track), AnalysisVersions.AnalyzerVersion, data.ToJson(), false), AnalysisFeature.Signature(track)));
        data = await TrackAnalyzer.Analyze("synthetic", track, default, (pcm, ct) => throw new IOException(), Decode);
        Assert.False(data.Complete); Assert.Equal(3, data.AnalysisAttempts);
        var failed = new FeaturesRow("synthetic", "s", AnalysisVersions.AnalyzerVersion, data.ToJson(), true);
        Assert.False(FeaturesData.NeedsAnalysis(failed, "s")); Assert.True(FeaturesData.NeedsAnalysis(failed, "changed"));
        Assert.True(FeaturesData.NeedsAnalysis(failed with { AnalyzerVersion = 4 }, "s"));
        Assert.True(FeaturesData.NeedsAnalysis(failed with { Data = FeaturesData.Of(null, null).ToJson() }, "s"));
    }
    [Fact]
    public async Task CancellationDoesNotBecomeFailureAndWindowsNeverCrossSeams()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TrackAnalyzer.Analyze("synthetic", Track(), cancel.Token, (_, _) => (Score, [])));
        var lengths = new List<int>();
        var data = await TrackAnalyzer.Analyze("synthetic", Track(100000), default, (pcm, ct) => {
            lengths.Add(pcm.Length); return (Score, Enumerable.Repeat(Score, 10).ToArray());
        }, (_, _, _, _) => Task.FromResult(new float[160000]));
        Assert.Equal(new[] {160000, 160000, 160000}, lengths);
        Assert.Empty(AnalysisEvidence.Parse(data.AnalysisEvidence)!.AdditionalReasons);
    }
    [Fact]
    public async Task FailedAdditionalRoundKeepsFirstPassAndIncompleteV5CannotMasqueradeAsCurrent()
    {
        int n = 0;
        var data = await TrackAnalyzer.Analyze("synthetic", Track(100000), default, (pcm, ct) => {
            var score = ++n == 1 ? Score with { Vocal = 1 } : Score;
            return (score, Enumerable.Repeat(score, 10).ToArray());
        }, (_, start, _, _) => start < 120000 ? throw new IOException() : Task.FromResult(new float[160000]));
        var e = AnalysisEvidence.Parse(data.AnalysisEvidence)!;
        Assert.True(data.Complete);
        Assert.Contains("additional_failure_first_pass_retained", e.AdditionalReasons);
        Assert.Equal(3, e.ScoredSegments().Length);
        var incomplete = FeaturesData.Of(null, Score);
        Assert.Null(FeaturesData.FromRow(new("s", "sig", 5, incomplete.ToJson(), false)));
        Assert.NotNull(FeaturesData.FromRow(new("s", "sig", 4, incomplete.ToJson(), false)));
        Assert.Equal(e.Aggregate(), AnalysisEvidence.Parse(FeaturesData.Parse(data.ToJson())!.AnalysisEvidence)!.Aggregate());
    }

    [Fact]
    public async Task ConflictsAddAtMostTwentySecondsAndShortFragmentsAreExplicitFailures()
    {
        int n = 0;
        var data = await TrackAnalyzer.Analyze("synthetic", Track(100000), default, (pcm, ct) => {
            var score = ++n == 1 ? Score with { Vocal = 1 } : Score;
            return (score, Enumerable.Repeat(score, 10).ToArray());
        }, (_, _, _, _) => Task.FromResult(new float[160000]));
        var e = AnalysisEvidence.Parse(data.AnalysisEvidence)!;
        Assert.Equal(5, e.Segments.Length); Assert.Contains("voice_segment_conflict", e.AdditionalReasons);
        Assert.Equal(20_000, e.Segments.Where(s => s.Additional).Sum(s => s.EndMs - s.StartMs));
        data = await TrackAnalyzer.Analyze("synthetic", Track(500), default, (_, _) => throw new Exception("must not infer"),
            (_, _, _, _) => Task.FromResult(new float[8000]));
        Assert.False(data.Complete); Assert.Equal("decode:insufficient_samples", AnalysisEvidence.Parse(data.AnalysisEvidence)!.Segments[0].Error);
    }
}
