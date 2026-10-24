namespace JeekMusic.Playback;

/// <summary>精确硬件格式探测结果；采样率候选必须经驱动确认，不能用共享客户端的自动转换结果。</summary>
public sealed record DeviceAudioCapabilities(string? DeviceId, int? MixRate, int? Channels,
    IReadOnlyList<int> SharedRates, IReadOnlyList<int> ExclusiveRates, bool CanSetShared, string? Diagnostic,
    EndpointAudioFormat? DeviceFormat = null, EndpointAudioFormat? MixFormat = null, IReadOnlyList<EndpointAudioFormat>? SupportedFormats = null);

internal interface IDeviceAudioFormat
{
    DeviceAudioCapabilities CaptureFormat(string? device, SourceAudioFormat source);
    // Caller closes its own stream first. Failure must attempt complete original-format rollback.
    bool SetSharedFormat(string device, EndpointAudioFormat format, out string? diagnostic);
}
