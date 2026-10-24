using JeekMusic.Library;

namespace JeekMusic.Playback;

public sealed record EndpointAudioFormat(int SampleRate, int ContainerBits, int ValidBits, int Channels, uint ChannelMask, string SampleKind)
{
    public string Layout => ChannelLayouts.Name(ChannelMask, Channels);
    public int IntegerPrecision => SampleKind == "float" ? 24 : ValidBits;
}

public sealed record SourceAudioFormat(int? SampleRate, int? Bits, int? Channels, uint? ChannelMask, string? Layout, string Precision)
{
    public static SourceAudioFormat FromTrack(Track? track) => new(track?.SampleRate,
        IntegerBits(track), track?.Channels,
        track?.Channels == 1 ? 4u : track?.Channels == 2 ? 3u : null,
        track?.Channels == 1 ? "mono" : track?.Channels == 2 ? "stereo" : null,
        PrecisionFor(track?.Codec, IntegerBits(track)));

    public static int? IntegerBits(Track? track, string? decodedCodec = null)
    {
        var codec = decodedCodec ?? track?.Codec;
        return !IsLossy(codec) && FloatingPrecision(codec) is null && codec?.ToLowerInvariant() is not ("ogg" or "wma") &&
            track?.BitsPerSample is >= 8 and <= 32 ? track.BitsPerSample : null;
    }

    public static bool IsLossy(string? codec) => codec?.ToLowerInvariant() is "mp3" or "mp2" or "aac" or "opus" or "vorbis" or
        "ac3" or "eac3" or "wmav1" or "wmav2" or "wmapro";
    public static string? FloatingPrecision(string? codec) =>
        codec?.StartsWith("pcm_f32", StringComparison.OrdinalIgnoreCase) == true ? "float32" :
        codec?.StartsWith("pcm_f64", StringComparison.OrdinalIgnoreCase) == true ? "float64" : null;
    public static string PrecisionFor(string? codec, int? bits) => IsLossy(codec) ? "lossy-not-applicable" :
        FloatingPrecision(codec) ?? (bits is not null ? "integer-metadata" : "unknown");
}

public static class ChannelLayouts
{
    // Explicit FFmpeg/mpv layouts, never infer a multichannel layout from count alone.
    public static uint? Mask(string? layout) => layout?.ToLowerInvariant() switch
    {
        "mono" => 4, "stereo" => 3, "2.1" => 0xb, "3.0" => 7, "3.0(back)" => 0x103,
        "quad" => 0x33, "quad(side)" => 0x603, "4.0" => 0x107, "5.0" => 0x37,
        "5.0(side)" => 0x607, "5.1" => 0x3f, "5.1(side)" => 0x60f,
        "6.1" => 0x70f, "7.1" => 0x63f, "7.1(wide)" => 0xff, _ => null
    };
    public static string Name(uint mask, int channels) => mask switch
    {
        4 when channels == 1 => "mono", 3 when channels == 2 => "stereo", 0x3f => "5.1", 0x60f => "5.1(side)",
        0x63f => "7.1", 0x33 => "quad", 0x603 => "quad(side)", _ => mask == 0 ? "unknown" : $"mask:0x{mask:X}"
    };
}

internal sealed record AudioFormatPlan(EndpointAudioFormat? Target, string? Conversion);

internal static class AudioFormatPolicy
{
    internal static AudioFormatPlan Choose(SourceAudioFormat source, EndpointAudioFormat current, IReadOnlyList<EndpointAudioFormat> supported)
    {
        if (source.SampleRate is not > 0) return new(null, "Source sample rate is unknown; keeping the Windows format.");
        var layouts = new List<(int Channels, uint Mask)>();
        if (source.ChannelMask is { } mask && source.Channels is > 0) layouts.Add((source.Channels.Value, mask));
        if (!layouts.Contains((current.Channels, current.ChannelMask))) layouts.Add((current.Channels, current.ChannelMask));
        EndpointAudioFormat? selected = null;
        // The rate is the first priority; every candidate is a complete driver-accepted tuple.
        foreach (var rate in new[] { source.SampleRate.Value, current.SampleRate }.Distinct())
        {
            foreach (var layout in layouts)
            {
                selected = supported.Where(f => f.SampleRate == rate && f.Channels == layout.Channels && f.ChannelMask == layout.Mask)
                    .OrderBy(f => PrecisionRank(source.Bits, f)).ThenBy(f => f.SampleKind == "pcm" ? 0 : 1)
                    .ThenBy(f => f.ContainerBits).FirstOrDefault();
                if (selected is not null) break;
            }
            if (selected is not null) break;
        }
        if (selected is null) return new(null, "No driver-supported complete format was found; keeping the Windows format.");
        var notes = new List<string>();
        if (selected.SampleRate != source.SampleRate) notes.Add($"Resampling {source.SampleRate} → {selected.SampleRate} Hz");
        if (source.ChannelMask is null)
        {
            notes.Add("Source layout unknown; preserving the current speaker layout");
            if (source.Channels is { } count && count != selected.Channels) notes.Add($"Channel conversion {count} → {selected.Channels} channels");
        }
        else if (source.ChannelMask != selected.ChannelMask || source.Channels != selected.Channels)
            notes.Add($"Channel conversion {source.Layout} → {selected.Layout}");
        if (source.Bits is { } bits && selected.IntegerPrecision < bits) notes.Add($"Precision conversion {bits} → {selected.IntegerPrecision} effective bits");
        return new(selected, notes.Count > 0 ? string.Join("; ", notes) : null);
    }

    internal static int PrecisionRank(int? sourceBits, EndpointAudioFormat format)
    {
        var bits = format.IntegerPrecision;
        if (sourceBits is > 0 and <= 16) return bits == 16 ? 0 : bits >= sourceBits ? 1 + bits : 100 - bits;
        var desired = sourceBits is > 0 ? sourceBits.Value : 24;
        return bits >= desired ? bits - desired : 100 + desired - bits;
    }
}
