namespace JeekMusic.Playback;

/// <summary>精确硬件格式探测结果；采样率候选必须经驱动确认，不能用共享客户端的自动转换结果。</summary>
public sealed record DeviceAudioCapabilities(string? DeviceId, int? MixRate, int? Channels,
    IReadOnlyList<int> SharedRates, IReadOnlyList<int> ExclusiveRates, bool CanSetShared, string? Diagnostic);

internal interface IDeviceAudioFormat
{
    DeviceAudioCapabilities Capture(string? device, int? sourceRate, int channels);
    // 调用方先关闭自己的流。实现必须读回系统混音率，并在失败时尝试恢复原格式。
    bool SetSharedRate(string device, int rate, out string? diagnostic);
    int? ReadMixRate(string device);
}
