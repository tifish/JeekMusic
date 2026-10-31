using System.Diagnostics;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;

namespace JeekMusic.Analysis;

/// <summary>Production v6 pulse stage, shared by background work, explicit batches and diagnostics.</summary>
public static class WorkoutAnalysis
{
    public static FeaturesData? UpgradeWithoutAudio(FeaturesRow row, string signature)
    {
        if (row.AnalyzerVersion != 5 || row.Signature != signature || row.Failed || FeaturesData.Parse(row.Data) is not { Complete: true } data ||
            AnalysisEvidence.Parse(data.AnalysisEvidence) is not { Contract: 5 } evidence || WorkoutEvidence.NeedsPulse(evidence)) return null;
        return data with { AnalysisEvidence = (evidence with { Contract = 6, RhythmContract = 1, RhythmState = "not_needed", BaseEvidenceReused = true }).ToJson() };
    }

    public static async Task<FeaturesData> Complete(string path, Track track, FeaturesData data, CancellationToken ct,
        Func<string, long, long, CancellationToken, Task<float[]>>? decode = null, bool reuseBase = false)
    {
        decode ??= PcmDecoder.Window;
        var timer = Stopwatch.StartNew();
        var evidence = AnalysisEvidence.Parse(data.AnalysisEvidence) ?? throw new InvalidDataException("base_evidence");
        long start = track.StartMs ?? 0;
        long duration = track.EndMs is { } end ? Math.Min(track.DurationMs, end - start) : track.DurationMs;
        var plan = WorkoutEvidence.Plan(evidence, start, duration);
        var segments = evidence.Segments.ToArray();
        bool failed = false;
        foreach (var w in plan)
        {
            float[]? pcm = null;
            PercussivePulse? pulse = null;
            string? error = null;
            int attempts = 0;
            var stage = Stopwatch.StartNew();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                attempts++;
                if (attempt > 0) await Task.Delay(attempt == 1 ? 200 : 800, ct);
                try
                {
                    pcm ??= await decode(path, w.StartMs, w.EndMs, ct);
                    if (pcm.Length < (w.EndMs - w.StartMs) * 16 - 1600)
                    { pcm = null; throw new InvalidDataException("truncated_pulse_context"); }
                    pulse = PercussivePulseExtractor.FromModelPcm(pcm, ct);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { error = e.GetType().Name; }
            }
            failed |= pulse is null;
            pulse = (pulse ?? new PercussivePulse([], [], error)) with {
                StartMs = w.StartMs, EndMs = w.EndMs, Attempts = attempts, ElapsedMs = stage.ElapsedMilliseconds };
            int index = Array.IndexOf(evidence.Segments, w.Segment);
            segments[index] = w.Segment with { Pulse = pulse };
        }
        ct.ThrowIfCancellationRequested();
        var updated = evidence with { Contract = 6, Segments = segments, RhythmContract = 1,
            RhythmState = failed ? "failed" : plan.Count == 0 ? "not_needed" : "complete", BaseEvidenceReused = reuseBase,
            ElapsedMs = (reuseBase ? 0 : evidence.ElapsedMs) + timer.ElapsedMilliseconds };
        return data with { AnalysisEvidence = updated.ToJson(), AnalysisAttempts = failed ? 3 : data.AnalysisAttempts };
    }
}
