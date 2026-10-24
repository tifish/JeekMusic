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
    public IDisposable? TryAcquireControl(string device, out string? diagnostic)
    {
        diagnostic = null;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes(device)));
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, @"Local\JeekMusic.AudioFormat." + key);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new FormatControlLease(mutex);
            diagnostic = "Another Jeek Music instance controls temporary formatting of this endpoint; keeping the actual Windows format.";
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        { diagnostic = "Cannot acquire endpoint format control: " + e.Message; }
        mutex?.Dispose();
        return null;
    }

    sealed class FormatControlLease(Mutex mutex) : IDisposable
    {
        bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    public DeviceAudioSnapshot? CaptureSnapshot(string device, out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            using var access = new Access(device);
            var deviceBytes = access.DeviceBytes();
            var mixBytes = access.MixBytes();
            var currentDevice = ToInfo(access.PropertyFormat());
            var currentMix = ToInfo(access.Mix());
            if (currentDevice != ToInfo(MemoryMarshal.Read<WaveFormat>(deviceBytes)) || currentMix != ToInfo(MemoryMarshal.Read<WaveFormat>(mixBytes)))
            {
                diagnostic = "Windows format changed while capturing its baseline; no temporary format was applied.";
                return null;
            }
            return new(access.Id, currentDevice, currentMix, deviceBytes, mixBytes);
        }
        catch (COMException e) { diagnostic = Failure("capture original Windows endpoint/mix format", e); return null; }
    }

    public DeviceFormatRestoreResult RestoreIfUnchanged(DeviceAudioSnapshot baseline, EndpointAudioFormat expectedDevice, EndpointAudioFormat expectedMix)
    {
        try
        {
            using var access = new Access(baseline.DeviceId);
            var currentDevice = ToInfo(access.PropertyFormat());
            var currentMix = ToInfo(access.Mix());
            if (currentDevice != expectedDevice || currentMix != expectedMix)
                return new("external-change", "Windows format changed externally; the old baseline was not restored.");
            if (currentDevice == baseline.Device && currentMix == baseline.Mix) return new("restored");
            access.SetBytes(baseline.DeviceBytes, baseline.MixBytes);
            using var independent = new Access(baseline.DeviceId);
            if (ToInfo(independent.PropertyFormat()) != baseline.Device || ToInfo(independent.Mix()) != baseline.Mix)
                return new("failed", "Original Windows endpoint/mix format readback did not match after restoration.");
            return new("restored");
        }
        catch (COMException e) { return new("failed", Failure("restore original Windows format", e)); }
    }

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

    public DeviceAudioCapabilities CaptureFormat(string? device, SourceAudioFormat source)
    {
        try
        {
            using var access = new Access(device);
            var mix = ToInfo(access.Mix());
            EndpointAudioFormat? actual = null;
            string? diagnostic = null;
            try { actual = ToInfo(access.PropertyFormat()); }
            catch (COMException e) { diagnostic = Failure("read endpoint device format", e); }
            var layouts = new List<(int Channels, uint Mask)> { (actual?.Channels ?? mix.Channels, actual?.ChannelMask ?? mix.ChannelMask) };
            if (source.ChannelMask is { } mask && source.Channels is > 0 && !layouts.Contains((source.Channels.Value, mask)))
                layouts.Insert(0, (source.Channels.Value, mask));
            var supported = new List<EndpointAudioFormat>();
            foreach (var rate in ProbeRates.Concat(new[] { source.SampleRate ?? 0, mix.SampleRate }).Where(r => r is > 0 and <= 1536000).Distinct().Order())
            foreach (var layout in layouts)
            foreach (var precision in new[] { (16, 16, "pcm"), (24, 24, "pcm"), (32, 24, "pcm"), (32, 32, "pcm"), (32, 32, "float") })
            {
                var candidate = new EndpointAudioFormat(rate, precision.Item1, precision.Item2, layout.Channels, layout.Mask, precision.Item3);
                if (access.Supports(ToWave(candidate))) supported.Add(candidate);
            }
            var canSet = access.PolicyAvailable && actual is not null;
            if (!access.PolicyAvailable) diagnostic ??= "IPolicyConfig unavailable; the Windows endpoint format cannot be changed.";
            var sharedRates = supported.Select(f => f.SampleRate).Distinct().Order().ToArray();
            return new(access.Id, mix.SampleRate, mix.Channels, canSet ? sharedRates : [], sharedRates, canSet, diagnostic,
                actual, mix, supported);
        }
        catch (COMException e) { return new(null, null, null, [], [], false, Failure("query complete device formats", e)); }
    }

    public bool SetSharedFormat(string device, EndpointAudioFormat format, out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            using var access = new Access(device);
            var originalMix = access.MixBytes();
            var originalEndpoint = access.DeviceBytes();
            var accepted = access.Accept(ToWave(format));
            if (accepted is null) { diagnostic = "Driver rejected the complete endpoint format tuple."; return false; }
            var wave = accepted.Value;
            var current = ToInfo(access.PropertyFormat());
            var currentMix = ToInfo(MemoryMarshal.Read<WaveFormat>(originalMix));
            if (current == format && MixMatches(currentMix, format)) return true;
            var mix = ToWave(format with { ContainerBits = 32, ValidBits = 32, SampleKind = "float" });
            var mixBytes = new byte[sizeof(WaveFormat)];
            MemoryMarshal.Write(mixBytes, in mix);
            try
            {
                access.Set(wave, mixBytes);
                using var independent = new Access(device);
                // Device bits/layout come from documented IPropertyStore PKEY_AudioEngine_DeviceFormat,
                // independently of both mpv's client float stream and IPolicyConfig's write interface.
                var actual = ToInfo(independent.PropertyFormat());
                var actualMix = ToInfo(independent.Mix());
                if (actual != format || !MixMatches(actualMix, format))
                    throw new COMException($"Device/mix readback mismatch; device={actual}; mix={actualMix}.");
                return true;
            }
            catch (COMException e)
            {
                diagnostic = Failure("set complete shared format", e);
                try { access.SetBytes(originalEndpoint, originalMix); }
                catch (COMException rollback) { diagnostic += "; rollback failed: " + Failure("restore complete format", rollback); }
                return false;
            }
        }
        catch (COMException e) { diagnostic = Failure("set complete shared format", e); return false; }
    }

    static bool MixMatches(EndpointAudioFormat mix, EndpointAudioFormat device) =>
        mix.SampleRate == device.SampleRate && mix.Channels == device.Channels && mix.ChannelMask == device.ChannelMask;

    static EndpointAudioFormat ToInfo(WaveFormat wave) => new((int)wave.Rate, wave.Bits,
        wave.Tag == 0xfffe && wave.ValidBits > 0 ? wave.ValidBits : wave.Bits, wave.Channels,
        wave.Tag == 0xfffe ? wave.Mask : wave.Channels == 1 ? 4u : wave.Channels == 2 ? 3u : 0,
        wave.Tag == 3 || wave.Tag == 0xfffe && wave.Subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") ? "float" : "pcm");

    static WaveFormat ToWave(EndpointAudioFormat format) => new()
    {
        Tag = 0xfffe, Extra = 22, Rate = (uint)format.SampleRate, Channels = (ushort)format.Channels,
        Bits = (ushort)format.ContainerBits, ValidBits = (ushort)format.ValidBits, Mask = format.ChannelMask,
        Align = (ushort)(format.Channels * format.ContainerBits / 8), Bytes = (uint)(format.SampleRate * format.Channels * format.ContainerBits / 8),
        Subtype = format.SampleKind == "float" ? new("00000003-0000-0010-8000-00aa00389b71") : new("00000001-0000-0010-8000-00aa00389b71")
    };

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
        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public uint Size;
            [FieldOffset(16)] public nint Data;
        }
        public WaveFormat PropertyFormat()
        {
            nint store = 0;
            var value = new PropVariant();
            try
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, out nint, int>)(*(nint**)_device)[4])(_device, 0, out store));
                var key = new PropertyKey { Format = new("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), Id = 0 };
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)(*(nint**)store)[5])(store, &key, &value));
                if (value.Type != 65 || value.Data == 0 || value.Size < 18) throw new COMException("Endpoint device format is not a WAVEFORMAT blob.");
                var bytes = new byte[Math.Max((int)value.Size, sizeof(WaveFormat))];
                Marshal.Copy(value.Data, bytes, 0, (int)value.Size);
                return MemoryMarshal.Read<WaveFormat>(bytes);
            }
            finally
            {
                PropVariantClear((nint)(&value));
                if (store != 0) Marshal.Release(store);
            }
        }

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
        public bool Supports(WaveFormat wave) => Accept(wave) is not null;
        public WaveFormat? Accept(WaveFormat wave)
        {
            var query = (delegate* unmanaged[Stdcall]<nint, int, WaveFormat*, nint, int>)(*(nint**)_client)[7];
            if (query(_client, 1, &wave, 0) == 0) return wave;
            if (wave.Channels > 2 || wave.Bits != wave.ValidBits) return null;
            wave.Tag = wave.Subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : (ushort)1;
            wave.Extra = 0;
            return query(_client, 1, &wave, 0) == 0 ? wave : null;
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

    [LibraryImport("ole32.dll")] private static partial int PropVariantClear(nint value);
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint model);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);
}
