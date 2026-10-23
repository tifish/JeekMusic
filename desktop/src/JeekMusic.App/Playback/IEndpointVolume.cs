namespace JeekMusic.Playback;

internal sealed record EndpointVolumeSnapshot(string DeviceId, double? Volume, bool? Muted,
    bool? HardwareVolume, bool? HardwareMute, double? SessionVolume, bool? SessionMuted, string? Error = null);

/// <summary>只读探测端点与本进程默认会话；仅显式用户操作可以按已绑定 ID 写端点音量，绝不写静音。</summary>
internal interface IEndpointVolume
{
    EndpointVolumeSnapshot? Capture(string? device);
    string? LastError { get; }
    bool SetVolume(string deviceId, double volume);
}
