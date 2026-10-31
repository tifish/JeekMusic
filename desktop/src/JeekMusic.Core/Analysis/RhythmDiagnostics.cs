namespace JeekMusic.Core.Analysis;

public sealed record TempoCandidate(int Lag, double Bpm, double Correlation, double ProductionWeight,
    double[] QuarterCorrelations);
public sealed record OnsetPeak(double Seconds, double Strength, double? IntervalSeconds);
public sealed record RhythmEvidence(double HopSeconds, double[] Envelope, OnsetPeak[] Onsets,
    TempoCandidate[] Candidates, AudioFeatures Features);

/// <summary>Opt-in evidence, never a classifier. Reuses the production spectral-flux envelope.
/// All lags 40–240 BPM are exposed, without choosing a half/double beat interpretation.
/// Correlations and detected spectral onsets are not beat annotations or probabilities.</summary>
public static class RhythmDiagnostics
{
    public static RhythmEvidence Extract(float[] pcm)
    {
        if (pcm.Length > 60 * FeatureExtractor.SampleRate || pcm.Any(x => !float.IsFinite(x)))
            throw new ArgumentException("Expected finite independent PCM interval of at most 60 seconds");
        double[] envelope = [];
        var features = FeatureExtractor.Extract(pcm, observeEnvelope: e => envelope = e);
        const double hop = 256.0 / FeatureExtractor.SampleRate;
        if (envelope.Length == 0) return new(hop, [], [], [], features);
        var mean = envelope.Average();
        var threshold = mean + .5 * Math.Sqrt(envelope.Average(x => (x - mean) * (x - mean)));
        var peaks = new List<OnsetPeak>();
        var last = -(int)(.1 / hop);
        for (int i = 1; i < envelope.Length - 1; i++)
            if (envelope[i] > threshold && envelope[i] >= envelope[i - 1] && envelope[i] > envelope[i + 1] && i - last >= (int)(.1 / hop))
            {
                peaks.Add(new(i * hop, envelope[i], peaks.Count == 0 ? null : (i - last) * hop));
                last = i;
            }
        var candidates = new List<TempoCandidate>();
        for (int lag = (int)Math.Ceiling(60 / (hop * 240)); lag <= (int)(60 / (hop * 40)); lag++)
        {
            var bpm = 60 / (hop * lag);
            var quarters = Enumerable.Range(0, 4).Select(q => Correlation(envelope.AsSpan(q * envelope.Length / 4,
                (q + 1) * envelope.Length / 4 - q * envelope.Length / 4), lag)).ToArray();
            candidates.Add(new(lag, bpm, Correlation(envelope, lag),
                Math.Exp(-.5 * Math.Pow(Math.Log2(bpm / 120), 2)), quarters));
        }
        return new(hop, envelope, peaks.ToArray(), candidates.ToArray(), features);
    }

    // Pearson correlation between overlapping lagged sequences; avoids loudness scaling.
    static double Correlation(ReadOnlySpan<double> e, int lag)
    {
        var n = e.Length - lag;
        if (n < 8) return 0;
        double x = 0, y = 0, xx = 0, yy = 0, xy = 0;
        for (int i = 0; i < n; i++) { var a = e[i]; var b = e[i + lag]; x += a; y += b; xx += a*a; yy += b*b; xy += a*b; }
        var variance = Math.Max(0, xx - x*x/n) * Math.Max(0, yy - y*y/n);
        return variance > 1e-18 ? Math.Clamp((xy - x*y/n) / Math.Sqrt(variance), -1, 1) : 0;
    }
}
