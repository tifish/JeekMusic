namespace JeekMusic.Core.Analysis;

/// <summary>Rules v6 heuristic over existing v5 windows, independent of workout sensitivity.
/// Support is sampled evidence, not a calibrated probability or a loudness measurement.</summary>
public sealed record CalmnessEvidence(int Windows, double Support, double MinimumSegmentSupport, double EnergeticShare)
{
    public const double CalmFloor = .02, EnergyCeiling = .08, EnergyToCalmCeiling = 1.5;
    public const double SupportThreshold = .7, SegmentSupportThreshold = .5, EnergeticThreshold = .15, EnergeticShareCeiling = .2;
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
        static bool Quiet(ModelScores f) => f.Calm >= CalmFloor && f.Energy <= EnergyCeiling && f.Energy <= f.Calm * EnergyToCalmCeiling;
        return new(frames.Length, frames.Count(Quiet) / (double)frames.Length,
            segments.Min(s => s.Frames.Count(Quiet) / (double)s.Frames.Length),
            frames.Count(f => f.Energy > EnergeticThreshold) / (double)frames.Length);
    }
}
