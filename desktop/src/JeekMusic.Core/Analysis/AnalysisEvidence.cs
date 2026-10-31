using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeekMusic.Core.Analysis;

public sealed record SegmentEvidence(long StartMs, long EndMs, bool Additional, int SampleCount,
    int DecodeAttempts, int ModelAttempts, ModelScores? Mean, ModelScores[] Frames, string? Error,
    PercussivePulse? Pulse = null)
{
    public int ValidWindows => Frames.Length;
    public bool Complete => Mean is not null && Frames.Length > 0 && Error is null;
}

public sealed record DimensionEvidence(double Vocal, double Sport, double Classical,
    double VocalSupport, double SportSupport, double ClassicalSupport);

/// <summary>Versioned v5/v6 evidence. Margins/support describe sampled evidence, never calibrated probabilities.</summary>
public sealed record AnalysisEvidence(int Contract, SegmentEvidence[] Segments, string[] AdditionalReasons,
    bool FeatureComplete, string? FeatureError, long ElapsedMs, int RhythmContract = 0, string? RhythmState = null,
    bool BaseEvidenceReused = false)
{
    public bool Complete => Contract is 5 or 6 && FeatureComplete && Segments.Any(s => !s.Additional) &&
        Segments.Where(s => !s.Additional).All(s => s.Complete);
    public SegmentEvidence[] ScoredSegments() => Segments.Where(s => s.Complete &&
        (!s.Additional || !Segments.Any(x => x.Additional && !x.Complete))).ToArray();
    public DimensionEvidence? Aggregate()
    {
        var good = ScoredSegments();
        var frames = good.SelectMany(s => s.Frames).ToArray();
        if (frames.Length == 0) return null;
        // A sustained run can retain vocals through a long instrumental break;
        // a lone high frame cannot rescue a negative mean. Never cross segment seams.
        var vocal = frames.Average(f => f.Vocal);
        foreach (var s in good)
            for (int i = 0; i + 2 < s.Frames.Length; i++)
                vocal = Math.Max(vocal, s.Frames.Skip(i).Take(3).Min(f => f.Vocal) - .5);
        // Median window ratio requires sustained energy, not a loud/fast isolated passage.
        var sports = frames.Select(TagClassifier.SportScore).Order().ToArray();
        var sport = (sports[(sports.Length - 1) / 2] + sports[sports.Length / 2]) / 2;
        // Whole sampled style: duration-weighted segment genre ratios, no instrument shortcut.
        var classical = good.Sum(s => s.Mean!.Classical * s.ValidWindows) / frames.Length;
        return new(vocal, sport, classical,
            frames.Count(f => f.Vocal >= TagClassifier.VocalThreshold) / (double)frames.Length,
            frames.Count(f => TagClassifier.SportScore(f) >= Sensitivity.NORMAL.Sport()) / (double)frames.Length,
            frames.Count(f => f.Classical >= TagClassifier.ClassicalThreshold) / (double)frames.Length);
    }

    public string[] Reasons()
    {
        var reasons = new List<string>();
        var a = Aggregate();
        var good = ScoredSegments();
        if (a is null || good.Sum(s => s.ValidWindows) < 10 || good.Length != Segments.Length) reasons.Add("insufficient_evidence");
        if (a is not null)
        {
            if (Math.Abs(a.Vocal - TagClassifier.VocalThreshold) <= .5) reasons.Add("voice_near_threshold");
            if (new[] { 2.75, 3.25, 3.75 }.Any(t => Math.Abs(a.Sport - t) <= .35)) reasons.Add("mood_near_threshold");
            if (Math.Abs(a.Classical - TagClassifier.ClassicalThreshold) <= .35) reasons.Add("genre_near_threshold");
            bool Conflict(Func<SegmentEvidence, bool> positive) => good.Any(positive) && good.Any(s => !positive(s));
            if (Conflict(s => s.Mean!.Vocal >= TagClassifier.VocalThreshold)) reasons.Add("voice_segment_conflict");
            if (Conflict(s => TagClassifier.SportScore(s.Mean!) >= 3.25)) reasons.Add("mood_segment_conflict");
            if (Conflict(s => s.Mean!.Classical >= TagClassifier.ClassicalThreshold)) reasons.Add("genre_segment_conflict");
        }
        return reasons.ToArray();
    }
    public string ToJson() => JsonSerializer.Serialize(this, EvidenceJson.Default.AnalysisEvidence);
    public static AnalysisEvidence? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var e = JsonSerializer.Deserialize(json, EvidenceJson.Default.AnalysisEvidence);
            if (e is null || e.Contract is not (5 or 6) || e.Segments is null || e.AdditionalReasons is null || e.Segments.Length > 5 ||
                e.Contract == 5 && (e.RhythmContract != 0 || e.RhythmState is not null || e.BaseEvidenceReused || e.Segments.Any(s => s?.Pulse is not null)) ||
                e.Contract == 6 && (e.RhythmContract != 1 || e.RhythmState is not ("not_needed" or "complete" or "failed")) ||
                e.Segments.Any(s => s is null || s.Frames is null || s.Frames.Length > 62 || s.EndMs <= s.StartMs ||
                    s.Frames.Any(f => f is null || !Finite(f)) || s.Mean is { } m && !Finite(m) ||
                    s.Pulse is { } p && (p.Bass is null || p.Broadband is null || p.Bass.Length > 8 || p.Broadband.Length > 8 ||
                        p.StartMs < 0 || p.StartMs > s.StartMs || p.EndMs < s.EndMs || p.EndMs-p.StartMs > 30000 || p.Attempts is < 1 or > 3 ||
                        p.Bass.Concat(p.Broadband).Any(x => x is null || x.Bpm is < 40 or > 240 || !double.IsFinite(x.Correlation) ||
                            !double.IsFinite(x.Coherence) || x.Correlation is < -1 or > 1 || x.Coherence is < 0 or > 1)))) return null;
            return e;
        }
        catch (JsonException) { return null; }
    }
    static bool Finite(ModelScores m) => double.IsFinite(m.Vocal) && double.IsFinite(m.Classical) &&
        double.IsFinite(m.Calm) && double.IsFinite(m.Energy) && double.IsFinite(m.Orchestra);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnalysisEvidence))]
internal partial class EvidenceJson : JsonSerializerContext;
