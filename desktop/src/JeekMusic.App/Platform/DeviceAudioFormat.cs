using System.Runtime.InteropServices;
using JeekMusic.Playback;

namespace JeekMusic.Platform;

/// <summary>
/// Core Audio 精确硬件探测 + Windows 系统混音格式修改。IPolicyConfig 是 Windows 非公开接口，
/// 不保证所有系统/驱动可用；每次写入均读回 IAudioClient.GetMixFormat，失败返回 HRESULT 并回滚。
/// 绝不将 IsFormatSupported(SHARED) 的可转换格式冒充设备支持。
/// </summary>
internal sealed unsafe partial class DeviceAudioFormat : IDeviceAudioFormat
{
    // Windows 没有通用的“枚举全部采样率”接口。探测标准音乐/视频采样率，另加歌曲与当前混音率。
    internal static readonly int[] ProbeRates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000,
        64000, 88200, 96000, 128000, 176400, 192000, 256000, 352800, 384000, 705600, 768000];

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint Rate, Bytes;
        public ushort Align, Bits, Extra, ValidBits;
        public uint Mask;
        public Guid Subtype;
    }

    public DeviceAudioCapabilities Capture(string? device, int? sourceRate, int channels)
    {
        try
        {
            using var access = new Access(device);
            var mix = access.Mix();
            var endpoint = mix;
            var canSet = access.PolicyAvailable;
            string? diagnostic = canSet ? null : "IPolicyConfig unavailable; Windows shared mix format cannot be changed on this system.";
            if (canSet)
            {
                try { endpoint = MemoryMarshal.Read<WaveFormat>(access.DeviceBytes()); }
                catch (COMException e) { canSet = false; diagnostic = Failure("query shared endpoint format", e); }
            }
            var shared = new List<int>();
            var exclusive = new List<int>();
            foreach (var rate in ProbeRates.Concat(new[] { sourceRate ?? 0, (int)mix.Rate }).Where(r => r is > 0 and <= 1536000).Distinct().Order())
            {
                if (access.Find(rate, mix.Channels, endpoint) is not null) shared.Add(rate);
                if (access.Find(rate, channels) is not null) exclusive.Add(rate);
            }
            return new(access.Id, (int)mix.Rate, mix.Channels, canSet ? shared : [], exclusive, canSet,
                diagnostic);
        }
        catch (COMException e) { return new(null, null, null, [], [], false, Failure("query device formats", e)); }
    }

    public int? ReadMixRate(string device)
    {
        try { using var access = new Access(device); return (int)access.Mix().Rate; }
        catch (COMException) { return null; }
    }

    public bool SetSharedRate(string device, int rate, out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            using var access = new Access(device);
            var originalMix = access.MixBytes();
            var mix = MemoryMarshal.Read<WaveFormat>(originalMix);
            if (mix.Rate == rate) return true;
            var originalEndpoint = access.DeviceBytes();
            var endpoint = access.Find(rate, mix.Channels, MemoryMarshal.Read<WaveFormat>(originalEndpoint));
            if (endpoint is null) { diagnostic = $"Driver does not support {rate} Hz at {mix.Channels} channels."; return false; }
            // 保留原混音格式的声道布局/位深/扩展字段，只修改采样率和每秒字节数。
            var targetMix = (byte[])originalMix.Clone();
            fixed (byte* buffer = targetMix)
            {
                var wave = (WaveFormat*)buffer;
                wave->Rate = (uint)rate;
                wave->Bytes = checked((uint)rate * wave->Align);
            }
            try
            {
                access.Set(endpoint.Value, targetMix);
                // 修改会使已激活的客户端失效，用新客户端读取系统实际混音格式。
                if (ReadMixRate(device) != rate) throw new COMException("Windows mix format readback did not match the requested rate.");
                return true;
            }
            catch (COMException e)
            {
                diagnostic = Failure("set shared mix format", e);
                try { access.SetBytes(originalEndpoint, originalMix); }
                catch (COMException rollback) { diagnostic += "; rollback failed: " + Failure("restore mix format", rollback); }
                diagnostic += $"; actual mix rate: {ReadMixRate(device)?.ToString() ?? "unavailable"} Hz";
                return false;
            }
        }
        catch (COMException e) { diagnostic = Failure("set shared mix format", e); return false; }
    }

    static string Failure(string stage, COMException e) => $"{stage}: 0x{e.HResult:X8} {e.Message}";

    internal (byte[] Endpoint, byte[] Mix) CaptureSystemFormat(string device)
    {
        using var access = new Access(device);
        return (access.DeviceBytes(), access.MixBytes());
    }

    internal void RestoreSystemFormat(string device, (byte[] Endpoint, byte[] Mix) format)
    {
        using var access = new Access(device);
        access.SetBytes(format.Endpoint, format.Mix);
    }

    sealed class Access : IDisposable
    {
        readonly int _initialized;
        nint _enumerator, _device, _client, _policy;
        public string Id { get; private set; } = "";
        public bool PolicyAvailable => _policy != 0;

        public Access(string? name)
        {
            _initialized = CoInitializeEx(0, 0);
            try
            {
                if (_initialized < 0 && _initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(_initialized);
                Marshal.ThrowExceptionForHR(CoCreateInstance(new("bcde0395-e52f-467c-8e3d-c4579291692e"), 0, 1,
                    new("a95664d2-9614-4f35-a746-de8db63617e6"), out _enumerator));
                var v = *(nint**)_enumerator;
                if (name is null or "auto")
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int, out nint, int>)v[4])(_enumerator, 0, 1, out _device));
                else
                {
                    var id = name.StartsWith("wasapi/", StringComparison.Ordinal) ? name[7..] : name;
                    if (!id.StartsWith("{0.0.0.00000000}.", StringComparison.Ordinal)) id = "{0.0.0.00000000}." + id;
                    fixed (char* key = id)
                        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, out nint, int>)v[5])(_enumerator, key, out _device));
                }
                v = *(nint**)_device;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, out nint, int>)v[5])(_device, out var idPointer));
                try { Id = Marshal.PtrToStringUni(idPointer) ?? ""; }
                finally { Marshal.FreeCoTaskMem(idPointer); }
                var iid = new Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, out nint, int>)v[3])(_device, &iid, 1, 0, out _client));
                CoCreateInstance(new("870af99c-171d-4f9e-af0d-e63df40c2bc9"), 0, 1,
                    new("f8679f50-850a-41cf-9c72-430f290290c8"), out _policy);
            }
            catch { Dispose(); throw; }
        }

        public byte[] MixBytes()
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(nint**)_client)[8])(_client, out var pointer));
            return CopyAndFree(pointer);
        }
        public WaveFormat Mix() => MemoryMarshal.Read<WaveFormat>(MixBytes());
        public byte[] DeviceBytes()
        {
            if (_policy == 0) throw new COMException("IPolicyConfig is unavailable.", unchecked((int)0x80004002));
            fixed (char* id = Id)
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, out nint, int>)(*(nint**)_policy)[4])(_policy, id, 0, out var pointer));
                return CopyAndFree(pointer);
            }
        }
        static byte[] CopyAndFree(nint pointer)
        {
            try
            {
                var length = 18 + ((WaveFormat*)pointer)->Extra;
                var bytes = new byte[Math.Max(length, sizeof(WaveFormat))];
                Marshal.Copy(pointer, bytes, 0, length);
                return bytes;
            }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        public WaveFormat? Find(int rate, int channels, WaveFormat? preferred = null)
        {
            if (channels is < 1 or > 8) return null;
            var mix = Mix();
            var query = (delegate* unmanaged[Stdcall]<nint, int, WaveFormat*, nint, int>)(*(nint**)_client)[7];
            if (preferred is { } original && original.Channels == channels)
            {
                original.Rate = (uint)rate;
                original.Bytes = checked((uint)rate * original.Align);
                if (query(_client, 1, &original, 0) == 0) return original;
            }
            foreach (var (bits, valid, floating) in new[] { (32, 32, false), (32, 24, false), (24, 24, false), (16, 16, false), (32, 32, true) })
            {
                var wave = new WaveFormat
                {
                    Tag = 0xfffe, Channels = (ushort)channels, Rate = (uint)rate, Bytes = (uint)(rate * channels * bits / 8),
                    Align = (ushort)(channels * bits / 8), Bits = (ushort)bits, Extra = 22, ValidBits = (ushort)valid,
                    Mask = channels == mix.Channels && mix.Tag == 0xfffe ? mix.Mask : ChannelMask(channels),
                    Subtype = floating ? new("00000003-0000-0010-8000-00aa00389b71") : new("00000001-0000-0010-8000-00aa00389b71")
                };
                if (query(_client, 1, &wave, 0) == 0) return wave;
                if (bits != valid || channels > 2) continue;
                wave.Tag = floating ? (ushort)3 : (ushort)1;
                wave.Extra = 0;
                if (query(_client, 1, &wave, 0) == 0) return wave;
            }
            return null;
        }
        public void Set(WaveFormat endpoint, byte[] mix)
        {
            var bytes = new byte[sizeof(WaveFormat)];
            MemoryMarshal.Write(bytes, in endpoint);
            SetBytes(bytes, mix);
        }
        public void SetBytes(byte[] endpoint, byte[] mix)
        {
            if (_policy == 0) throw new COMException("IPolicyConfig is unavailable.", unchecked((int)0x80004002));
            fixed (char* id = Id)
            fixed (byte* e = endpoint, m = mix)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, byte*, byte*, int>)(*(nint**)_policy)[6])(_policy, id, e, m));
        }
        public void Dispose()
        {
            if (_policy != 0) Marshal.Release(_policy);
            if (_client != 0) Marshal.Release(_client);
            if (_device != 0) Marshal.Release(_device);
            if (_enumerator != 0) Marshal.Release(_enumerator);
            _policy = _client = _device = _enumerator = 0;
            if (_initialized >= 0) CoUninitialize();
        }
    }

    internal static uint ChannelMask(int channels) => channels switch
    {
        1 => 4, 2 => 3, 3 => 7, 4 => 0x33, 5 => 0x37, 6 => 0x3f, 7 => 0x70f, 8 => 0x63f, _ => 0
    };

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint model);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);
}
