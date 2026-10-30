using System.Diagnostics;
using JeekMusic.Core.Analysis;
using JeekMusic.Core.Library;
using JeekMusic.Core.Text;
using Xunit.Abstractions;

namespace JeekMusic.Core.Tests;

/// <summary>
/// 不适合写成共用 JSON 的测试：用合成信号测特征提取（移动端 FeatureExtractorTest.kt），
/// 以及移植时容易出错的地方（正则、Unicode、代理项、排序稳定性）。
/// </summary>
public class PortTests(ITestOutputHelper output)
{
    private const int Sr = FeatureExtractor.SampleRate;

    /// <summary>节拍器：每拍一个短促的鼓点（衰减的噪声加低频）。</summary>
    private static float[] ClickTrack(double bpm, int seconds, float amplitude = 0.8f)
    {
        var data = new float[Sr * seconds];
        var period = Sr * 60 / bpm;
        var random = new Random(1);
        for (var beat = 0.0; beat < data.Length; beat += period)
        {
            var start = (int)beat;
            for (var i = 0; i < (int)(Sr * 0.08) && start + i < data.Length; i++)
            {
                var env = Math.Exp(-i / (Sr * 0.015));
                var kick = Math.Sin(2 * Math.PI * 60 * i / Sr);
                data[start + i] += (float)(amplitude * env * (0.6 * kick + 0.4 * (random.NextDouble() * 2 - 1)));
            }
        }
        return data;
    }

