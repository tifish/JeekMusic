using System.Text.Json.Nodes;
using JeekMusic.Analysis;
using JeekMusic.Automation;
using JeekMusic.Core.Analysis;

namespace JeekMusic.App.Tests;

public sealed class AnalysisDiagnosticTests
{
    static float[] Pulses(double bpm, bool alternate = false)
    {
        var pcm = new float[FeatureExtractor.SampleRate * 10];
        var gap = FeatureExtractor.SampleRate * 60 / bpm;
        for (int n = 0; n * gap + 250 < pcm.Length; n++)
            for (int k = 0; k < 250; k++)
                pcm[(int)(n * gap) + k] = (float)(Math.Sin(k * 1.7) * Math.Exp(-k / 40.0) * (alternate && n % 2 == 1 ? .4 : 1));
        return pcm;
    }

    [Fact]
    public void ReusesExactProductionEnvelopeAndExposesHalfDoubleAlternatives()
    {
        var pcm = Pulses(160, true);
        double[]? observed = null;
        var features = FeatureExtractor.Extract(pcm, observeEnvelope: e => observed = e);
        var d = RhythmDiagnostics.Extract(pcm);
        Assert.Equal(features, d.Features); Assert.Equal(observed!, d.Envelope);
        Assert.All(d.Candidates, c => Assert.InRange(c.Bpm, 40, 240));
        var fast = d.Candidates.MinBy(c => Math.Abs(c.Bpm - 160))!;
        var half = d.Candidates.MinBy(c => Math.Abs(c.Bpm - 80))!;
        Assert.True(fast.Correlation > .3); Assert.True(half.Correlation > .3);
        Assert.All(half.QuarterCorrelations, c => Assert.True(c > .3));
        Assert.InRange(d.Onsets.Length, 20, 30);
        Assert.True(d.Onsets.Max(o => o.Strength) > d.Onsets.Min(o => o.Strength) * 1.2);
    }

    [Fact]
    public void SilenceNoiseShortAndBadInputDoNotBecomeBeatTruth()
    {
        var silence = RhythmDiagnostics.Extract(new float[FeatureExtractor.SampleRate * 10]);
        Assert.Empty(silence.Onsets); Assert.Null(silence.Features.Bpm);
        Assert.All(silence.Candidates, c => Assert.Equal(0, c.Correlation));
        Assert.Empty(RhythmDiagnostics.Extract([]).Candidates);
        Assert.Throws<ArgumentException>(() => RhythmDiagnostics.Extract([float.NaN]));
        Assert.Throws<ArgumentException>(() => RhythmDiagnostics.Extract(new float[FeatureExtractor.SampleRate * 61]));
        var random = new Random(731);
        var noise = RhythmDiagnostics.Extract(Enumerable.Range(0, FeatureExtractor.SampleRate * 10).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray());
        Assert.True(noise.Candidates.Max(c => c.Correlation) < .4);
    }

    [Fact]
    public void ShortActivityCannotMasqueradeAsFullIntervalPersistence()
    {
        var sustained = RhythmDiagnostics.Extract(Pulses(120));
        var shortPcm = Pulses(120); Array.Clear(shortPcm, FeatureExtractor.SampleRate * 3, shortPcm.Length - FeatureExtractor.SampleRate * 3);
        var brief = RhythmDiagnostics.Extract(shortPcm);
        var steady = sustained.Candidates.MinBy(c => Math.Abs(c.Bpm - 120))!;
        var shortCandidate = brief.Candidates.Single(c => c.Lag == steady.Lag);
        Assert.All(steady.QuarterCorrelations, c => Assert.True(c > .4));
        Assert.Equal(0, shortCandidate.QuarterCorrelations[3]);
    }

    [Fact]
    public void RawObserverUsesActualSameWindowScoresWithoutChangingAggregation()
    {
        using var model = new AudioTagger();
        var raw = new List<float[]>();
        var pcm = new float[YamnetScores.Frame * 2];
        var a = model.AnalyzeObserved(pcm, default, (_, s) => raw.Add(s));
        var b = model.AnalyzeWindows(pcm);
        Assert.Equal(b.Mean, a.Mean); Assert.Equal(b.Frames, a.Frames);
        Assert.Equal(2, raw.Count);
        for (int n = 0; n < 2; n++) { Assert.Equal(521, raw[n].Length); Assert.Equal(a.Frames[n], YamnetScores.Scores(raw[n])); }
    }

    [Fact]
    public async Task JobRejectsReentryCancelsDiscardsAndCanRetryWithoutStateLeak()
    {
        using var job = new AnalysisDiagnosticJob();
        Assert.Throws<ArgumentOutOfRangeException>(() => job.Start(1, 181, _ => Task.FromResult(new JsonObject())));
        job.Start(1, 15, async token => { await Task.Delay(Timeout.Infinite, token); return new(); });
        Assert.Throws<InvalidOperationException>(() => job.Start(2, 15, _ => Task.FromResult(new JsonObject())));
        job.Cancel();
        await Until(() => job.Read()["state"]!.GetValue<string>() == "cancelled");
        Assert.Null(job.Read()["summary"]);
        job.Start(2, 15, _ => throw new InvalidDataException("private/path must not be reported"));
        await Until(() => job.Read()["state"]!.GetValue<string>() == "failed");
        Assert.Equal("InvalidDataException", job.Read()["error"]!.GetValue<string>());
        job.Start(3, 15, _ => Task.FromResult(new JsonObject { ["segments"] = new JsonArray((JsonNode)new JsonObject { ["test"] = 1 }) }));
        await Until(() => job.Read()["state"]!.GetValue<string>() == "completed");
        Assert.Equal(1, job.Read()["summary"]!["segmentCount"]!.GetValue<int>());
        Assert.Null(job.Read()["summary"]!["segments"]);
        var read = job.Read(0); read["segment"]!["test"] = 2;
        Assert.Equal(1, job.Read(0)["segment"]!["test"]!.GetValue<int>());
        Assert.Throws<ArgumentOutOfRangeException>(() => job.Read(1));
    }

    static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task DeadlineDiscardsUncooperativeLateResultAndReleasesSlot()
    {
        using var job = new AnalysisDiagnosticJob();
        // Model/decode boundary may finish after the deadline. Never publish it.
        job.Start(1, 15, async _ => { await Task.Delay(15_100); return new JsonObject(); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (job.Read()["state"]!.GetValue<string>() == "running") await Task.Delay(50, timeout.Token);
        Assert.Equal("budget_exhausted", job.Read()["state"]!.GetValue<string>());
        Assert.Null(job.Read()["summary"]);
        job.Start(2, 15, _ => throw new IOException());
        await Until(() => job.Read()["state"]!.GetValue<string>() == "failed");
    }

    [Fact]
    public void McpContractBoundsExplicitScopeAndHasNoDevelopmentSwitch()
    {
        var tool = McpTools.BuildCatalog().Single(x => x!["name"]!.GetValue<string>() == "diagnose_analysis")!;
        Assert.Contains("no library scan", tool["description"]!.GetValue<string>());
        var schema = tool["inputSchema"]!.AsObject();
        McpSession.Validate(JsonNode.Parse("""{"action":"start","trackId":1,"budgetSeconds":15}""")!.AsObject(), schema);
        foreach (var input in new[] { """{"action":"start","trackIds":[1]}""", """{"action":"start","trackId":1,"budgetSeconds":181}""", """{"action":"result","segmentIndex":5}""" })
            Assert.Throws<ArgumentException>(() => McpSession.Validate(JsonNode.Parse(input)!.AsObject(), schema));
    }
}
