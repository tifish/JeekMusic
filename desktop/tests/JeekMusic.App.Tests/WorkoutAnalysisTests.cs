using System.Text.Json.Nodes;
using JeekMusic.Analysis;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;

namespace JeekMusic.App.Tests;

public sealed class WorkoutAnalysisTests
{
    static (FeaturesData Data, Track Track) Candidate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testcases", "workout-v8.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!["cases"]![0]!["evidence"]!.ToJsonString();
        var e = AnalysisEvidence.Parse(json)!;
        e = e with { Contract = 5, RhythmContract = 0, RhythmState = null, Segments = e.Segments.Select(s => s with { Pulse = null }).ToArray() };
        return (FeaturesData.Of(null, e.Segments[0].Mean) with { AnalysisEvidence = e.ToJson() },
            new Track { Id = 1, TrackKey = "candidate", Dir = "", FileName = "fixture.wav", Title = "", ArtistDisplay = "", DurationMs = 160000, Mtime = 42, Size = 1 });
    }

    [Fact]
    public async Task NewFeatureCannotBeInventedFromOldCacheAndFailureKeepsBaseWithNoWorkoutResult()
    {
        var (data, track) = Candidate();
        var row = new FeaturesRow(track.TrackKey, AnalysisFeature.Signature(track), 5, data.ToJson(), false);
        Assert.Null(WorkoutAnalysis.UpgradeWithoutAudio(row, row.Signature));
        int decodes = 0;
        var result = await WorkoutAnalysis.Complete("private-path-never-in-evidence", track, data, default, (_, _, _, _) => {
            decodes++; throw new IOException("private file name must not leak");
        }, reuseBase: true);
        Assert.Equal(6, decodes); Assert.True(result.Complete);
        var evidence = AnalysisEvidence.Parse(result.AnalysisEvidence)!;
        Assert.Equal("failed", evidence.RhythmState); Assert.True(evidence.BaseEvidenceReused);
        Assert.DoesNotContain("private", result.AnalysisEvidence);
        Assert.All(evidence.Segments, s => { Assert.Equal(3, s.Pulse!.Attempts); Assert.Equal("IOException", s.Pulse.Error); });
        var tags = TagClassifier.Classify(result.ToFeatures(6), Sensitivity.LOOSE);
        Assert.Null(tags[TagDimension.MOOD]); Assert.Equal(Tag.VOCAL, tags[TagDimension.VOICE]);
        Assert.NotNull(tags[TagDimension.CALMNESS]);
    }

    [Fact]
    public async Task CancellationDoesNotProduceACommitCandidate()
    {
        var (data, track) = Candidate();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkoutAnalysis.Complete("fixture",track,data,cancel.Token,
            (_,_,_,ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(new float[480000]); }));
    }

    [Fact]
    public void NonCandidatePromotionPreservesScoresAndSignatureWithoutNewPulse()
    {
        var (data, track) = Candidate();
        var e = AnalysisEvidence.Parse(data.AnalysisEvidence)!;
        var quiet = new ModelScores(0,-2,.02,.01,0);
        e = e with { Segments = e.Segments.Select(s => s with { Mean = quiet, Frames = Enumerable.Repeat(quiet,10).ToArray() }).ToArray() };
        data = FeaturesData.Of(null, quiet) with { AnalysisEvidence = e.ToJson() };
        var row = new FeaturesRow(track.TrackKey,"sig",5,data.ToJson(),false);
        Assert.Null(WorkoutAnalysis.UpgradeWithoutAudio(row,"stale"));
        var upgraded = WorkoutAnalysis.UpgradeWithoutAudio(row,"sig")!;
        Assert.Equal(data with { AnalysisEvidence = upgraded.AnalysisEvidence },upgraded);
        var fresh = AnalysisEvidence.Parse(upgraded.AnalysisEvidence)!;
        Assert.Equal(6,fresh.Contract); Assert.True(fresh.BaseEvidenceReused);
        Assert.All(fresh.Segments,s => Assert.Null(s.Pulse));
    }

    [Fact]
    public async Task ExplicitBatchRejectsDuplicatesReentryAndRetainsOnlyCompletedTracksOnCancel()
    {
        using var job = new AnalysisBatchJob();
        Assert.Throws<ArgumentException>(() => job.Start([1,1],15,(_,_) => Task.FromResult(new JsonObject())));
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        job.Start([1,2],15,async (id,ct) => {
            if (id == 2) { enteredSecond.SetResult(); await Task.Delay(Timeout.Infinite,ct); }
            return new JsonObject { ["trackId"] = id,["state"] = "committed" };
        });
        await enteredSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => job.Start([3],15,(_,_) => Task.FromResult(new JsonObject())));
        job.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (job.Read()["state"]!.GetValue<string>() == "cancelling") await Task.Delay(10,timeout.Token);
        Assert.Equal("cancelled",job.Read()["state"]!.GetValue<string>());
        Assert.Equal(1,job.Read()["processed"]!.GetValue<int>());
        var read = job.Read(); read["results"]![0]!["state"] = "tampered";
        Assert.Equal("committed",job.Read()["results"]![0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExplicitBatchDeadlineCannotBeReportedAsCompletedAfterLateBoundary()
    {
        using var job = new AnalysisBatchJob();
        job.Start([1],15,async (_,_) => { await Task.Delay(15100); return new JsonObject { ["state"] = "synthetic_no_write" }; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (job.Read()["state"]!.GetValue<string>() == "running") await Task.Delay(50,timeout.Token);
        Assert.Equal("budget_exhausted",job.Read()["state"]!.GetValue<string>());
    }
}