    private static float[] Sine(double freq, int seconds, double amplitude) =>
        Enumerable.Range(0, Sr * seconds).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * freq * i / Sr))).ToArray();

    [Theory]
    [InlineData(90.0)]
    [InlineData(128.0)]
    [InlineData(150.0)]
    public void MetronomeBpm(double bpm)
    {
        var f = FeatureExtractor.Extract(ClickTrack(bpm, 30));
        Assert.NotNull(f.Bpm);
        Assert.True(Math.Abs(f.Bpm!.Value - bpm) < 3, $"expected {bpm}, got {f.Bpm}");
        Assert.True(f.BpmConfidence > 1.3, $"confidence {f.BpmConfidence}");
    }

    [Fact]
    public void OnsetRateNearBeatsPerSecond()
    {
        var f = FeatureExtractor.Extract(ClickTrack(120.0, 30));
        Assert.True(Math.Abs(f.OnsetRate - 2.0) < 0.5, $"onset rate {f.OnsetRate}");
    }

    [Fact]
    public void SineLoudness()
    {
        // 幅度 0.1 的正弦，均方 0.005 → -0.691 + 10·log10(0.005) ≈ -23.7
        var f = FeatureExtractor.Extract(Sine(1000.0, 10, 0.1));
        Assert.InRange(f.Loudness, -24.2, -23.2);
        Assert.True(f.LoudnessRange < 1);
    }

    [Fact]
    public void LowRatioAndCentroid()
    {
        var low = FeatureExtractor.Extract(Sine(80.0, 10, 0.3));
        var high = FeatureExtractor.Extract(Sine(3000.0, 10, 0.3));
        Assert.True(low.LowRatio > 0.9, $"low ratio {low.LowRatio}");
        Assert.True(high.LowRatio < 0.05, $"high ratio {high.LowRatio}");
        Assert.True(high.CentroidHz > low.CentroidHz);
        Assert.InRange(high.CentroidHz, 2700, 3300);
    }

    [Fact]
    public void SilenceAndTooShort()
    {
        var silence = FeatureExtractor.Extract(new float[Sr * 10]);
        Assert.Equal(-70.0, silence.Loudness, 2);
        Assert.Null(silence.Bpm);
        Assert.Null(FeatureExtractor.Extract(new float[100]).Bpm);
    }

    [Fact]
    public void Downsample()
    {
        var input = Enumerable.Range(0, 16_000).Select(i => i % 2 == 0 ? 1f : -1f).ToArray();
        var output = PcmMath.Downsample(input, 16_000, 11_025);
        Assert.Equal(11_025, output.Length);
        // 盒式平均会把奈奎斯特附近的高频平均掉
        Assert.All(output, v => Assert.InRange(v, -1f, 1f));
        Assert.Same(input, PcmMath.Downsample(input, 16_000, 16_000));
    }

    [Fact]
    public void MixToMonoKeepsModelRange()
    {
        var mono = PcmMath.MixToMono([0.5f, 0.5f, 1f, 1f], 2);
        Assert.Equal(2, mono.Length);
        Assert.Equal(0.5f, mono[0]);
        Assert.Equal(1f, mono[1]);
        Assert.Equal([(0L, 30_000L)], PcmMath.Windows(0, 30_000));
        Assert.Equal([(30_000L, 40_000L), (50_000L, 60_000L), (70_000L, 80_000L)], PcmMath.Windows(0, 100_000));
    }

    [Fact]
    public void YamnetGroups()
    {
        var means = new float[YamnetScores.Classes];
        means[24] = 0.5f; // Singing
        means[232] = 0.2f; // Classical music
        var s = YamnetScores.Scores(means);
        Assert.True(s.Vocal > 5);
        Assert.True(s.Classical > 5);
    }

    [Fact]
    public void FftOfImpulseIsFlat()
    {
        var fft = new Fft(8);
        var re = new double[8];
        var im = new double[8];
        re[0] = 1;
        fft.Transform(re, im);
        Assert.All(re, v => Assert.Equal(1.0, v, 9));
        Assert.All(im, v => Assert.Equal(0.0, v, 9));
        Assert.Throws<ArgumentException>(() => new Fft(6));
    }

    private sealed record Song(string FileNameKey, string TitleKey, string ArtistKey, bool ArtistUnknown, long Modified, int? CueTrackNo = null) : ISortableSong;

    [Fact]
    public void SongSorting()
    {
        var a = new Song("a. mp3", "zhou", "x", false, 1);
        var b = new Song("b. mp3", "an", "", true, 3);
        var c = new Song("c. flac", "an", "y", false, 2);
        var c2 = new Song("c. flac", "an", "y", false, 2, 2);
        var c1 = new Song("c. flac", "an", "y", false, 2, 1);
        Assert.Equal([a, b, c1, c2], SongSorter.Sorted([c2, b, a, c1], SongSort.FILE_NAME, s => s));
        Assert.Equal([b, c1, c2, a], SongSorter.Sorted([a, c2, b, c1], SongSort.MODIFIED, s => s));
        Assert.Equal([b, c1, a], SongSorter.Sorted([a, c1, b], SongSort.TITLE, s => s));
        // 未知艺术家排最后；缺失的条目排最后、保持原顺序
        Assert.Equal<Song?>([a, c, b, null, null], SongSorter.Sorted<Song?>([null, b, c, null, a], SongSort.ARTIST, s => s));
        Assert.Equal("zhou jie lun", SongSorter.Key("周杰伦"));
    }

    [Fact]
    public void CueResolve()
    {
        const string cue = "FILE \"album.wav\" WAVE\n TRACK 01 AUDIO\n  INDEX 01 00:00:00\n TRACK 02 AUDIO\n  INDEX 01 03:00:00\n";
        var siblings = new[] { "album.flac", "album.cue", "cover.jpg" };
        // CUE 里写 .wav、实际是 .flac：按同名不同扩展名匹配（需求 CUE-5）
        var resolved = CueResolver.Resolve("album.flac", 40 * 60_000, null, siblings, _ => cue);
        Assert.NotNull(resolved);
        Assert.Equal(2, resolved.File.Tracks.Count);
        // 太短的文件不找外部 CUE
        Assert.Null(CueResolver.Resolve("album.flac", 60_000, null, siblings, _ => cue));
        // 内嵌 cuesheet 不限时长
        Assert.NotNull(CueResolver.Resolve("album.flac", 60_000, cue, siblings, _ => null));
        // 不是整轨格式
        Assert.Null(CueResolver.Resolve("album.mp3", 40 * 60_000, null, siblings, _ => cue));
        // 桌面端可以把 APE 也算作整轨
        var ape = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "flac", "wav", "ape" };
        Assert.NotNull(CueResolver.Resolve("album.ape", 40 * 60_000, null, ["album.ape", "album.cue"], _ => cue, ape));
        // 只有一首的不算整轨
        Assert.Null(CueResolver.Pick(CueParser.Parse("FILE \"a.flac\" WAVE\n TRACK 01 AUDIO\n  INDEX 01 00:00:00"), "a.flac", single: true));
    }

    [Fact]
    public void PinyinEdgeCases()
    {
        // 孤立的代理项原样保留（ICU 也是），不会变成 U+FFFD，也不会让 Normalize 抛异常
        Assert.Equal("a\uD800b", Pinyin.Transliterate("a\uD800b"));
        Assert.Equal("zhou\uDC00", Pinyin.Transliterate("周\uDC00"));
        Assert.Equal("", Pinyin.Transliterate(""));
        // 分解形式的附加符号（macOS 上常见的 NFD 文件名）
        Assert.Equal("Cafe", Pinyin.Transliterate("Café"));
        // 扩展 B 区汉字
        Assert.Equal("he", Pinyin.Transliterate("\U00020000"));
    }

    [Fact]
    public void ChineseTextContextRules()
    {
        // 简转繁：数字后面的"只"是"隻"，否则看词组
        Assert.Equal("三隻", ChineseText.ToTraditional("三只"));
        Assert.Equal("週一", ChineseText.ToTraditional("周一"));
        Assert.Equal("周傑倫", ChineseText.ToTraditional("周杰伦"));
        Assert.Equal("单于", ChineseText.ToSimplified("單于"));
        Assert.Equal("Queen", ChineseText.ToSimplified("Queen"));
    }

    [Fact]
    public void TablesLoadQuickly()
    {
        var sw = Stopwatch.StartNew();
        Pinyin.Preload();
        var pinyin = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        _ = ChineseText.ToTraditional("周");
        _ = ChineseText.ToSimplified("張");
        var chinese = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        for (var i = 0; i < 10_000; i++) _ = Pinyin.Transliterate("01. 周杰伦 - 晴天.flac");
        output.WriteLine($"拼音表加载 {pinyin:F0} ms，繁简表加载 {chinese:F0} ms，转写 1 万次 {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(pinyin < 2000);
    }
}
