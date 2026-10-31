using JeekMusic.Core.Analysis;

namespace JeekMusic.Core.Tests.Analysis;

public sealed class PercussivePulseTests
{
    static float[] Percussion(int bpm, double gain = 1)
    {
        var samples = new float[16000 * 6];
        uint state = 12345;
        for (int i = 0; i < samples.Length; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            double t = i / 16000.0, phase = (t + .1) % (60.0 / bpm);
            double noise = (state / (double)uint.MaxValue * 2 - 1) * .4 * Math.Exp(-phase / .025);
            double bass = Math.Sin(2 * Math.PI * 80 * phase) * .6 * Math.Exp(-phase / .07);
            samples[i] = (float)((noise + bass) * gain);
        }
        return samples;
    }

    [Theory]
    [InlineData(80, false)] [InlineData(120, true)]
    public void EmptyInterbeatsDoNotTurnSlowPercussionIntoFastPercussion(int bpm, bool sport)
    {
        var result = PercussivePulseExtractor.FromModelPcm(Percussion(bpm));
        Assert.True(Math.Abs(result.Broadband[0].Bpm - bpm) <= 1);
        Assert.Equal(sport, result.SupportsWorkout);
    }

    [Fact]
    public void SilenceHasNoPulseAndCancellationStopsWork()
    {
        var result = PercussivePulseExtractor.FromModelPcm(new float[16000 * 4]);
        Assert.Empty(result.Bass); Assert.Empty(result.Broadband);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => PercussivePulseExtractor.FromModelPcm(new float[16000 * 10], cancel.Token));
        Assert.Throws<ArgumentException>(() => PercussivePulseExtractor.FromModelPcm(new float[16000 * 32]));
    }
}
