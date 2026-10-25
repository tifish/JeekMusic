using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace JeekMusic.Online;

/// <summary>Checks native container boundaries around ATL's staged output. Never repairs unknown
/// records or infers lost text. An unsupported/changeable envelope fails before source replacement.</summary>
internal static partial class TagWriteFrames
{
    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
    static void Equal(IEnumerable<string> a, IEnumerable<string> b, string reason)
    {
        if (!a.SequenceEqual(b)) throw Unsafe(reason);
    }
    internal static void CheckOgg(byte[] before, byte[] after, bool cover, bool lyrics)
    {
        var a = Ogg(before); var b = Ogg(after);
        Equal(a.Packets.Where((_, i) => i != 1).Select(Hash), b.Packets.Where((_, i) => i != 1).Select(Hash), "Ogg codec/audio packet changed");
        Equal(a.Granules, b.Granules, "Ogg granule timeline changed");
        if (a.Serial != b.Serial) throw Unsafe("Ogg stream serial changed");
        var opus = a.Packets[0].AsSpan().StartsWith("OpusHead"u8); var offset = opus ? 8 : 7;
        var prefix = opus ? "OpusTags"u8.ToArray() : new byte[] { 3, 118, 111, 114, 98, 105, 115 };
        if (!a.Packets[1].AsSpan().StartsWith(prefix) || !b.Packets[1].AsSpan().StartsWith(prefix)) throw Unsafe("unsupported Ogg codec comments");
        var ca = ParseComments(a.Packets[1][offset..]); var cb = ParseComments(b.Packets[1][offset..]);
        if (!opus && (ca.Suffix.Length != 1 || ca.Suffix[0] != 1 || cb.Suffix.Length != 1 || cb.Suffix[0] != 1)) throw Unsafe("invalid Vorbis framing bit");
        // An Opus binary suffix has application-specific meaning; preserve it too.
        if (!ca.Vendor.AsSpan().SequenceEqual(cb.Vendor) || !ca.Suffix.AsSpan().SequenceEqual(cb.Suffix)) throw Unsafe("Ogg vendor/binary suffix changed");
        bool Target(byte[] field)
        {
            var key = Key(field);
            if (lyrics && key == "LYRICS") return true;
            if (cover && key is "COVERART" or "COVERARTMIME") throw Unsafe("legacy Ogg cover representation");
            if (!cover || key != "METADATA_BLOCK_PICTURE") return false;
            byte[] picture;
            try { picture = Convert.FromBase64String(Encoding.ASCII.GetString(field[(Array.IndexOf(field, (byte)'=') + 1)..])); }
            catch (FormatException) { throw Unsafe("invalid Ogg picture vector"); }
            return Number(picture, 0) is 0 or 3;
        }
        // Field-name case and order between different keys are not significant in Vorbis.
        // Preserve the ordered values of every individual key, including repeated ARTIST.
        static IEnumerable<string> Values(IEnumerable<byte[]> fields) => fields
            .Select(f => (Key: Key(f), Value: Hash(f[(Array.IndexOf(f, (byte)'=') + 1)..])))
            .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + ":" + x.Value);
        Equal(Values(ca.Fields.Where(f => !Target(f))), Values(cb.Fields.Where(f => !Target(f))), "Ogg non-target comments/multivalues changed");
    }
    internal sealed record OggData(List<byte[]> Packets, List<string> Granules, uint Serial);
    internal static OggData Ogg(byte[] b)
    {
        var packets = new List<byte[]>(); var granules = new List<string>(); using var packet = new MemoryStream();
        uint? serial = null; uint sequence = 0;
        for (var p = 0; p < b.Length;)
        {
            if (p > b.Length - 27 || !b.AsSpan(p, 4).SequenceEqual("OggS"u8) || b[p + 4] != 0) throw Unsafe("invalid Ogg page");
            var id = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 14)); serial ??= id;
            if (id != serial || BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 18)) != sequence++) throw Unsafe("chained/multiplexed Ogg");
            if (((b[p + 5] & 1) != 0) != (packet.Length > 0)) throw Unsafe("invalid Ogg continuation");
            var flags = b[p + 5];
            if ((flags & ~7) != 0 || ((flags & 2) != 0) != (p == 0)) throw Unsafe("invalid Ogg page flags/BOS");
            var count = b[p + 26]; var start = p + 27 + count;
            if (start > b.Length) throw Unsafe("truncated Ogg lacing");
            var completedOnPage = 0;
            for (var i = 0; i < count; i++)
            {
                var n = b[p + 27 + i]; if (n > b.Length - start) throw Unsafe("truncated Ogg packet");
                packet.Write(b, start, n); start += n;
                if (packet.Length > MaxTagBytes) throw Unsafe("oversized Ogg packet");
                if (n < 255) { packets.Add(packet.ToArray()); packet.SetLength(0); completedOnPage++; }
            }
            var granule = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p + 6));
            if ((completedOnPage == 0 && granule != -1) || (completedOnPage > 0 && granule < 0)) throw Unsafe("Ogg granule disagrees with packet completion");
            if (((flags & 4) != 0) != (start == b.Length) || ((flags & 4) != 0 && packet.Length != 0)) throw Unsafe("invalid Ogg EOS/incomplete packet");
            uint crc = 0;
            for (var i = p; i < start; i++) crc = crc << 8 ^ OggCrcTable[(crc >> 24) ^ (uint)(i >= p + 22 && i < p + 26 ? 0 : b[i])];
            if (crc != BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 22))) throw Unsafe("Ogg checksum mismatch");
            if (packets.Count > 2) granules.Add(packets.Count + ":" + Convert.ToHexString(b.AsSpan(p + 6, 8)));
            p = start;
        }
        if (packet.Length != 0 || packets.Count < 3 || serial is null) throw Unsafe("incomplete Ogg stream");
        var opus = packets[0].AsSpan().StartsWith("OpusHead"u8);
        if (!opus && !packets[0].AsSpan().StartsWith(new byte[] { 1, 118, 111, 114, 98, 105, 115 })) throw Unsafe("unsupported Ogg codec");
        return new(packets, granules, serial.Value);
    }
    static readonly uint[] OggCrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        var r = (uint)i << 24; for (var n = 0; n < 8; n++) r = r << 1 ^ ((r & 0x80000000) != 0 ? 0x04c11db7u : 0); return r;
    }).ToArray();

    internal static byte[] EditOgg(byte[] source, byte[]? cover, string? lyrics)
    {
        var data = Ogg(source); var opus = data.Packets[0].AsSpan().StartsWith("OpusHead"u8);
        if (!opus && !data.Packets[0].AsSpan().StartsWith(new byte[] { 1, 118, 111, 114, 98, 105, 115 })) throw Unsafe("unsupported Ogg codec");
        var headerPackets = opus ? 2 : 3; var prefix = opus ? "OpusTags"u8.ToArray() : new byte[] { 3, 118, 111, 114, 98, 105, 115 };
        if (!data.Packets[1].AsSpan().StartsWith(prefix)) throw Unsafe("invalid Ogg comment packet");
        var comments = ParseComments(data.Packets[1][prefix.Length..]);
        if (lyrics is not null && comments.Fields.Count(f => Key(f) == "LYRICS") > 1) throw Unsafe("multiple Ogg lyric slots");
        var kept = new List<byte[]>();
        foreach (var field in comments.Fields)
        {
            var key = Key(field);
            if (lyrics is not null && key == "LYRICS") continue;
            if (cover is not null && key is "COVERART" or "COVERARTMIME") throw Unsafe("legacy Ogg cover representation");
            if (cover is not null && key == "METADATA_BLOCK_PICTURE")
            {
                byte[] picture;
                try { picture = Convert.FromBase64String(Encoding.ASCII.GetString(field[(Array.IndexOf(field, (byte)'=') + 1)..])); }
                catch (FormatException) { throw Unsafe("invalid Ogg picture"); }
                if (Number(picture, 0) is 0 or 3) continue;
            }
            kept.Add(field);
        }
        if (lyrics is not null) kept.Add([.."LYRICS="u8, ..Utf8.GetBytes(lyrics)]);
        if (cover is not null) kept.Add([.."METADATA_BLOCK_PICTURE="u8, ..Encoding.ASCII.GetBytes(Convert.ToBase64String(Picture(cover)))]);
        List<byte[]> packetBytes = [[..prefix, ..EncodeComments(comments with { Fields = kept })]];
        if (!opus) packetBytes.Add(data.Packets[2]);
        // This deliberately supports header pages separate from audio, not a universal Ogg muxer.
        var firstEnd = 0; var audioStart = 0; var completed = 0;
        for (var p = 0; p < source.Length;)
        {
            var count = source[p + 26]; var end = p + 27 + count;
            for (var i = 0; i < count; i++) { var n = source[p + 27 + i]; end += n; if (n < 255) completed++; }
            if (p == 0) { if (completed != 1 || count == 0 || source[p + 26 + count] == 255) throw Unsafe("Ogg identification page must be separate"); firstEnd = end; }
            if (completed >= headerPackets)
            {
                if (completed != headerPackets || count == 0 || source[p + 26 + count] == 255) throw Unsafe("Ogg audio shares header page");
                audioStart = end; break;
            }
            p = end;
        }
        if (audioStart == 0 || audioStart >= source.Length) throw Unsafe("missing separate Ogg audio pages");
        using var output = new MemoryStream(); output.Write(source.AsSpan(0, firstEnd)); uint sequence = 1;
        void Page(byte[] page)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence++); page.AsSpan(22, 4).Clear(); uint crc = 0;
            foreach (var value in page) crc = crc << 8 ^ OggCrcTable[(crc >> 24) ^ value];
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), crc); output.Write(page);
        }
        var segments = new List<byte>(); using var payload = new MemoryStream(); var continued = false;
        void Flush(bool continuationNext)
        {
            var page = new byte[27 + segments.Count + payload.Length]; "OggS"u8.CopyTo(page); page[5] = (byte)(continued ? 1 : 0);
            // A page consisting solely of continuation segments has no completed packet.
            BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), segments.Any(n => n < 255) ? 0 : -1);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), data.Serial); page[26] = (byte)segments.Count;
            segments.ToArray().CopyTo(page, 27); payload.GetBuffer().AsSpan(0, (int)payload.Length).CopyTo(page.AsSpan(27 + segments.Count));
            Page(page); segments.Clear(); payload.SetLength(0); continued = continuationNext;
        }
        foreach (var packet in packetBytes)
        {
            for (var pos = 0; pos <= packet.Length;)
            {
                var n = Math.Min(255, packet.Length - pos); segments.Add((byte)n); payload.Write(packet, pos, n); pos += n;
                if (segments.Count == 255) Flush(n == 255);
                if (n < 255) break;
            }
        }
        if (segments.Count != 0) Flush(false);
        for (var p = audioStart; p < source.Length;)
        {
            var count = source[p + 26]; var end = p + 27 + count; for (var i = 0; i < count; i++) end += source[p + 27 + i];
            Page(source[p..end]); p = end;
        }
        var result = output.ToArray(); CheckOgg(source, result, cover is not null, lyrics is not null); return result;
    }

    internal static void CheckMp4(byte[] before, byte[] after, bool cover, bool lyrics)
    {
        var a = Mp4(before, cover, lyrics); var b = Mp4(after, cover, lyrics);
        if (!a.Any(s => s.Contains("/ilst:container:", StringComparison.Ordinal)))
        {
            var hasHandler = a.Any(s => s.Contains("/meta/hdlr:", StringComparison.Ordinal));
            bool NewStructure(string s) => s.Contains("/udta:container:", StringComparison.Ordinal) || s.Contains("/meta:container:", StringComparison.Ordinal) || s.Contains("/ilst:container:", StringComparison.Ordinal) || (!hasHandler && s.Contains("/meta/hdlr:", StringComparison.Ordinal));
            a = a.Where(s => !NewStructure(s)).ToList(); b = b.Where(s => !NewStructure(s)).ToList();
        }
        Equal(a, b, "MP4 non-target atom/typed value/audio or sample location changed");
    }
    static List<string> Mp4(byte[] b, bool cover, bool lyrics)
    {
        var result = new List<string>(); var mdats = new List<(long Start, long End)>();
        long Large(int p) { var n = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p, 8)); return n <= int.MaxValue ? (long)n : throw Unsafe("large MP4 atom"); }
        List<(string Type, int Start, int Header, int End)> Atoms(int start, int end)
        {
            var atoms = new List<(string, int, int, int)>();
            for (var p = start; p < end;)
            {
                if (p > end - 8) throw Unsafe("truncated MP4 atom");
                var n = Number(b, p); var header = 8;
                if (n == 1) { if (p > end - 16) throw Unsafe("truncated large MP4 atom"); n = (int)Large(p + 8); header = 16; }
                if (n == 0) n = end - p;
                if (n < header || n > end - p) throw Unsafe("invalid MP4 atom length");
                atoms.Add((Encoding.Latin1.GetString(b, p + 4, 4), p, header, p + n)); p += n;
            }
            return atoms;
        }
        var top = Atoms(0, b.Length);
        if (top.Count(x => x.Type == "moov") != 1 || top.Any(x => x.Type == "moof")) throw Unsafe("fragmented/ambiguous MP4");
        foreach (var atom in top.Where(x => x.Type == "mdat")) mdats.Add((atom.Start + atom.Header, atom.End));
        if (mdats.Count != 1) throw Unsafe("MP4 requires one bounded media-data atom");
        void Walk(int start, int end, string path, int depth)
        {
            if (depth > 12) throw Unsafe("deep MP4 atom nesting");
            var children = Atoms(start, end);
            // ilst property order is immaterial; retain the order within repeated property types.
            if (path.EndsWith("/ilst", StringComparison.Ordinal)) children = children.OrderBy(a => a.Type, StringComparer.Ordinal).ToList();
            foreach (var atom in children)
            {
                var (type, p, h, e) = atom; var key = path + "/" + type; var body = b[(p + h)..e];
                if (type == "mvex") throw Unsafe("fragmented MP4 movie");
                if (path.EndsWith("/ilst", StringComparison.Ordinal) && ((cover && type == "covr") || (lyrics && type == "©lyr"))) continue;
                if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "udta" or "ilst" or "meta")
                {
                    var prefix = type == "meta" ? 4 : 0;
                    if (body.Length < prefix) throw Unsafe("invalid MP4 meta");
                    if (prefix == 4 && body.AsSpan(0, 4).ContainsAnyExcept((byte)0)) throw Unsafe("unsupported MP4 meta version/flags");
                    result.Add(key + ":container:" + Convert.ToHexString(body.AsSpan(0, prefix))); Walk(p + h + prefix, e, key, depth + 1);
                }
                else if (type is "stco" or "co64")
                {
                    if (body.Length < 8) throw Unsafe("invalid MP4 chunk table"); var count = Number(body, 4); var width = type == "co64" ? 8 : 4;
                    if (count > (body.Length - 8) / width || body.Length != 8 + count * width) throw Unsafe("invalid MP4 chunk count");
                    result.Add(key + ":" + Convert.ToHexString(body.AsSpan(0, 4)));
                    for (var i = 0; i < count; i++)
                    {
                        var offset = width == 4 ? Number(body, 8 + i * width) : checked((long)BinaryPrimitives.ReadUInt64BigEndian(body.AsSpan(8 + i * width)));
                        if (offset < mdats[0].Start || offset >= mdats[0].End) throw Unsafe("MP4 external sample data");
                        result.Add(key + ":relative:" + (offset - mdats[0].Start));
                    }
                }
                else if (type == "free" && body.AsSpan().IndexOfAnyExcept((byte)0) < 0) { /* zero padding only */ }
                else result.Add(key + ":" + Hash(body));
            }
        }
        Walk(0, b.Length, "", 0); return result;
    }

    internal static void CheckAsf(byte[] before, byte[] after, bool cover, bool lyrics) => Equal(Asf(before, cover, lyrics), Asf(after, cover, lyrics), "ASF non-target descriptor/object/audio changed");
    internal static void UpdateAsfFileSize(byte[] before, byte[] after)
    {
        static int Position(byte[] b)
        {
            if (b.Length < 30) throw Unsafe("truncated ASF header");
            var end = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16));
            if (end > (ulong)b.Length || end > MaxTagBytes) throw Unsafe("invalid ASF header");
            for (var p = 30; p < (int)end;)
            {
                if (p > (int)end - 24) throw Unsafe("truncated ASF object");
                var n = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p + 16));
                if (n < 24 || n > end - (ulong)p) throw Unsafe("invalid ASF object size");
                if (new Guid(b.AsSpan(p, 16)) == Guid.Parse("8CABDCA1-A947-11CF-8EE4-00C00C205365"))
                { if (n != 104) throw Unsafe("invalid ASF file properties"); return p + 40; }
                p += (int)n;
            }
            throw Unsafe("missing ASF file properties");
        }
        var a = Position(before); var b = Position(after); var old = BinaryPrimitives.ReadUInt64LittleEndian(before.AsSpan(a)); var produced = BinaryPrimitives.ReadUInt64LittleEndian(after.AsSpan(b));
        if (old != (ulong)before.Length || (produced != old && produced != (ulong)after.Length)) throw Unsafe("inconsistent ASF file length");
        // ATL's native partial writer leaves this structural length stale. No metadata field
        // is inferred: the only permitted adjustment is the independently known file size.
        BinaryPrimitives.WriteUInt64LittleEndian(after.AsSpan(b), (ulong)after.Length);
    }
    static List<string> Asf(byte[] b, bool cover, bool lyrics)
    {
        var result = new List<string>();
        static bool Is(Guid a, string s) => a == Guid.Parse(s);
        long Length(int p) { if (p > b.Length - 8) throw Unsafe("truncated ASF size"); var n = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)); return n <= int.MaxValue ? (long)n : throw Unsafe("large ASF object"); }
        bool Target(string name, byte[] value)
        {
            if (lyrics && name == "WM/Lyrics_Synchronised") throw Unsafe("ASF synchronized lyric slot");
            if (lyrics && name == "WM/Lyrics") return true;
            if (cover && name == "WM/Picture") { if (value.Length < 1) throw Unsafe("invalid ASF picture"); return value[0] is 0 or 3; }
            return false;
        }
        void Objects(int start, int end, int depth)
        {
            if (depth > 2) throw Unsafe("nested ASF objects");
            for (var p = start; p < end;)
            {
                if (p > end - 24) throw Unsafe("truncated ASF object"); var id = new Guid(b.AsSpan(p, 16)); var size = Length(p + 16);
                if (size < 24 || size > end - p) throw Unsafe("invalid ASF object size"); var e = checked(p + (int)size); var payload = p + 24;
                if (Is(id, "5FBF03B5-A92E-11CF-8EE3-00C00C205365"))
                {
                    if (payload > e - 22 || Number(b, payload + 18, false) != e - payload - 22) throw Unsafe("invalid ASF header extension");
                    result.Add(id + ":" + Hash(b[payload..(payload + 18)])); Objects(payload + 22, e, depth + 1);
                }
                else if (Is(id, "D2D0A440-E307-11D2-97F0-00A0C95EA850") || Is(id, "C5F8CBEA-5BAF-4877-8467-AA8C44FA4CCA") || Is(id, "44231C94-9498-49D1-A141-1D134E457054"))
                {
                    var extended = Is(id, "D2D0A440-E307-11D2-97F0-00A0C95EA850"); var q = payload;
                    int Word() { if (q > e - 2) throw Unsafe("truncated ASF descriptor"); var n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(q)); q += 2; return n; }
                    var count = Word();
                    for (var i = 0; i < count; i++)
                    {
                        var begin = q; if (!extended) { Word(); Word(); } var nameLength = Word(); var type = !extended ? Word() : 0; var valueLength = !extended ? Number(b, q, false) : 0; if (!extended) q += 4;
                        if ((nameLength & 1) != 0 || nameLength > e - q) throw Unsafe("invalid ASF name"); var name = Encoding.Unicode.GetString(b, q, nameLength).TrimEnd('\0'); q += nameLength;
                        if (extended) { type = Word(); valueLength = Word(); }
                        if (valueLength > e - q) throw Unsafe("truncated ASF value"); var value = b[q..(q + valueLength)]; q += valueLength;
                        if (!Target(name, value)) result.Add(id + ":" + Hash(b[begin..q]));
                    }
                    if (q != e) throw Unsafe("ASF descriptor trailing data");
                }
                else if (Is(id, "1806D474-CADF-4509-A4BA-9AABCB96AAE8") && b.AsSpan(payload, e - payload).IndexOfAnyExcept((byte)0) < 0) { }
                else if (Is(id, "8CABDCA1-A947-11CF-8EE4-00C00C205365"))
                {
                    if (e - payload < 80 || Length(payload + 16) != b.Length) throw Unsafe("unsupported ASF file properties");
                    result.Add(id + ":" + Hash([..b[payload..(payload + 16)], ..b[(payload + 24)..e]]));
                }
                else result.Add(id + ":" + Hash(b[payload..e]));
                p = e;
            }
        }
        if (b.Length < 30 || !Is(new Guid(b.AsSpan(0, 16)), "75B22630-668E-11CF-A6D9-00AA0062CE6C")) throw Unsafe("not ASF");
        var headerEnd = (int)Length(16); if (headerEnd < 30 || headerEnd > b.Length || headerEnd > MaxTagBytes) throw Unsafe("invalid ASF header");
        result.Add("header-reserved:" + Convert.ToHexString(b.AsSpan(28, 2))); Objects(30, headerEnd, 0); result.Add("data-and-index:" + Hash(b[headerEnd..])); return result;
    }
}
