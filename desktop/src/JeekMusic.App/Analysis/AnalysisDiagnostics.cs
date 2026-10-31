using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;

namespace JeekMusic.Analysis;

/// <summary>Public, read-only production pipeline observer. Does not save features, audio or tags.</summary>
public static class AnalysisDiagnostics
{
    static JsonArray Numbers(IEnumerable<double> values) => new(values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    static readonly Lazy<string[]> Names = new(() =>
    {
        using var stream = typeof(AnalysisDiagnostics).Assembly.GetManifestResourceStream("JeekMusic.YamnetClassMap")!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        if (Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant() != "b03d48f9ebe23f69ea825193de7e736934086a12410f0af47babe897b78bc0d3")
            throw new InvalidDataException("class_map_hash");
        var names = System.Text.Encoding.UTF8.GetString(bytes.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(l => l.Split(',', 3)[2].Trim().Trim('"').Replace("\"\"", "\"")).ToArray();
        if (names.Length != YamnetScores.Classes) throw new InvalidDataException("class_map_contract");
        return names;
    });

    public static async Task<JsonObject> Run(string path, Track track, CancellationToken ct)
    {
        if (track.DurationMs <= 0 || track.DurationMs > 24 * 60 * 60 * 1000L)
            throw new ArgumentException("Invalid logical duration");
        var timer = Stopwatch.StartNew();
        using var model = new AudioTagger();
        var modelLoadMs = timer.ElapsedMilliseconds;
        var segments = new JsonArray();
        var raw = new List<float[]>();
        long rhythmMs = 0, observerMs = 0;
        var features = await TrackAnalyzer.Analyze(path, track, ct, (pcm, token) =>
        {
            raw.Clear(); // retries never mix partial model runs
            return model.AnalyzeObserved(pcm, token, (_, scores) => raw.Add(scores));
        }, observe: (segment, pcm) =>
        {
            ct.ThrowIfCancellationRequested();
            var begin = timer.ElapsedMilliseconds;
            var rhythm = RhythmDiagnostics.Extract(PcmMath.Downsample(pcm, 16000, FeatureExtractor.SampleRate));
            rhythmMs += timer.ElapsedMilliseconds - begin;
            var windows = new JsonArray();
            if (segment.Complete)
            {
                if (raw.Count != segment.Frames.Length) throw new InvalidDataException("observer_window_mismatch");
                for (int n = 0; n < raw.Count; n++)
                    windows.Add((JsonNode)new JsonObject {
                        ["startMs"] = segment.StartMs + n * 975, ["endMs"] = segment.StartMs + (n + 1) * 975,
                        ["energy"] = segment.Frames[n].Energy, ["calm"] = segment.Frames[n].Calm,
                        ["sportScore"] = TagClassifier.SportScore(segment.Frames[n]),
                        ["scores521"] = Numbers(raw[n].Select(x => (double)x)) });
            }
            segments.Add((JsonNode)new JsonObject {
                ["startMs"] = segment.StartMs, ["endMs"] = segment.EndMs, ["additional"] = segment.Additional,
                ["complete"] = segment.Complete, ["error"] = segment.Error,
                ["decodeAttempts"] = segment.DecodeAttempts, ["modelAttempts"] = segment.ModelAttempts,
                ["sampleCount"] = segment.SampleCount, ["productionBpm"] = rhythm.Features.Bpm,
                ["productionPeakRatio"] = rhythm.Features.BpmConfidence, ["onsetsPerSecond"] = rhythm.Features.OnsetRate,
                ["hopSeconds"] = rhythm.HopSeconds, ["envelope"] = Numbers(rhythm.Envelope),
                ["onsets"] = new JsonArray(rhythm.Onsets.Select(o => (JsonNode)new JsonObject {
                    ["seconds"] = o.Seconds, ["strength"] = o.Strength, ["intervalSeconds"] = o.IntervalSeconds }).ToArray()),
                ["tempoCandidates"] = new JsonArray(rhythm.Candidates.Select(c => (JsonNode)new JsonObject {
                    ["lag"] = c.Lag, ["bpm"] = c.Bpm, ["correlation"] = c.Correlation,
                    ["productionWeight"] = c.ProductionWeight, ["quarterCorrelations"] = Numbers(c.QuarterCorrelations) }).ToArray()),
                ["windows"] = windows });
            observerMs += timer.ElapsedMilliseconds - begin;
        });
        ct.ThrowIfCancellationRequested();
        var evidence = AnalysisEvidence.Parse(features.AnalysisEvidence)!;
        return new JsonObject {
            ["contract"] = "analysis_diagnostics_v1", ["analyzerVersion"] = 5, ["rulesVersion"] = 7,
            ["cacheCompatible"] = false, ["writesAudio"] = false, ["writesFeaturesOrTags"] = false,
            ["modelSha256"] = Hash(Path.Combine(AppContext.BaseDirectory, "Models", "yamnet.onnx")),
            ["classMapSha256"] = "b03d48f9ebe23f69ea825193de7e736934086a12410f0af47babe897b78bc0d3",
            ["classNames"] = new JsonArray(Names.Value.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["energyIds"] = Numbers(YamnetScores.EnergyIds.ToArray().Select(x => (double)x)),
            ["calmIds"] = Numbers(YamnetScores.CalmIds.ToArray().Select(x => (double)x)),
            ["logicalStartMs"] = track.StartMs ?? 0, ["durationMs"] = track.DurationMs,
            ["sampleRate"] = 16000, ["modelFrameSamples"] = YamnetScores.Frame,
            ["complete"] = features.Complete, ["productionEvidence"] = JsonNode.Parse(evidence.ToJson()),
            ["sportMedian"] = evidence.Aggregate()?.Sport, ["segments"] = segments,
            ["modelLoadMs"] = modelLoadMs, ["rhythmExtraMs"] = rhythmMs, ["observerExtraMs"] = observerMs, ["elapsedMs"] = timer.ElapsedMilliseconds,
            ["interpretation"] = "Independent intervals; no seam-crossing. 521 sigmoid responses are not exclusive probabilities. Tempo candidates include half/double alternatives; quarter correlations are fixed-lag persistence, not beat tracking. Production BPM peak ratio is not probability. No diagnostic result is imported into cache."
        };
    }
}

/// <summary>One bounded in-memory job shared by all MCP connections. Caller serializes controls.</summary>
public sealed class AnalysisDiagnosticJob : IDisposable
{
    readonly object gate = new();
    CancellationTokenSource? stop;
    JsonObject? result;
    string state = "idle";
    long? trackId;
    int budgetSeconds;
    readonly Stopwatch elapsed = new();
    string? error;

    public void Start(long id, int seconds, Func<CancellationToken, Task<JsonObject>> run)
    {
        if (seconds is < 15 or > 180) throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (gate)
        {
            if (state is "running" or "cancelling") throw new InvalidOperationException("Diagnostic already running");
            stop?.Dispose(); stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var token = stop.Token;
            trackId = id; budgetSeconds = seconds; result = null; error = null; state = "running"; elapsed.Restart();
            _ = Task.Run(async () =>
            {
                try
                {
                    var value = await run(token);
                    token.ThrowIfCancellationRequested();
                    lock (gate) { token.ThrowIfCancellationRequested(); result = value; state = "completed"; elapsed.Stop(); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { lock (gate) { state = state == "cancelling" ? "cancelled" : "budget_exhausted"; elapsed.Stop(); } }
                catch (Exception e) { lock (gate) { state = "failed"; error = e.GetType().Name; elapsed.Stop(); } }
            });
        }
    }

    public JsonObject Read(int? segmentIndex = null)
    {
        lock (gate)
        {
            var value = new JsonObject { ["state"] = state, ["trackId"] = trackId, ["budgetSeconds"] = budgetSeconds,
                ["elapsedMs"] = elapsed.ElapsedMilliseconds, ["error"] = error, ["writesAudio"] = false, ["writesFeaturesOrTags"] = false };
            if (result is not null)
            {
                if (segmentIndex is { } index)
                {
                    var segments = result["segments"]!.AsArray();
                    if (index < 0 || index >= segments.Count) throw new ArgumentOutOfRangeException(nameof(segmentIndex));
                    value["segment"] = segments[index]!.DeepClone();
                }
                else
                {
                    var summary = new JsonObject();
                    foreach (var property in result.Where(p => p.Key != "segments"))
                        summary[property.Key] = property.Value?.DeepClone();
                    value["summary"] = summary;
                    value["summary"]!["segmentCount"] = result["segments"]!.AsArray().Count;
                }
            }
            return value;
        }
    }
    public void Cancel() { lock (gate) if (state == "running") { state = "cancelling"; stop!.Cancel(); } }
    public void Dispose() => Cancel();
}
