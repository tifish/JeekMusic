using JeekMusic.Platform;

namespace JeekMusic.App.Tests;

public sealed class ExclusiveAudioFormatTests
{
    [Fact]
    public void Keeps44100With16BitWhenTheDeviceOnlySupports24BitAtHigherRates()
    {
        // 同一设备在 48/96kHz 支持 24bit，但查询限定在歌曲的 44.1kHz。
        var selected = ExclusiveAudioFormat.Choose(null, (bits, valid, floating) => !floating && bits == 16 && valid == 16);
        Assert.Equal("s16", selected);
    }

    [Fact]
    public void DoesNotReduce24BitAudioWhenTheSourceRateSupportsIt()
    {
        var selected = ExclusiveAudioFormat.Choose(24, (bits, valid, floating) => !floating && (bits == 16 || bits == 24));
        Assert.Equal("s32", selected); // mpv 以 s32 表达 24bit PCM。
    }

    [Fact]
    public void Preserves32BitIntegerPrecisionInsteadOfConvertingToFloat()
    {
        Assert.Equal("s32", ExclusiveAudioFormat.Choose(32, (_, _, _) => true));
    }

    [Fact]
    public void UsesNativeFloatForDecodedLossyAudioWhenSupported()
    {
        Assert.Equal("float", ExclusiveAudioFormat.Choose(null, (_, _, _) => true));
    }

    [Fact]
    public void KeepsTheSourceRateWithFloatWhenNoIntegerFormatIsSupportedAtThatRate()
    {
        Assert.Equal("float", ExclusiveAudioFormat.Choose(32, (_, _, floating) => floating));
    }

    [Fact]
    public void LeavesUnsupportedRatesToTheBackendInsteadOfForcingAnInvalidFormat()
    {
        Assert.Null(ExclusiveAudioFormat.Choose(24, (_, _, _) => false));
    }
}
