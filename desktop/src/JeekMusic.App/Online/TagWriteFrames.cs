using System.Buffers.Binary;
using System.Text;

namespace JeekMusic.Online;

/// <summary>Bounded envelope editing, not an audio/tag model. Unknown records are opaque bytes.
/// Only APIC/USLT are encoded here; no ID3 version migration or text-tag normalization.</summary>
internal static partial class TagWriteFrames
{
    internal const int MaxFileBytes = 256 * 1024 * 1024;
    const int MaxTagBytes = 32 * 1024 * 1024;
    internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    static readonly Encoding Utf16 = new UnicodeEncoding(false, true, true);
    internal sealed record Record(string Id, byte[] Raw, byte[] Body);
    internal sealed record Id3(int Version, int End, List<Record> Frames);

    internal static byte[] Read(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw Unsafe("file exceeds bounded edit limit (256 MiB)");
        return File.ReadAllBytes(path);
    }
    internal static NotSupportedException Unsafe(string reason) => new("Unsafe tag write: " + reason);
    internal static int Number(byte[] b, int p, bool big = true)
    {
        if (p < 0 || p > b.Length - 4) throw Unsafe("truncated integer");
        var n = big ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
        return n <= int.MaxValue ? (int)n : throw Unsafe("oversized record");
    }
    internal static byte[] Size(int n, bool big = true)
    {
        var b = new byte[4];
        if (big) BinaryPrimitives.WriteInt32BigEndian(b, n); else BinaryPrimitives.WriteInt32LittleEndian(b, n);
        return b;
    }
    static int Sync(byte[] b, int p)
    {
        if (p > b.Length - 4 || b.AsSpan(p, 4).ContainsAnyInRange((byte)128, byte.MaxValue)) throw Unsafe("invalid synchsafe size");
        return b[p] << 21 | b[p + 1] << 14 | b[p + 2] << 7 | b[p + 3];
    }
    static byte[] Sync(int n) => [(byte)(n >> 21 & 127), (byte)(n >> 14 & 127), (byte)(n >> 7 & 127), (byte)(n & 127)];
    internal static Id3 ParseId3(byte[] b, bool allowAbsent = false)
    {
        if (b.Length < 3 || !b.AsSpan(0, 3).SequenceEqual("ID3"u8))
            return allowAbsent ? new(4, 0, []) : throw Unsafe("missing ID3 envelope");
        if (b.Length < 10 || b[3] is not (3 or 4) || b[4] != 0 || b[5] != 0)
            throw Unsafe("ID3 requires v2.3/v2.4 without extended header, global unsync or footer");
        var end = checked(10 + Sync(b, 6));
        if (end > b.Length || end > MaxTagBytes) throw Unsafe("invalid/oversized ID3 tag");
        var frames = new List<Record>(); var p = 10;
        while (p < end && b[p] != 0)
        {
            if (p > end - 10 || b.AsSpan(p, 4).ContainsAnyExceptInRange((byte)'0', (byte)'Z')) throw Unsafe("invalid ID3 frame");
            var id = Encoding.ASCII.GetString(b, p, 4);
            if (id.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))) throw Unsafe("invalid ID3 frame identifier");
            var n = b[3] == 4 ? Sync(b, p + 4) : Number(b, p + 4);
            if (n < 1 || n > end - p - 10) throw Unsafe("invalid ID3 frame length");
            // Such frames require discard-on-tag-alteration; preserving them would violate the spec.
            if ((b[p + 8] & (b[3] == 4 ? 0x40 : 0x80)) != 0) throw Unsafe("discard-on-tag-alteration frame");
            frames.Add(new(id, b[p..(p + 10 + n)], b[(p + 10)..(p + 10 + n)])); p += 10 + n;
        }
        if (b.AsSpan(p, end - p).ContainsAnyExcept((byte)0)) throw Unsafe("nonzero ID3 padding");
        return new(b[3], end, frames);
    }
    static Record Frame(string id, byte[] body, int version) => new(id,
        [..Encoding.ASCII.GetBytes(id), ..(version == 4 ? Sync(body.Length) : Size(body.Length)), 0, 0, ..body], body);
    static byte[] Encoded(string text, int version) => version == 4 ? Utf8.GetBytes(text) : [0xff, 0xfe, ..Utf16.GetBytes(text)];
    static int PictureType(Record f)
    {
        if (f.Raw[9] != 0) throw Unsafe("encoded/grouped/compressed APIC cannot be safely classified");
        var end = Array.IndexOf(f.Body, (byte)0, 1);
        if (end < 1 || end + 1 >= f.Body.Length) throw Unsafe("invalid APIC MIME");
        return f.Body[end + 1];
    }
    internal static byte[] EditId3(byte[] source, byte[]? cover, string? lyrics)
    {
        var tag = ParseId3(source, true);
        // Reject a stacked leading tag, rather than emit competing versions. Audio bytes are opaque.
        if (source.Length - tag.End >= 3 && source.AsSpan(tag.End, 3).SequenceEqual("ID3"u8)) throw Unsafe("stacked ID3 tags");
        var frames = tag.Frames; var version = tag.Version;
        if (lyrics is not null && (frames.Count(f => f.Id == "USLT") > 1 || frames.Any(f => f.Id == "SYLT")))
            throw Unsafe("multiple lyric slots or SYLT require explicit slot selection");
        var replacement = new List<Record>();
        if (cover is not null)
        {
            // Only our supplied image is serialized. All other pictures remain original opaque frames.
            byte[] description = version == 4 ? [0] : [0xff, 0xfe, 0, 0];
            replacement.Add(Frame("APIC", [(byte)(version == 4 ? 3 : 1), .."image/jpeg\0"u8, 3, ..description, ..cover], version));
        }
        if (lyrics is not null)
        {
            var old = frames.SingleOrDefault(f => f.Id == "USLT");
            byte[] language = "eng"u8.ToArray();
            if (old is not null)
            {
                if (old.Raw[9] != 0 || old.Body.Length < 5) throw Unsafe("complex USLT slot");
                language = old.Body[1..4];
                // A nonempty description identifies a separate slot. Do not silently discard it.
                var b = old.Body; var start = b[0] == 1 ? 6 : 4;
                if (b[0] == 1 && (b.Length < 8 || !b.AsSpan(4, 2).SequenceEqual(new byte[] { 255, 254 }))) throw Unsafe("unsupported USLT description BOM");
                if (start >= b.Length || b[start] != 0 || (b[0] is 1 or 2 && (start + 1 >= b.Length || b[start + 1] != 0))) throw Unsafe("named lyric slot");
            }
            byte[] delimiter = version == 4 ? [0] : [0xff, 0xfe, 0, 0];
            replacement.Add(Frame("USLT", [(byte)(version == 4 ? 3 : 1), ..language, ..delimiter, ..Encoded(lyrics, version)], version));
        }
        bool Target(Record f) => (lyrics is not null && f.Id == "USLT") || (cover is not null && f.Id == "APIC" && PictureType(f) is 0 or 3);
        if (frames.Any(f => Target(f) && (f.Raw[8] & (version == 4 ? 0x10 : 0x20)) != 0)) throw Unsafe("read-only ID3 target frame");
        using var output = new MemoryStream();
        // Preserve the order and complete raw bytes of every non-target frame, including unknown frames.
        foreach (var frame in frames) if (!Target(frame)) output.Write(frame.Raw);
        foreach (var frame in replacement) output.Write(frame.Raw);
        var length = Math.Max(tag.End > 0 ? tag.End - 10 : 0, checked((int)output.Length));
        if (length > MaxTagBytes) throw Unsafe("ID3 metadata exceeds limit");
        var result = new byte[checked(10 + length + source.Length - tag.End)];
        "ID3"u8.CopyTo(result); result[3] = (byte)version; Sync(length).CopyTo(result, 6);
        output.GetBuffer().AsSpan(0, (int)output.Length).CopyTo(result.AsSpan(10));
        source.AsSpan(tag.End).CopyTo(result.AsSpan(10 + length));
        var after = ParseId3(result);
        if (!frames.Where(f => !Target(f)).Select(f => Convert.ToHexString(f.Raw)).SequenceEqual(after.Frames.Where(f => !Target(f)).Select(f => Convert.ToHexString(f.Raw))) || !source.AsSpan(tag.End).SequenceEqual(result.AsSpan(after.End)))
            throw Unsafe("ID3 non-target preservation failed");
        return result;
    }
    internal static byte[] EditRiff(byte[] source, byte[]? cover, string? lyrics)
    {
        var big = source.AsSpan(0, 4).SequenceEqual("FORM"u8);
        var kind = Encoding.ASCII.GetString(source, 8, 4);
        if ((!big && kind != "WAVE") || (big && kind is not ("AIFF" or "AIFC")) || Number(source, 4, big) != source.Length - 8) throw Unsafe("unsupported RIFF/FORM envelope");
        using var output = new MemoryStream(); output.Write(source.AsSpan(0, 12));
        var found = false;
        for (var p = 12; p < source.Length;)
        {
            if (p > source.Length - 8) throw Unsafe("truncated chunk");
            var id = Encoding.ASCII.GetString(source, p, 4); var n = Number(source, p + 4, big);
            if (n > source.Length - p - 8 - (n & 1)) throw Unsafe("truncated chunk payload");
            if (id.Equals("id3 ", StringComparison.OrdinalIgnoreCase))
            {
                if (found) throw Unsafe("multiple embedded ID3 chunks"); found = true;
                var edited = EditId3(source[(p + 8)..(p + 8 + n)], cover, lyrics);
                output.Write(source.AsSpan(p, 4)); output.Write(Size(edited.Length, big)); output.Write(edited); if ((edited.Length & 1) != 0) output.WriteByte(0);
            }
            else output.Write(source.AsSpan(p, 8 + n + (n & 1)));
            p += 8 + n + (n & 1);
        }
        if (!found)
        {
            var edited = EditId3([], cover, lyrics); output.Write(big ? "ID3 "u8 : "id3 "u8); output.Write(Size(edited.Length, big)); output.Write(edited); if ((edited.Length & 1) != 0) output.WriteByte(0);
        }
        var result = output.ToArray(); Size(result.Length - 8, big).CopyTo(result, 4); return result;
    }
}
