namespace JeekMusic.Core.Analysis;

public sealed record PulsePeak(int Bpm, double Correlation, double Coherence)
{
    public double Salience => Math.Max(0, Correlation) * Coherence;
}

/// <summary>Music-oriented transient periodicity, not annotated beats or a probability.</summary>
public sealed record PercussivePulse(PulsePeak[] Bass, PulsePeak[] Broadband, string? Error = null,
    long StartMs = 0, long EndMs = 0, int Attempts = 1, long ElapsedMs = 0)
{
    public bool SupportsWorkout => Error is null && Bass.Any(b => b.Bpm is >= 95 and <= 220 && b.Salience >= .025 &&
        Broadband.Any(a => a.Bpm is >= 95 and <= 220 && a.Salience >= .05 && Math.Abs(a.Bpm - b.Bpm) <= 3));
}

/// <summary>
/// Bounded HPSS soft mask followed by bass/full-band spectral novelty and a Fourier/ACF product.
/// Independent 11.025 kHz intervals only. No model weights, octave selection, track identities or labels.
/// Numerical reference: desktop/tools/research-workout-pulse.py (not an imported feature contract).
/// </summary>
public static class PercussivePulseExtractor
{
    const int Rate = 11025, Size = 1024, Hop = 128, Radius = 15, Kernel = 31;
    const double Fps = (double)Rate / Hop;
    const int SincRadius = 64;
    static readonly Lazy<double[][]> ResampleWeights = new(() => Enumerable.Range(0, 441).Select(phase =>
    {
        // Rational 16000 -> 11025 resampler. Fixed Blackman-windowed sinc; the
        // legacy box reduction is deliberately NOT changed for legacy features.
        const double cutoff = (11025.0 / 16000) * .97;
        var weights = new double[2 * SincRadius + 1];
        for (int k = -SincRadius; k <= SincRadius; k++)
        {
            double x = k - phase / 441.0, z = Math.PI * cutoff * x;
            double w = Math.Abs(x) > SincRadius ? 0 : .42 + .5 * Math.Cos(Math.PI * x / SincRadius) + .08 * Math.Cos(2 * Math.PI * x / SincRadius);
            weights[k + SincRadius] = cutoff * (Math.Abs(z) < 1e-12 ? 1 : Math.Sin(z) / z) * w;
        }
        double sum = weights.Sum();
        for (int j = 0; j < weights.Length; j++) weights[j] /= sum;
        return weights;
    }).ToArray());

    public static PercussivePulse FromModelPcm(float[] mono16k, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (mono16k.Length < 16000 * 4 || mono16k.Length > 16000 * 31)
            throw new ArgumentException("Requires independent 4–31 second 16 kHz mono PCM");
        if (mono16k.All(x => x == 0)) return new([], []);
        var weights = ResampleWeights.Value;
        var samples = new float[(int)((long)mono16k.Length * 441 / 640)];
        for (int i = 0; i < samples.Length; i++)
        {
            if (i % 256 == 0) ct.ThrowIfCancellationRequested();
            long position = (long)i * 640;
            int center = (int)(position / 441), phase = (int)(position % 441);
            double value = 0;
            for (int k = -SincRadius; k <= SincRadius; k++)
                if (center + k >= 0 && center + k < mono16k.Length)
                    value += mono16k[center + k] * weights[phase][k + SincRadius];
            samples[i] = (float)value;
        }
        return Extract(samples, ct);
    }

