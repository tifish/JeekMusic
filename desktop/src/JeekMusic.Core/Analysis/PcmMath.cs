namespace JeekMusic.Core.Analysis;

/// <summary>
/// 分析用 PCM 的取样窗口、下混和抽取（移动端 PcmDecoder.kt 中与平台无关的部分，移动端设计 §6.2）。
/// 两端用同样的算法，保证喂给模型的 16kHz 输入一样。解码本身各端自己做。
/// </summary>
public static class PcmMath
{
    /// <summary>取样窗口：时长 ≥ 40 秒时取 30%、50%、70% 处各 10 秒，否则整首（最多 60 秒）。时间都相对于 <paramref name="startMs"/>。</summary>
    /// <returns>每个窗口的 [起点, 终点)，毫秒</returns>
    public static IReadOnlyList<(long StartMs, long EndMs)> Windows(long startMs, long durationMs)
    {
        if (durationMs < 40_000) return [(startMs, startMs + Math.Min(durationMs, 60_000))];
        return new[] { 0.3, 0.5, 0.7 }.Select(p =>
        {
            var from = startMs + (long)(durationMs * p);
            return (from, from + 10_000);
        }).ToList();
    }

    /// <summary>
    /// 交错的多声道 float 样本 → 单声道。除以 √声道数（功率守恒）：对声道高度相关的音乐，响度与 BS.1770 的"各声道功率之和"一致
    /// （ffmpeg 也是这样下混的）。
    /// </summary>
    public static float[] MixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        channels = Math.Max(1, channels);
        var scale = 1f / MathF.Sqrt(channels);
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum * scale;
        }
        return mono;
    }

    /// <summary>
    /// 抽取到 <paramref name="target"/>：每个输出样本取对应区间的平均值（简单的盒式低通）。
    /// 对统计特征和 YAMNet 的分类都够用；目标采样率不低于源时原样返回。
    /// </summary>
    public static float[] Downsample(float[] input, int sourceRate, int target)
    {
        if (sourceRate <= target) return input;
        var ratio = (double)sourceRate / target;
        var count = (int)(input.Length / ratio);
        var output = new float[count];
        for (var i = 0; i < count; i++)
        {
            var from = (int)(i * ratio);
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
