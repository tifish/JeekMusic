namespace JeekMusic.Playback;

internal sealed record EndpointVolumeSnapshot(string DeviceId, double Volume);

/// <summary>设备主音量独立于共享模式的程序会话音量；以设备 ID 恢复，不能跟随已变化的默认设备。</summary>
internal interface IEndpointVolume
{
    EndpointVolumeSnapshot? Capture(string? device);
    bool Restore(EndpointVolumeSnapshot snapshot);
}
