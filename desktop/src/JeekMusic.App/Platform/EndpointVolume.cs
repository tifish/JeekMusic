using System.Runtime.InteropServices;
using JeekMusic.Playback;

namespace JeekMusic.Platform;

/// <summary>Core Audio 端点与本进程默认会话。COM vtable 调用兼容 Native AOT；不写会话音量或任何系统静音。</summary>
internal sealed unsafe partial class EndpointVolume : IEndpointVolume
{
    static readonly Guid EnumeratorClass = new("bcde0395-e52f-467c-8e3d-c4579291692e");
    static readonly Guid EnumeratorInterface = new("a95664d2-9614-4f35-a746-de8db63617e6");
    static readonly Guid VolumeInterface = new("5cdf2c82-841e-4546-9722-0cf74078229a");
    static readonly Guid SessionManagerInterface = new("bfa971f1-4d5e-40bb-935e-967039bfbee4");
    public string? LastError { get; private set; }

    public EndpointVolumeSnapshot? Capture(string? device) => Access(device, null);

    public bool SetVolume(string deviceId, double volume) =>
        double.IsFinite(volume) && volume is >= 0 and <= 100 &&
        Access(deviceId, volume) is { Volume: { } actual } && Math.Abs(actual - volume) <= 0.5;

    EndpointVolumeSnapshot? Access(string? name, double? target)
    {
        var id = name is null or "auto" ? null : name.StartsWith("wasapi/", StringComparison.Ordinal) ? name[7..] : name;
        if (id is not null && !id.StartsWith("{0.0.0.00000000}.", StringComparison.Ordinal))
            id = "{0.0.0.00000000}." + id;
        var initialized = CoInitializeEx(0, 0);
        nint enumerator = 0, device = 0, endpoint = 0, idPointer = 0, manager = 0, session = 0;
        LastError = null;
        string stage = "COM";
        try
        {
            if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
            Marshal.ThrowExceptionForHR(CoCreateInstance(EnumeratorClass, 0, 1, EnumeratorInterface, out enumerator));
            var methods = *(nint**)enumerator;
            stage = "resolve endpoint";
            if (id is null)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int, out nint, int>)methods[4])(enumerator, 0, 1, out device));
            else
                fixed (char* key = id)
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, out nint, int>)methods[5])(enumerator, key, out device));
            methods = *(nint**)device;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, out uint, int>)methods[6])(device, out var state));
            if (state != 1) { LastError = $"endpoint inactive (state={state})"; return null; }
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, out nint, int>)methods[5])(device, out idPointer));
            var deviceId = Marshal.PtrToStringUni(idPointer);
            if (string.IsNullOrEmpty(deviceId)) { LastError = "empty endpoint ID"; return null; }
            double? volume = null, sessionVolume = null;
            bool? mute = null, hardwareVolume = null, hardwareMute = null, sessionMute = null;
            var errors = new List<string>();
            void Check(int hr, string operation) { if (hr < 0) errors.Add($"{operation}: 0x{hr:X8}"); }
            var iid = VolumeInterface;
            var result = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, out nint, int>)methods[3])(device, &iid, 1, 0, out endpoint);
            Check(result, "activate endpoint volume");
            if (result >= 0)
            {
                var v = *(nint**)endpoint;
                result = ((delegate* unmanaged[Stdcall]<nint, out uint, int>)v[19])(endpoint, out var support);
                Check(result, "hardware support");
                if (result >= 0) { hardwareVolume = (support & 1) != 0; hardwareMute = (support & 2) != 0; }
                if (target is { } desired)
                {
                    stage = "set endpoint volume";
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, float, nint, int>)v[7])(endpoint, (float)(desired / 100), 0));
                }
                result = ((delegate* unmanaged[Stdcall]<nint, out float, int>)v[9])(endpoint, out var scalar);
                Check(result, "read endpoint volume");
                if (result >= 0 && float.IsFinite(scalar) && scalar is >= 0 and <= 1) volume = scalar * 100.0;
                result = ((delegate* unmanaged[Stdcall]<nint, out int, int>)v[15])(endpoint, out var muted);
                Check(result, "read endpoint mute");
                if (result >= 0) mute = muted != 0;
            }
            // mpv WASAPI uses GUID_NULL and no cross-process flag. Windows initializes a new
            // session at unity; opening an existing one retains the user's mixer attenuation.
            iid = SessionManagerInterface;
            result = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, out nint, int>)methods[3])(device, &iid, 1, 0, out manager);
            Check(result, "activate session manager");
            if (result >= 0)
            {
                var v = *(nint**)manager;
                result = ((delegate* unmanaged[Stdcall]<nint, nint, uint, out nint, int>)v[4])(manager, 0, 0, out session);
                Check(result, "open process default session");
                if (result >= 0)
                {
                    v = *(nint**)session;
                    result = ((delegate* unmanaged[Stdcall]<nint, out float, int>)v[4])(session, out var scalar);
                    Check(result, "read session volume");
                    if (result >= 0 && float.IsFinite(scalar) && scalar is >= 0 and <= 1) sessionVolume = scalar * 100.0;
                    result = ((delegate* unmanaged[Stdcall]<nint, out int, int>)v[6])(session, out var muted);
                    Check(result, "read session mute");
                    if (result >= 0) sessionMute = muted != 0;
                }
            }
            LastError = errors.Count == 0 ? null : string.Join("; ", errors);
            return new(deviceId, volume, mute, hardwareVolume, hardwareMute, sessionVolume, sessionMute, LastError);
        }
        catch (COMException e)
        {
            LastError = $"{stage}: 0x{e.HResult:X8} {e.Message}";
            return null;
        }
        finally
        {
            if (session != 0) Marshal.Release(session);
            if (manager != 0) Marshal.Release(manager);
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
