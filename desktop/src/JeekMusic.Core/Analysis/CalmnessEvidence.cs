namespace JeekMusic.Core.Analysis;

/// <summary>Rules v7 heuristic over existing v5 windows, independent of workout sensitivity.
/// Support is sampled evidence, not a calibrated probability or a loudness measurement.</summary>
public sealed record CalmnessEvidence(int Windows, double Support, double MinimumSegmentSupport, double EnergeticShare)
{
    public const double CalmFloor = .02, EnergyCeiling = .08, EnergyToCalmCeiling = 1.5;
    public const double SupportThreshold = .7, SegmentSupportThreshold = .5, EnergeticThreshold = .15, EnergeticShareCeiling = .2;
    // A weak sad/tender/lullaby proxy is not necessarily agitation. Admit it only
    // with stronger relative dominance and a lower absolute energy ceiling.
    // Zero/tiny calm still supplies no positive evidence, even when energy is zero.
    public const double RelativeCalmFloor = .01, RelativeEnergyCeiling = .02, RelativeEnergyToCalmCeiling = .5;
    public double StrictSupport { get; init; }
    public double RelativeSupport { get; init; }
    public CalmnessSegmentEvidence[] Segments { get; init; } = [];
    public Tag Tag => Support >= SupportThreshold && MinimumSegmentSupport >= SegmentSupportThreshold &&
        EnergeticShare <= EnergeticShareCeiling ? Tag.CALM : Tag.NON_CALM;

    public static CalmnessEvidence? From(AnalysisEvidence? evidence)
    {
        if (evidence?.Complete != true) return null;
        var segments = evidence.ScoredSegments();
        var frames = segments.SelectMany(s => s.Frames).ToArray();
        // Too little evidence or effectively absent mood signal is not a negative label.
        if (frames.Length < 3 || frames.Any(f => !double.IsFinite(f.Calm) || !double.IsFinite(f.Energy) || f.Calm < 0 || f.Energy < 0) ||
            !frames.Any(f => f.Calm + f.Energy >= .01)) return null;
        static bool Strict(ModelScores f) => f.Calm >= CalmFloor && f.Energy <= EnergyCeiling && f.Energy <= f.Calm * EnergyToCalmCeiling;
        static bool Relative(ModelScores f) => f.Calm >= RelativeCalmFloor && f.Energy <= RelativeEnergyCeiling && f.Energy <= f.Calm * RelativeEnergyToCalmCeiling;
        static bool Quiet(ModelScores f) => Strict(f) || Relative(f);
        var details = segments.Select(s => new CalmnessSegmentEvidence(s.StartMs, s.EndMs, s.Frames.Length,
            s.Frames.Count(Quiet) / (double)s.Frames.Length, s.Frames.Count(Strict) / (double)s.Frames.Length,
            s.Frames.Count(Relative) / (double)s.Frames.Length)).ToArray();
        return new(frames.Length, frames.Count(Quiet) / (double)frames.Length,
            details.Min(s => s.Support), frames.Count(f => f.Energy > EnergeticThreshold) / (double)frames.Length)
        {
            StrictSupport = frames.Count(Strict) / (double)frames.Length,
            RelativeSupport = frames.Count(Relative) / (double)frames.Length, Segments = details
        };
    }
}

public sealed record CalmnessSegmentEvidence(long StartMs, long EndMs, int Windows,
    double Support, double StrictSupport, double RelativeSupport);
