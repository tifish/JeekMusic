namespace JeekMusic.Core.Analysis;

/// <summary>一首歌的音频特征（移动端 FeatureExtractor.kt，移动端设计 §6.3）。</summary>
/// <param name="Loudness">近似 LUFS。</param>
/// <param name="LoudnessRange">动态范围，单位 LU。</param>
/// <param name="OnsetRate">每秒起音数。</param>
/// <param name="LowRatio">150Hz 以下能量占比。</param>
public sealed record AudioFeatures(
    double? Bpm,
    double BpmConfidence,
    double Loudness,
    double LoudnessRange,
    double OnsetRate,
    double CentroidHz,
    double LowRatio);

/// <summary>
/// 从单声道 PCM（[-1, 1]）中提取特征（移动端 FeatureExtractor.kt）。帧长 1024、帧移 256、汉宁窗。
/// 输入通常是 3 个各 10 秒的片段拼起来，片段之间的接缝对统计特征影响很小。
/// </summary>
public static class FeatureExtractor
{
    public const int SampleRate = 11_025;
    private const int Frame = 1024;
    private const int Hop = 256;
    private const double Fps = (double)SampleRate / Hop;

    public static AudioFeatures Extract(float[] samples, int sampleRate = SampleRate)
    {
        if (sampleRate != SampleRate) throw new ArgumentException($"resample to {SampleRate} Hz first", nameof(sampleRate));
        var frames = samples.Length < Frame ? 0 : (samples.Length - Frame) / Hop + 1;
        if (frames < 8) return new AudioFeatures(null, 0.0, -70.0, 0.0, 0.0, 0.0, 0.0);

        var fft = new Fft(Frame);
        var window = new double[Frame];
        for (var i = 0; i < Frame; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Frame - 1));
        const int bins = Frame / 2 + 1;
        const double binHz = (double)SampleRate / Frame;
        var lowBin = (int)(150 / binHz);

        var flux = new double[frames];
        var previous = new double[bins];
        var centroidSum = 0.0;
        var centroidFrames = 0;
        var lowEnergy = 0.0;
        var totalEnergy = 0.0;
        var re = new double[Frame];
        var im = new double[Frame];

        for (var f = 0; f < frames; f++)
        {
            var offset = f * Hop;
            for (var i = 0; i < Frame; i++)
            {
                re[i] = samples[offset + i] * window[i];
                im[i] = 0.0;
            }
            fft.Transform(re, im);
            var current = new double[bins];
            var magSum = 0.0;
            var weighted = 0.0;
            var fluxSum = 0.0;
            for (var k = 0; k < bins; k++)
            {
                var power = re[k] * re[k] + im[k] * im[k];
                var mag = Math.Sqrt(power);
                // 对数压缩后的幅度做频谱通量，对响度不敏感
                var compressed = Math.Log(1 + 100 * mag);
                current[k] = compressed;
                var diff = compressed - previous[k];
                if (diff > 0) fluxSum += diff;
                magSum += mag;
                weighted += mag * k * binHz;
                if (k <= lowBin) lowEnergy += power;
                totalEnergy += power;
            }
            flux[f] = fluxSum;
            if (magSum > 1e-3)
            {
                centroidSum += weighted / magSum;
                centroidFrames++;
            }
            previous = current;
        }
        flux[0] = 0.0;

