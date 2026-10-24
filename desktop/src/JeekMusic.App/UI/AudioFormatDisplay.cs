using JeekMusic.Playback;

namespace JeekMusic.UI;

internal static class AudioFormatDisplay
{
    public static string Rate(int? rate) => rate is > 0 ? $"{rate / 1000.0:0.###} kHz" : "—";
    public static string Channels(int? count, string? layout) => count is > 0
        ? L.F($"{count} 声道") + (layout is null or "unknown" ? "" : $" ({layout})") : L.T("声道未知");
    public static string Bits(EndpointAudioFormat format) => format.SampleKind == "float" ? L.F($"{format.ContainerBits} 位浮点") :
        format.ValidBits == format.ContainerBits ? L.F($"{format.ValidBits} 位") : L.F($"{format.ValidBits} 有效位 / {format.ContainerBits} 位容器");
    public static string Source(PlayerService p)
    {
        var source = p.SourceFormat;
        return L.T("歌曲：") + Rate(source.SampleRate) + " · " +
            (source.Bits is { } bits ? L.F($"{bits} 位") : L.T("位深未知 / 不适用")) + " · " + Channels(source.Channels, source.ChannelMask is not null ? source.Layout : null);
    }
    public static string Device(PlayerService p)
    {
        if (p.OutputPending) return L.T("设备：正在切换");
        if (p.Exclusive) return L.T("设备：") + Rate(p.ActualSampleRate) + " · " + L.T("有效位深未知（独占流）") + " · " +
            Channels(p.Format.DeviceChannels, null);
        if (p.ActualDeviceFormat is not { } actual) return L.T("设备：格式读取失败");
        return L.T("设备：") + Rate(actual.SampleRate) + " · " + Bits(actual) + " · " + Channels(actual.Channels, actual.Layout);
    }
}