    public static PercussivePulse Extract(float[] samples, CancellationToken ct = default)
    {
        if (samples.Length < Rate * 4 || samples.Length > Rate * 31 || samples.Any(x => !float.IsFinite(x)))
            throw new ArgumentException("Pulse extraction requires a finite independent 4–31 second mono interval");
        int frames = 1 + samples.Length / Hop, bins = Size / 2 + 1;
        var magnitude = new double[frames * bins];
        var fft = new Fft(Size);
        var re = new double[Size]; var im = new double[Size]; var window = new double[Size];
        for (int i = 0; i < Size; i++) window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / Size);
        for (int t = 0; t < frames; t++)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < Size; i++)
            {
                int index = t * Hop + i - Size / 2;
                re[i] = index >= 0 && index < samples.Length ? samples[index] * window[i] : 0;
                im[i] = 0;
            }
            fft.Transform(re, im);
            for (int k = 0; k < bins; k++) magnitude[t * bins + k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
        }
        var bass = new double[frames - 1]; var broad = new double[frames - 1];
        var previous = new double[bins]; var horizontal = new double[Kernel]; var vertical = new double[Kernel];
        int low = (int)Math.Ceiling(30.0 * Size / Rate), bassEnd = (int)Math.Ceiling(150.0 * Size / Rate);
        for (int t = 0; t < frames; t++)
        {
            ct.ThrowIfCancellationRequested();
            double bassFlux = 0, broadFlux = 0;
            for (int k = low; k < bins; k++)
            {
                for (int j = -Radius; j <= Radius; j++)
                {
                    horizontal[j + Radius] = magnitude[Reflect(t + j, frames) * bins + k];
                    vertical[j + Radius] = magnitude[t * bins + Reflect(k + j, bins)];
                }
                Array.Sort(horizontal); Array.Sort(vertical);
                double h = horizontal[Radius], p = vertical[Radius], denominator = h * h + p * p;
                double mask = denominator > 0 ? p * p / denominator : .5;
                double value = Math.Log(1 + 100 * magnitude[t * bins + k] * mask);
                double diff = Math.Max(0, value - previous[k]);
                if (k < bassEnd) bassFlux += diff;
                broadFlux += diff;
                previous[k] = value;
            }
            if (t > 0) { bass[t - 1] = bassFlux / (bassEnd - low); broad[t - 1] = broadFlux / (bins - low); }
        }
        return new(Peaks(Novelty(bass), ct), Peaks(Novelty(broad), ct));
    }

    static int Reflect(int i, int n) => i < 0 ? -i - 1 : i >= n ? 2 * n - i - 1 : i;

    static double[] Novelty(double[] flux)
    {
        const int radius = 21, kernel = 43;
        var prefix = new double[flux.Length + 1];
        for (int i = 0; i < flux.Length; i++) prefix[i + 1] = prefix[i] + flux[i];
        var output = new double[flux.Length];
        for (int i = 0; i < output.Length; i++)
        {
            int from = i - radius, to = i + radius + 1;
            var sum = prefix[Math.Min(to, flux.Length)] - prefix[Math.Max(from, 0)] +
                Math.Max(0, -from) * flux[0] + Math.Max(0, to - flux.Length) * flux[^1];
            output[i] = Math.Max(0, flux[i] - sum / kernel);
        }
        return output;
    }

    static PulsePeak[] Peaks(double[] envelope, CancellationToken ct)
    {
        double total = envelope.Sum();
        if (total <= 1e-9) return [];
        var candidates = new PulsePeak[201];
        for (int bpm = 40; bpm <= 240; bpm++)
        {
            ct.ThrowIfCancellationRequested();
            int lag = (int)Math.Round(60 * Fps / bpm), count = envelope.Length - lag;
            double mx = 0, my = 0;
            for (int i = 0; i < count; i++) { mx += envelope[i]; my += envelope[i + lag]; }
            mx /= count; my /= count;
            double xx = 0, yy = 0, xy = 0;
            for (int i = 0; i < count; i++)
            {
                double x = envelope[i] - mx, y = envelope[i + lag] - my;
                xx += x * x; yy += y * y; xy += x * y;
            }
            double denominator = Math.Sqrt(xx * yy), real = 0, imaginary = 0;
            double angle = -2 * Math.PI * bpm / (60 * Fps), c = Math.Cos(angle), s = Math.Sin(angle), cr = 1, ci = 0;
            foreach (double value in envelope)
            {
                real += value * cr; imaginary += value * ci;
                (cr, ci) = (cr * c - ci * s, cr * s + ci * c);
            }
            candidates[bpm - 40] = new(bpm, denominator > 1e-9 ? Math.Clamp(xy / denominator, -1, 1) : 0,
                Math.Clamp(Math.Sqrt(real * real + imaginary * imaginary) / total, 0, 1));
        }
        return candidates.Where((c, i) => c.Salience > 0 &&
            (i == 0 || c.Salience >= candidates[i - 1].Salience) &&
            (i == candidates.Length - 1 || c.Salience > candidates[i + 1].Salience))
            .OrderByDescending(c => c.Salience).Take(8).ToArray();
    }
}
