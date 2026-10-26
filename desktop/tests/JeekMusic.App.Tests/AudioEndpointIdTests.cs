using JeekMusic.Platform;
using JeekMusic.Playback;
using Xunit.Abstractions;

namespace JeekMusic.App.Tests;

public sealed class AudioEndpointIdTests(ITestOutputHelper output)
{
    const string GuidAlias = "{6C26BA7D-F0B2-4225-B422-8168C5261E45}";
    const string Remote = "{3.0.0.00000001}." + GuidAlias;
    const string Local = "{0.0.0.00000000}." + GuidAlias;

    [Theory]
    [InlineData(null, null)]
    [InlineData("auto", null)]
    [InlineData(Remote, Remote)]
    [InlineData("wasapi/" + Remote, Remote)]
    [InlineData(Local, Local)]
    [InlineData("wasapi/" + Local, Local)]
    [InlineData(GuidAlias, Local)]
    [InlineData("wasapi/" + GuidAlias, Local)]
    [InlineData("future-opaque-endpoint", "future-opaque-endpoint")]
    [InlineData("wasapi/future-opaque-endpoint", "future-opaque-endpoint")]
    public void PreservesOpaqueIdsAndOnlyExpandsTheLegacyBracedGuidAlias(string? input, string? expected)
    {
        Assert.Equal(expected, AudioEndpointId.FromDeviceName(input));
        Assert.Equal(expected, AudioEndpointId.FromDeviceName(expected));
    }

    [EndpointReadFact]
    public void RealDefaultEndpointRoundTripsThroughVolumeAndFormatReadersWithoutMutation()
    {
        var volume = new EndpointVolume();
        var initial = volume.Capture(null);
        Assert.NotNull(initial);
        Assert.NotNull(initial.Volume);
        var source = new SourceAudioFormat(44100, null, 2, 3, "stereo", "unknown");
        var formats = new DeviceAudioFormat();
        var baseline = formats.CaptureFormat(null, source);
        Assert.Equal(initial.DeviceId, baseline.DeviceId);
        Assert.NotNull(baseline.MixFormat);
        foreach (var name in new[] { initial.DeviceId, "wasapi/" + initial.DeviceId })
        {
            var rebound = volume.Capture(name);
            Assert.NotNull(rebound);
            Assert.Equal(initial.DeviceId, rebound.DeviceId);
            Assert.Equal(initial.Volume, rebound.Volume);
            var caps = formats.CaptureFormat(name, source);
            Assert.Equal(baseline.DeviceId, caps.DeviceId);
            Assert.Equal(baseline.MixFormat, caps.MixFormat);
            Assert.Equal(baseline.DeviceFormat, caps.DeviceFormat);
            Assert.Equal(baseline.SupportedFormats, caps.SupportedFormats);
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(caps));
            // Capability query only: neither Initialize/Start nor a format/volume write.
            var selected = new ExclusiveAudioFormat().Select(name, 44100, 2, null);
            output.WriteLine($"Exclusive 44100 selection: {selected ?? "unsupported"}");
            Assert.Equal(new ExclusiveAudioFormat().Select(null, 44100, 2, null), selected);
        }
        const string missing = "{3.0.0.00000001}.{00000000-0000-0000-0000-000000000000}";
        Assert.Null(volume.Capture(missing));
        Assert.Contains(missing, volume.LastError);
        Assert.Null(formats.CaptureFormat(missing, source).DeviceId);
        Assert.Null(new ExclusiveAudioFormat().Select(missing, 44100, 2, null));
    }
}

public sealed class EndpointReadFactAttribute : FactAttribute
{
    public EndpointReadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("JEEKMUSIC_TEST_ENDPOINT_READ") != "1")
            Skip = "Opt-in read-only Core Audio probe requires JEEKMUSIC_TEST_ENDPOINT_READ=1 and an active default output.";
    }
}
