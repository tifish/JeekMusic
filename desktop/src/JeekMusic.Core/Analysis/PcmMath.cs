namespace JeekMusic.Core.Analysis;

/// <summary>
/// 分析用 PCM 的取样窗口、下混和抽取（移动端 PcmDecoder.kt 中与平台无关的部分，移动端设计 §6.2）。
/// 两端用同样的算法，保证喂给模型的 16kHz 输入一样。解码本身各端自己做。
/// </summary>
public static class PcmMath
{
    /// <summary>Half-open source intervals, bounded by the logical track (including CUE).</summary>
    public static IReadOnlyList<(long StartMs, long EndMs)> Windows(long startMs, long durationMs)
    {
        if (durationMs <= 0) return [];
        if (durationMs < 40_000) return [(startMs, startMs + durationMs)];
        // Avoid overlap on 40–50 second tracks.
        var result = new List<(long StartMs, long EndMs)>();
        foreach (var p in new[] { .3, .5, .7 })
        {
            var from = Math.Min(startMs + (long)(durationMs * p), startMs + durationMs - 10_000);
            if (result.Count > 0) from = Math.Max(from, result[^1].EndMs);
            if (from + 10_000 <= startMs + durationMs) result.Add((from, from + 10_000));
        }
        return result;
    }

    public static IReadOnlyList<(long StartMs, long EndMs)> AdditionalWindows(long startMs, long durationMs,
        IReadOnlyList<(long StartMs, long EndMs)> used)
    {
        if (durationMs < 40_000) return []; // Already sampled the complete short track.
        var result = new List<(long StartMs, long EndMs)>();
        foreach (var p in new[] { .1, .85, 0.0, .9 })
        {
            var from = Math.Min(startMs + (long)(durationMs * p), startMs + durationMs - 10_000);
            var end = from + 10_000;
            if (used.Concat(result).Any(w => from < w.EndMs && end > w.StartMs)) continue;
            result.Add((from, end));
            if (result.Count == 2) break;
        }
        return result;
    }

    /// <summary>Arithmetic channel mean, finite and clamped to [-1,1]. Not BS.1770 loudness.</summary>
    public static float[] MixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        var mono = new float[interleaved.Length / channels];
        for (var i = 0; i < mono.Length; i++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var v = interleaved[i * channels + c];
                sum += float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0;
            }
            mono[i] = (float)(sum / channels);
        }
        return mono;
    }

    /// <summary>v5 contract: box reduction, linear interpolation for upsampling, floor output length.</summary>
    public static float[] Downsample(float[] input, int sourceRate, int target)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(target, 1);
        if (sourceRate == target) return input;
        var ratio = (double)sourceRate / target;
        var output = new float[(int)(input.Length / ratio)];
        for (var i = 0; i < output.Length; i++)
        {
            var position = i * ratio;
            var from = (int)position;
            if (sourceRate < target)
            {
                output[i] = (float)(input[from] + (input[Math.Min(from + 1, input.Length - 1)] - input[from]) * (position - from));
                continue;
            }
            var to = Math.Min(input.Length, Math.Max((int)((i + 1) * ratio), from + 1));
            var sum = 0f;
            for (var j = from; j < to; j++) sum += input[j];
            output[i] = sum / (to - from);
        }
        return output;
    }

}

/// <summary>
/// YAMNet（AudioSet 521 类）的平均分 → 分类用的几组得分（移动端 AudioTagger.kt 的 scores，移动端设计 §6.3）。
/// 类别编号来自 YAMNet 的 yamnet_class_map.csv；分组和阈值见 mobile/tools/calibration/ 的校准。
/// </summary>
public static class YamnetScores
{
    public const int SampleRate = 16_000;

    /// <summary>一帧 0.975 秒，帧之间不重叠。</summary>
    public const int Frame = 15_600;

    public const int Classes = 521;

    /// <summary>Singing, Choir, Child singing, Rapping, Humming, Chant, Vocal music, A capella, Opera, Speech, Lullaby, Synthetic singing</summary>
    private static readonly int[] Voice = [24, 25, 29, 31, 32, 27, 249, 250, 233, 0, 266, 30];

    /// <summary>
    /// Musical instrument, Bowed string instrument, Violin, Cello, Piano, Keyboard, Orchestra, String section, Brass instrument,
    /// Trumpet, Woodwind instrument, Flute, Double bass, Electric piano, Pizzicato, Plucked string instrument, Guitar
    /// </summary>
    private static readonly int[] Instrument = [133, 184, 186, 188, 148, 147, 179, 185, 180, 182, 190, 191, 189, 149, 187, 134, 135];

    /// <summary>Classical music, Opera：只用曲风类。钢琴、弦乐等乐器类在流行抒情歌、翻奏和配乐里同样很高，不能当作古典的证据</summary>
    private static readonly int[] Classical = [232, 233];

    /// <summary>其他所有曲风类（211–231、234–248、250–260、262–265、267–270，不含 Vocal music、Song、Lullaby）</summary>
    private static readonly int[] Pop =
    [
        .. Enumerable.Range(211, 21), .. Enumerable.Range(234, 15), .. Enumerable.Range(250, 11), .. Enumerable.Range(262, 4), .. Enumerable.Range(267, 4),
    ];

    /// <summary>Tender, Sad, Lullaby, New-age, Ambient, Background music</summary>
    private static readonly int[] Calm = [273, 272, 266, 248, 241, 262];

    /// <summary>Exciting, Happy, Dance, Electronic, Hip hop, Rock, Drum kit, Drum, Rapping, Disco, Pop, Electronic dance music</summary>
    private static readonly int[] Energy = [274, 271, 269, 234, 212, 214, 157, 159, 31, 231, 211, 240];

    public static ReadOnlySpan<int> EnergyIds => Energy;
    public static ReadOnlySpan<int> CalmIds => Calm;

    private const int Orchestra = 179;

    /// <summary>各类别在所有帧上的平均分 → 分类用的几组得分。</summary>
    public static ModelScores Scores(ReadOnlySpan<float> means)
    {
        var voice = Sum(means, Voice);
        var instrument = Sum(means, Instrument);
        var classical = Sum(means, Classical);
        var pop = Sum(means, Pop);
        return new ModelScores(
            Vocal: Math.Log((voice + 1e-3) / (instrument + 1e-3)),
            Classical: Math.Log((classical + 1e-3) / (pop + 1e-3)),
            Calm: Sum(means, Calm),
            Energy: Sum(means, Energy),
            Orchestra: means[Orchestra]);
    }

    private static double Sum(ReadOnlySpan<float> means, int[] ids)
    {
        var s = 0.0;
        foreach (var id in ids) s += means[id];
        return s;
    }
}
