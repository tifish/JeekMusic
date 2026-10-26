using System.Runtime.InteropServices;
using JeekMusic.Infrastructure;
using JeekMusic.Playback;

namespace JeekMusic.Platform;

/// <summary>先在源采样率下协商位深，防止 WASAPI 的自动回退为保留位深而改变采样率。</summary>
internal sealed unsafe partial class ExclusiveAudioFormat : IExclusiveAudioFormat
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint Rate, Bytes;
        public ushort Align, Bits, Extra, ValidBits;
        public uint Mask;
        public Guid Subtype;
    }

    public string? Select(string? deviceName, int sampleRate, int channels, int? sourceBits)
    {
        // Count alone does not identify a multichannel layout. Let mpv negotiate those layouts.
        if (sampleRate <= 0 || channels is < 1 or > 2) return null;
        var initialized = CoInitializeEx(0, 0);
        nint enumerator = 0, device = 0, client = 0;
        try
        {
            if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
            Marshal.ThrowExceptionForHR(CoCreateInstance(new("bcde0395-e52f-467c-8e3d-c4579291692e"), 0, 1,
                new("a95664d2-9614-4f35-a746-de8db63617e6"), out enumerator));
            var methods = *(nint**)enumerator;
            var id = AudioEndpointId.FromDeviceName(deviceName);
            if (id is null)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int, out nint, int>)methods[4])(enumerator, 0, 1, out device));
            else
            {
                fixed (char* name = id)
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, out nint, int>)methods[5])(enumerator, name, out device));
            }
            var iid = new Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
            methods = *(nint**)device;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, out nint, int>)methods[3])(device, &iid, 1, 0, out client));
            return Choose(sourceBits, (bits, validBits, floating) => Supports(client, sampleRate, channels, bits, validBits, floating));
        }
        catch (COMException e)
        {
            Log.Warn("player", "查询原采样率输出格式失败，交由播放内核协商", e);
            return null;
        }
        finally
        {
            if (client != 0) Marshal.Release(client);
            if (device != 0) Marshal.Release(device);
            if (enumerator != 0) Marshal.Release(enumerator);
            if (initialized >= 0) CoUninitialize();
        }
    }

    internal static string? Choose(int? sourceBits, Func<int, int, bool, bool> supports)
    {
        if (sourceBits is > 0 and <= 16 && supports(16, 16, false)) return "s16";
        if (sourceBits is null && supports(32, 32, true)) return "float";
        if (supports(32, 32, false) || supports(32, 24, false) || supports(24, 24, false)) return "s32";
        if (supports(32, 32, true)) return "float";
        return supports(16, 16, false) ? "s16" : null;
    }

    static bool Supports(nint client, int rate, int channels, int bits, int validBits, bool floating)
    {
        var wave = new WaveFormat
        {
            Tag = 0xfffe, Channels = (ushort)channels, Rate = (uint)rate, Bytes = (uint)(rate * channels * bits / 8),
            Align = (ushort)(channels * bits / 8), Bits = (ushort)bits, Extra = 22, ValidBits = (ushort)validBits,
            Mask = channels == 1 ? 4u : 3u,
            Subtype = floating ? new("00000003-0000-0010-8000-00aa00389b71") : new("00000001-0000-0010-8000-00aa00389b71"),
        };
        // IAudioClient::IsFormatSupported(EXCLUSIVE)，不 Initialize / Start，不影响正在播放的音频。
        var query = (delegate* unmanaged[Stdcall]<nint, int, WaveFormat*, nint, int>)(*(nint**)client)[7];
        if (query(client, 1, &wave, 0) == 0) return true;
        // 部分驱动只接受普通 WAVEFORMATEX，按 Windows 的建议也检查这一形式。
        if (bits != validBits || channels > 2) return false;
        wave.Tag = floating ? (ushort)3 : (ushort)1;
        wave.Extra = 0;
        return query(client, 1, &wave, 0) == 0;
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint model);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);
}