        var envelope = OnsetEnvelope(flux);
        var (bpm, confidence) = Tempo(envelope);
        var (loudness, range) = Loudness(samples);
        return new AudioFeatures(
            Bpm: bpm,
            BpmConfidence: confidence,
            Loudness: loudness,
            LoudnessRange: range,
            OnsetRate: OnsetRate(envelope),
            CentroidHz: centroidFrames > 0 ? centroidSum / centroidFrames : 0.0,
            LowRatio: totalEnergy > 0 ? lowEnergy / totalEnergy : 0.0);
    }

    /// <summary>减去约 0.5 秒的滑动平均后半波整流，突出起音。</summary>
    private static double[] OnsetEnvelope(double[] flux)
    {
        var radius = (int)(Fps / 4);
        var prefix = new double[flux.Length + 1];
        for (var i = 0; i < flux.Length; i++) prefix[i + 1] = prefix[i] + flux[i];
        var result = new double[flux.Length];
        for (var i = 0; i < flux.Length; i++)
        {
            var from = Math.Max(0, i - radius);
            var to = Math.Min(flux.Length, i + radius + 1);
            var mean = (prefix[to] - prefix[from]) / (to - from);
            result[i] = Math.Max(0.0, flux[i] - mean);
        }
        return result;
    }

    /// <summary>
    /// 对起音包络做自相关，在 60–200 BPM 的范围内找峰值；乘以以 120 BPM 为中心的对数高斯权重减少倍频误判。
    /// 置信度 = 峰值相对于所有候选的平均值的倍数。
    /// </summary>
    private static (double? Bpm, double Confidence) Tempo(double[] envelope)
    {
        var minLag = (int)(60 * Fps / 200);
        var maxLag = (int)(60 * Fps / 60) + 1;
        if (envelope.Length < maxLag * 4) return (null, 0.0);
        var mean = envelope.Average();
        var centered = envelope.Select(v => v - mean).ToArray();
        var acf = new double[maxLag + 2];
        for (var lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            var sum = 0.0;
            for (var i = 0; i < centered.Length - lag; i++) sum += centered[i] * centered[i + lag];
            acf[lag] = sum / (centered.Length - lag);
        }
        var bestLag = -1;
        var bestScore = double.NegativeInfinity;
        var scoreSum = 0.0;
        var count = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var bpm = 60 * Fps / lag;
            var x = Math.Log2(bpm / 120) / 1.0;
            var weight = Math.Exp(-0.5 * (x * x));
            var score = acf[lag] * weight;
            scoreSum += Math.Max(0.0, score);
            count++;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }
        if (bestLag < 0 || bestScore <= 0) return (null, 0.0);
        // 抛物线插值求小数延迟
        var y0 = acf[bestLag - 1];
        var y1 = acf[bestLag];
        var y2 = acf[bestLag + 1];
        var denom = y0 - 2 * y1 + y2;
        var shift = denom != 0.0 ? Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5) : 0.0;
        var tempo = 60 * Fps / (bestLag + shift);
        var confidence = bestScore / Math.Max(scoreSum / count, 1e-9);
        return (tempo, confidence);
    }

    /// <summary>起音包络的峰值：高于"均值 + 0.5 倍标准差"、且与上一个峰相隔至少 0.1 秒。</summary>
    private static double OnsetRate(double[] envelope)
    {
        var mean = envelope.Average();
        var std = Math.Sqrt(envelope.Sum(v => (v - mean) * (v - mean)) / envelope.Length);
        var threshold = mean + 0.5 * std;
        var minGap = (int)(Fps * 0.1);
        var last = -minGap;
        var peaks = 0;
        for (var i = 1; i < envelope.Length - 1; i++)
        {
            var v = envelope[i];
            if (v > threshold && v >= envelope[i - 1] && v > envelope[i + 1] && i - last >= minGap)
            {
                peaks++;
                last = i;
            }
        }
        return peaks / (envelope.Length / Fps);
    }

    /// <summary>
    /// 近似 LUFS：400ms 块的均方，绝对门限 -70、相对门限 -10（ITU-R BS.1770 的门限积分，省略 K 计权的高架滤波）。
    /// 动态范围：3 秒短时响度的第 10 到第 95 百分位之差（相对门限 -20）。
    /// </summary>
    private static (double Integrated, double Range) Loudness(float[] samples)
    {
        var block = (int)(SampleRate * 0.4);
        var step = block / 4;
        var blocks = new List<double>();
        var start = 0;
        while (start + block <= samples.Length)
        {
            var sum = 0.0;
            for (var i = start; i < start + block; i++) sum += (double)samples[i] * samples[i];
            blocks.Add(sum / block);
            start += step;
        }
        static double Lufs(double ms) => -0.691 + 10 * Math.Log10(Math.Max(ms, 1e-12));
        var absolute = blocks.Where(b => Lufs(b) > -70).ToList();
        if (absolute.Count == 0) return (-70.0, 0.0);
        var ungated = Lufs(absolute.Average());
        var gated = absolute.Where(b => Lufs(b) > ungated - 10).ToList();
        var integrated = Lufs((gated.Count > 0 ? gated : absolute).Average());

        const int shortBlock = SampleRate * 3;
        const int shortStep = SampleRate / 2;
        var shortTerm = new List<double>();
        start = 0;
        while (start + shortBlock <= samples.Length)
        {
            var sum = 0.0;
            for (var i = start; i < start + shortBlock; i++) sum += (double)samples[i] * samples[i];
            shortTerm.Add(Lufs(sum / shortBlock));
            start += shortStep;
        }
        var usable = shortTerm.Where(v => v > -70 && v > integrated - 20).Order().ToList();
        var range = usable.Count >= 2
            ? usable[(int)((usable.Count - 1) * 0.95)] - usable[(int)((usable.Count - 1) * 0.10)]
            : 0.0;
        return (integrated, range);
    }
}

/// <summary>原地 radix-2 FFT（移动端 Fft.kt）。re 和 im 的长度必须相同且是 2 的幂。</summary>
public sealed class Fft
{
    private readonly int _size;
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly int _bits;

    public Fft(int size)
    {
        if (size <= 0 || (size & (size - 1)) != 0) throw new ArgumentException("size must be a power of two", nameof(size));
        _size = size;
        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (var i = 0; i < size / 2; i++)
        {
            _cos[i] = Math.Cos(-2 * Math.PI * i / size);
            _sin[i] = Math.Sin(-2 * Math.PI * i / size);
        }
        _bits = System.Numerics.BitOperations.TrailingZeroCount(size);
    }

    public void Transform(double[] re, double[] im)
    {
        for (var i = 0; i < _size; i++)
        {
            var j = (int)(ReverseBits((uint)i) >> (32 - _bits));
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= _size; len *= 2)
        {
            var half = len / 2;
            var step = _size / len;
            for (var start = 0; start < _size; start += len)
            {
                for (var k = 0; k < half; k++)
                {
                    var c = _cos[k * step];
                    var s = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = re[b] * c - im[b] * s;
                    var ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }

    private static uint ReverseBits(uint v)
    {
        v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
        v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
        v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
        v = ((v >> 8) & 0x00FF00FFu) | ((v & 0x00FF00FFu) << 8);
        return (v >> 16) | (v << 16);
    }
}
