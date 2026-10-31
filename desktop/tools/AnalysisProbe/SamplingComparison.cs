using System.Diagnostics;
using System.Text.Json.Nodes;
using JeekMusic.Analysis;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;

// Read-only diagnostic, deliberately not a cache producer or a production sampling switch.
// Uses the production decoder/model/feature extractor and the actual v7 classifier.
internal static class SamplingComparison
{
    public static async Task<JsonObject> Run(string path, Track track, CancellationToken ct)
    {
        if (track.DurationMs < 100_000) throw new ArgumentException("Comparison requires at least 100 seconds to avoid overlapping positions");
        var load = Stopwatch.StartNew();
        using var model = new AudioTagger();
        load.Stop();
        var timer = Stopwatch.StartNew();
        var baseline = await TrackAnalyzer.Analyze(path, track, ct, model.AnalyzeWindows);
        var baselineMs = timer.Elapsed.TotalMilliseconds;
        var original = AnalysisEvidence.Parse(baseline.AnalysisEvidence);
        if (!baseline.Complete || original is null) throw new InvalidDataException("Incomplete baseline");
        var plans = new JsonArray {
            Describe("production_initial_3x10", original with { Segments = original.Segments.Where(s => !s.Additional).ToArray() }, null),
            Describe("production_with_bounded_additional", original, baselineMs)
        };
        foreach (var (name, positions, length) in new[] {
            ("continuous_3x20", new[] { .3, .5, .7 }, 20_000L),
            ("distributed_6x10", new[] { .1, .25, .4, .55, .7, .85 }, 10_000L) })
        {
            timer.Restart();
            var segments = new List<SegmentEvidence>();
            float[] longest = [];
            foreach (var position in positions)
            {
                var start = (track.StartMs ?? 0) + (long)(track.DurationMs * position);
                // A <40s logical track makes the existing public decoder read exactly one interval.
                var window = new Track { TrackKey = "probe", Title = "", ArtistDisplay = "", FileName = "", Dir = "",
                    StartMs = start, EndMs = start + length, DurationMs = length };
                var pcm = await PcmDecoder.Decode(path, window, ct);
                if (pcm.Length < length * 16 - 1600) throw new InvalidDataException("Truncated diagnostic interval");
                var (mean, frames) = model.AnalyzeWindows(pcm, ct);
                if (mean is null || frames.Length == 0) throw new InvalidDataException("Empty diagnostic inference");
                segments.Add(new(start, start + length, false, pcm.Length, 1, 1, mean, frames, null));
                if (pcm.Length > longest.Length) longest = pcm;
            }
            _ = FeatureExtractor.Extract(PcmMath.Downsample(longest, 16000, FeatureExtractor.SampleRate));
            var evidence = new AnalysisEvidence(5, segments.ToArray(), [], true, null, (long)timer.Elapsed.TotalMilliseconds);
            plans.Add(Describe(name, evidence, timer.Elapsed.TotalMilliseconds));
        }
        return new JsonObject {
            ["provenance"] = "desktop_production_v5_decoder_onnx_diagnostic_plans",
            ["cacheCompatible"] = false, ["modelWindowSeconds"] = YamnetScores.Frame / 16000.0,
            ["modelLoadMs"] = load.Elapsed.TotalMilliseconds,
            ["modelSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Models", "yamnet.onnx")))).ToLowerInvariant(),
            ["timingNote"] = "Single warm-model run per plan, includes decode/inference/features, excludes process startup; initial is a reused subset",
            ["plans"] = plans
        };
    }

    private static JsonObject Describe(string name, AnalysisEvidence e, double? elapsedMs)
    {
        var calm = CalmnessEvidence.From(e) ?? throw new InvalidDataException("No usable calmness evidence");
        var segments = e.ScoredSegments();
        var strictMinimum = calm.Segments.Min(s => s.StrictSupport);
        var oldTag = calm.StrictSupport >= CalmnessEvidence.SupportThreshold && strictMinimum >= CalmnessEvidence.SegmentSupportThreshold &&
            calm.EnergeticShare <= CalmnessEvidence.EnergeticShareCeiling ? "CALM" : "NON_CALM";
        return new JsonObject {
            ["plan"] = name, ["sampledSeconds"] = segments.Sum(s => s.EndMs - s.StartMs) / 1000.0,
            ["windows"] = calm.Windows, ["elapsedMs"] = elapsedMs,
            ["v6"] = new JsonObject { ["tag"] = oldTag, ["support"] = calm.StrictSupport, ["minimumSegmentSupport"] = strictMinimum },
            ["v7"] = new JsonObject { ["tag"] = calm.Tag.ToString(), ["support"] = calm.Support, ["minimumSegmentSupport"] = calm.MinimumSegmentSupport },
            ["energeticShare"] = calm.EnergeticShare,
            ["segments"] = new JsonArray(calm.Segments.Select(s => (JsonNode)new JsonObject {
                ["startMs"] = s.StartMs, ["endMs"] = s.EndMs, ["windows"] = s.Windows,
                ["v6Support"] = s.StrictSupport, ["v7Support"] = s.Support }).ToArray()),
            ["samplingReasons"] = new JsonArray(e.Reasons().Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            // Standalone diagnostic fields, not importable analysis_evidence (6 segments exceed the cache contract).
            ["rawSegments"] = JsonNode.Parse(e.ToJson())!["segments"]!.DeepClone()
        };
    }
}
