using System.Runtime.InteropServices;
using JeekMusic.Infrastructure;
using JeekMusic.Playback;

namespace JeekMusic.Platform;

/// <summary>Windows 设备主音量。直接调用 COM vtable，确定性释放引用，兼容 Native AOT。</summary>
internal sealed unsafe partial class EndpointVolume : IEndpointVolume
{
    static readonly Guid EnumeratorClass = new("bcde0395-e52f-467c-8e3d-c4579291692e");
    static readonly Guid EnumeratorInterface = new("a95664d2-9614-4f35-a746-de8db63617e6");
    static readonly Guid VolumeInterface = new("5cdf2c82-841e-4546-9722-0cf74078229a");

    public EndpointVolumeSnapshot? Capture(string? device)
    {
        var id = device is null or "auto" ? null : device.StartsWith("wasapi/", StringComparison.Ordinal) ? device[7..] : device;
        if (id is not null && !id.StartsWith("{0.0.0.00000000}.", StringComparison.Ordinal))
            id = "{0.0.0.00000000}." + id;
        return Access(id, null);
    }

    public bool Restore(EndpointVolumeSnapshot snapshot) =>
        double.IsFinite(snapshot.Volume) && snapshot.Volume is >= 0 and <= 100 &&
        Access(snapshot.DeviceId, snapshot.Volume) is { } actual && Math.Abs(actual.Volume - snapshot.Volume) <= 0.5;

    static EndpointVolumeSnapshot? Access(string? id, double? target)
    {
        // 已在 STA 初始化的 UI 线程会返回 RPC_E_CHANGED_MODE，仍可使用已有的 COM apartment。
        var initialized = CoInitializeEx(0, 0);
        nint enumerator = 0, device = 0, endpoint = 0, idPointer = 0;
        try
        {
            if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
            Marshal.ThrowExceptionForHR(CoCreateInstance(EnumeratorClass, 0, 1, EnumeratorInterface, out enumerator));
            var methods = *(nint**)enumerator;
            if (id is null)
                // IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eMultimedia)，与 mpv 一致。
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int, out nint, int>)methods[4])(enumerator, 0, 1, out device));
            else
                fixed (char* name = id)
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, out nint, int>)methods[5])(enumerator, name, out device));
            methods = *(nint**)device;
            // IMMDevice::GetId / Activate(IAudioEndpointVolume)。
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, out nint, int>)methods[5])(device, out idPointer));
            var iid = VolumeInterface;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, out nint, int>)methods[3])(device, &iid, 1, 0, out endpoint));
            methods = *(nint**)endpoint;
            if (target is { } volume)
                // IAudioEndpointVolume::SetMasterVolumeLevelScalar；不修改静音或声道平衡。
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, float, nint, int>)methods[7])(endpoint, (float)(volume / 100), 0));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, out float, int>)methods[9])(endpoint, out var scalar));
            return float.IsFinite(scalar) && scalar is >= 0 and <= 1 && Marshal.PtrToStringUni(idPointer) is { Length: > 0 } deviceId
                ? new(deviceId, scalar * 100.0) : null;
        }
        catch (COMException e)
        {
            Log.Warn("volume", target is null ? "读取系统主音量失败" : "恢复系统主音量失败", e);
            return null;
        }
        finally
        {
            if (idPointer != 0) Marshal.FreeCoTaskMem(idPointer);
            if (endpoint != 0) Marshal.Release(endpoint);
            if (device != 0) Marshal.Release(device);
            if (enumerator != 0) Marshal.Release(enumerator);
            if (initialized >= 0) CoUninitialize();
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint model);
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);
}
