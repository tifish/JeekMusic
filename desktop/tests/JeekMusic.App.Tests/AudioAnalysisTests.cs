using JeekMusic.Analysis;
using JeekMusic.Core.Analysis;

namespace JeekMusic.App.Tests;

public sealed class AudioAnalysisTests
{
    [Fact]
    public async Task Stereo44100UsesTheSameMixAndDownsamplingAsMobile()
    {
        var path = Path.Combine(Path.GetTempPath(), "jeek-stereo-" + Guid.NewGuid().ToString("N") + ".wav");
        const int rate = 44100, count = rate * 2;
        var source = new float[count * 2];
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8); writer.Write(36 + count * 4); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(rate);
                writer.Write(rate * 4); writer.Write((short)4); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(count * 4);
                for (int i = 0; i < count; i++) for (int c = 0; c < 2; c++)
                {
                    var value = (short)(Math.Sin(i * Math.PI * 2 * (c == 0 ? 440 : 660) / rate) * 8192);
                    writer.Write(value); source[i * 2 + c] = value / 32768f;
                }
            }
            var expected = PcmMath.Downsample(PcmMath.MixToMono(source, 2), rate, 16000);
            var actual = await PcmDecoder.Window(path, 0, 2000, CancellationToken.None);
            Assert.Equal(expected.Length, actual.Length);
            Assert.True(expected.Zip(actual).Max(p => Math.Abs(p.First - p.Second)) < 0.000001f);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PipeDecoderReadsRequestedWindowWithoutAudioDevice()
    {
        var path = Path.Combine(Path.GetTempPath(), "jeek-pcm-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                const int rate = 16000, count = rate * 3;
                writer.Write("RIFF"u8); writer.Write(36 + count * 2); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
                writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(count * 2);
                for (int i = 0; i < count; i++) writer.Write((short)(i < rate ? 0 : Math.Sin(i * Math.PI * 2 * 440 / rate) * 8192));
            }
            var pcm = await PcmDecoder.Window(path, 1000, 2000, CancellationToken.None);
            Assert.InRange(pcm.Length, 15980, 16000);
            Assert.InRange(pcm.Select(x => (double)x * x).Average(), .030, .032);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConvertedModelProducesFiniteScores()
    {
        using var model = new AudioTagger();
        var scores = model.Analyze(new float[YamnetScores.Frame]);
        Assert.NotNull(scores);
        Assert.True(double.IsFinite(scores.Vocal));
        Assert.True(double.IsFinite(scores.Classical));
        Assert.InRange(scores.Calm, 0, 6);
        Assert.InRange(scores.Energy, 0, 12);
    }
}
