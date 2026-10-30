using System.Diagnostics;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;

namespace JeekMusic.Analysis;

/// <summary>Bounded, per-module retries. Successful decoding is reused when model inference fails.</summary>
public static class TrackAnalyzer
{
    public static async Task<FeaturesData> Analyze(string path, Track track, CancellationToken ct,
        Func<float[], CancellationToken, (ModelScores? Mean, ModelScores[] Frames)> infer,
        Func<string, long, long, CancellationToken, Task<float[]>>? decode = null)
    {
        decode ??= PcmDecoder.Window;
        var timer = Stopwatch.StartNew();
        var segments = new List<SegmentEvidence>();
        float[] longest = [];
        async Task Sample((long StartMs, long EndMs) w, bool additional)
        {
            float[]? pcm = null;
            string? error = null;
            int decodes = 0, models = 0;
            ModelScores? mean = null;
            ModelScores[] frames = [];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (attempt > 0) await Task.Delay(attempt == 1 ? 200 : 800, ct);
                try
                {
                    if (pcm is null) { decodes++; pcm = await decode(path, w.StartMs, w.EndMs, ct); }
                    if (pcm.Length < (w.EndMs - w.StartMs) * 16 - 1600)
                    { pcm = null; throw new InvalidDataException("truncated_window"); }
                    if (pcm.Length < YamnetScores.Frame) { error = "decode:insufficient_samples"; break; }
                    if (pcm.Length > longest.Length) longest = pcm;
                    models++;
                    (mean, frames) = infer(pcm, ct);
                    if (mean is null || frames.Length == 0) throw new InvalidDataException("empty_model_output");
                    error = null;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { error = (pcm is null ? "decode:" : "model:") + e.GetType().Name; }
            }
            segments.Add(new(w.StartMs, w.EndMs, additional, pcm?.Length ?? 0, decodes, models, mean, frames, error));
        }
        var start = track.StartMs ?? 0;
        var duration = track.EndMs is { } end ? Math.Min(track.DurationMs, end - start) : track.DurationMs;
        var initial = PcmMath.Windows(start, duration);
        foreach (var w in initial) await Sample(w, false);
        var first = new AnalysisEvidence(5, segments.ToArray(), [], false, null, 0);
        var reasons = first.Reasons();
        if (reasons.Length > 0)
            foreach (var w in PcmMath.AdditionalWindows(start, duration, initial)) await Sample(w, true);
        AudioFeatures? features = null;
        string? featureError = null;
        for (int attempt = 0; attempt < 3 && longest.Length > 0; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 0) await Task.Delay(attempt == 1 ? 200 : 800, ct);
            try { features = FeatureExtractor.Extract(PcmMath.Downsample(longest, 16000, FeatureExtractor.SampleRate)); break; }
            catch (Exception e) { featureError = "features:" + e.GetType().Name; }
        }
        var good = new AnalysisEvidence(5, segments.ToArray(), [], false, null, 0).ScoredSegments();
        var count = good.Sum(s => s.ValidWindows);
        double Mean(Func<ModelScores, double> selector) => good.Sum(s => selector(s.Mean!) * s.ValidWindows) / count;
        var scores = count == 0 ? null : new ModelScores(Mean(m => m.Vocal), Mean(m => m.Classical),
            Mean(m => m.Calm), Mean(m => m.Energy), Mean(m => m.Orchestra));
        if (segments.Any(s => s.Additional && !s.Complete)) reasons = [.. reasons, "additional_failure_first_pass_retained"];
        var evidence = new AnalysisEvidence(5, segments.ToArray(), reasons, features is not null,
            features is null ? featureError ?? "features:no_pcm" : null, timer.ElapsedMilliseconds);
        return FeaturesData.Of(features, scores) with { AnalysisEvidence = evidence.ToJson(), AnalysisAttempts = evidence.Complete ? 1 : 3 };
    }
}
