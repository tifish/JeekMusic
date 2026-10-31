namespace JeekMusic.Core.Analysis;

/// <summary>Conservative v8 extension: sustained model evidence AND independent musical pulse evidence.
/// The legacy thresholds remain unchanged; these are uncalibrated support measures, not probabilities.</summary>
public static class WorkoutEvidence
{
    public static int StrongRun(SegmentEvidence segment, Sensitivity sensitivity)
    {
        int current = 0, best = 0;
        foreach (var frame in segment.Frames)
        {
            current = TagClassifier.SportScore(frame) >= sensitivity.Sport() ? current + 1 : 0;
            best = Math.Max(best, current);
        }
        return best;
    }

    public static bool NeedsPulse(AnalysisEvidence evidence) => evidence.Complete &&
        evidence.Aggregate() is { Sport: < 3.75 } &&
        evidence.ScoredSegments().Count(s => StrongRun(s, Sensitivity.LOOSE) >= 3) >= 2;

    public static IReadOnlyList<(SegmentEvidence Segment, long StartMs, long EndMs)> Plan(
        AnalysisEvidence evidence, long logicalStartMs, long durationMs)
    {
        var result = new List<(SegmentEvidence, long, long)>();
        if (!NeedsPulse(evidence)) return result;
        long length = Math.Min(30000, durationMs);
        if (length < 4000) return result;
        foreach (var s in evidence.ScoredSegments().Where(s => StrongRun(s, Sensitivity.LOOSE) >= 3))
        {
            long start = Math.Clamp((s.StartMs + s.EndMs - length) / 2, logicalStartMs, logicalStartMs + durationMs - length);
            long end = start + length;
            // Independent continuous contexts, never CUE/track seams or duplicated evidence.
            if (result.Any(w => start < w.Item3 && end > w.Item2)) continue;
            result.Add((s, start, end));
            if (result.Count == 3) break;
        }
        return result.Count >= 2 ? result : [];
    }

    public static int SupportedSegments(AnalysisEvidence evidence, Sensitivity sensitivity)
    {
        int count = 0;
        long end = long.MinValue;
        foreach (var s in evidence.ScoredSegments().Where(s => StrongRun(s, sensitivity) >= 3 && s.Pulse?.SupportsWorkout == true)
                     .OrderBy(s => s.Pulse!.EndMs))
        {
            if (s.Pulse!.StartMs < end) continue;
            count++; end = s.Pulse.EndMs;
        }
        return count;
    }

    public static Tag? Classify(AnalysisEvidence evidence, Sensitivity sensitivity)
    {
        if (evidence.Aggregate() is not { } scores) return null;
        if (scores.Sport >= sensitivity.Sport()) return Tag.SPORT;
        if (evidence.Contract < 6) return Tag.NON_SPORT; // valid old results retained during progressive upgrade
        if (SupportedSegments(evidence, sensitivity) >= 2) return Tag.SPORT;
        if (evidence.RhythmState == "failed") return null; // unavailable new evidence is not negative evidence
        return Tag.NON_SPORT;
    }
}
