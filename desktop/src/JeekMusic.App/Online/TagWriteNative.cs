using System.Text;
using SkiaSharp;

namespace JeekMusic.Online;

internal static partial class TagWriteFrames
{
    internal sealed record Block(int Type, byte[] Body);
    static List<Block> FlacBlocks(byte[] b, out int end)
    {
        if (b.Length < 8 || !b.AsSpan(0, 4).SequenceEqual("fLaC"u8)) throw Unsafe("not native FLAC");
        var blocks = new List<Block>(); var p = 4;
        while (true)
        {
            if (p > b.Length - 4 || p > MaxTagBytes) throw Unsafe("truncated/oversized FLAC metadata");
            var n = b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3];
            if (n > b.Length - p - 4 || (b[p] & 127) == 127) throw Unsafe("invalid FLAC block");
            blocks.Add(new(b[p] & 127, b[(p + 4)..(p + 4 + n)])); var last = (b[p] & 128) != 0; p += 4 + n;
            if (last) break;
        }
        if (blocks[0].Type != 0 || blocks[0].Body.Length != 34 || blocks.Count(x => x.Type == 0) != 1 || blocks.Count(x => x.Type == 4) > 1) throw Unsafe("ambiguous FLAC metadata");
        if (p > MaxTagBytes) throw Unsafe("FLAC metadata exceeds limit"); end = p; return blocks;
    }
    internal sealed record Comments(byte[] Vendor, List<byte[]> Fields, byte[] Suffix);
    internal static Comments ParseComments(byte[] b)
    {
        var p = 0;
        byte[] Vector()
        {
            var n = Number(b, p, false); p += 4;
            if (n > b.Length - p) throw Unsafe("truncated Vorbis vector");
            var s = b[p..(p + n)]; p += n; return s;
        }
        var vendor = Vector(); var count = Number(b, p, false); p += 4;
        if (count > (b.Length - p) / 4) throw Unsafe("invalid comment count");
        var fields = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            var field = Vector(); var key = Array.IndexOf(field, (byte)'=');
            if (key <= 0 || field.AsSpan(0, key).ContainsAnyExceptInRange((byte)32, (byte)125)) throw Unsafe("invalid Vorbis key");
            fields.Add(field);
        }
        return new(vendor, fields, b[p..]);
    }
    static string Key(byte[] field) => Encoding.ASCII.GetString(field, 0, Array.IndexOf(field, (byte)'=')).ToUpperInvariant();
    static byte[] EncodeComments(Comments c)
    {
        using var s = new MemoryStream(); s.Write(Size(c.Vendor.Length, false)); s.Write(c.Vendor); s.Write(Size(c.Fields.Count, false));
        foreach (var f in c.Fields) { s.Write(Size(f.Length, false)); s.Write(f); }
        s.Write(c.Suffix); return s.ToArray();
    }
    static byte[] Picture(byte[] jpeg)
    {
        using var image = SKBitmap.Decode(jpeg) ?? throw Unsafe("invalid cover image");
        return [..Size(3), ..Size(10), .."image/jpeg"u8, ..Size(0), ..Size(image.Width), ..Size(image.Height), ..Size(24), ..Size(0), ..Size(jpeg.Length), ..jpeg];
    }
    internal static byte[] EditFlac(byte[] source, byte[]? cover, string? lyrics)
    {
        var blocks = FlacBlocks(source, out var end); var result = new List<Block>(); var wrote = false;
        foreach (var block in blocks)
        {
            if (cover is not null && block.Type == 6 && Number(block.Body, 0) is 0 or 3) continue;
            if (lyrics is not null && block.Type == 4)
            {
                var c = ParseComments(block.Body);
                if (c.Suffix.Length != 0 || c.Fields.Count(x => Key(x) == "LYRICS") > 1) throw Unsafe("ambiguous FLAC lyrics/comment suffix");
                var fields = c.Fields.Where(x => Key(x) != "LYRICS").ToList(); fields.Add([.."LYRICS="u8, ..Utf8.GetBytes(lyrics)]);
                result.Add(new(4, EncodeComments(c with { Fields = fields }))); wrote = true;
            }
            else result.Add(block);
        }
        if (lyrics is not null && !wrote) result.Add(new(4, EncodeComments(new("JeekMusic"u8.ToArray(), [[.."LYRICS="u8, ..Utf8.GetBytes(lyrics)]], []))));
        if (cover is not null) result.Add(new(6, Picture(cover)));
        using var output = new MemoryStream(); output.Write("fLaC"u8);
        for (var i = 0; i < result.Count; i++)
        {
            var b = result[i]; if (b.Body.Length > 0xffffff) throw Unsafe("FLAC metadata block too large");
            output.WriteByte((byte)(b.Type | (i == result.Count - 1 ? 128 : 0))); output.Write(Size(b.Body.Length).AsSpan(1)); output.Write(b.Body);
        }
        output.Write(source.AsSpan(end)); return output.ToArray();
    }
    internal static byte[] EditApe(byte[] source, byte[]? cover, string? lyrics)
    {
        var v1 = source.Length >= 128 && source.AsSpan(source.Length - 128, 3).SequenceEqual("TAG"u8) ? 128 : 0;
        var end = source.Length - v1; var start = end; var entries = new List<(string Key, byte[] Raw)>(); var header = false;
        var hasApe = end >= 32 && source.AsSpan(end - 32, 8).SequenceEqual("APETAGEX"u8);
        // A TAG signature within a WavPack block is audio, not a footer. Never insert a tag
        // inside that block. Other footer-only families need a proven native APE boundary.
        var wavpack = source.AsSpan(0, 4).SequenceEqual("wvpk"u8); var audioEnd = 0;
        if (wavpack)
        {
            while (audioEnd <= source.Length - 32 && source.AsSpan(audioEnd, 4).SequenceEqual("wvpk"u8))
            {
                var size = Number(source, audioEnd + 4, false);
                if (size < 24 || size > source.Length - audioEnd - 8) throw Unsafe("invalid WavPack audio block"); audioEnd += 8 + size;
            }
            if (audioEnd == 0 || audioEnd > end) throw Unsafe("TAG signature overlaps WavPack audio");
        }
        else if (v1 != 0 && !hasApe) throw Unsafe("footer-only APE family requires a proven native tag boundary");
        if (hasApe)
        {
            var footer = end - 32; var size = Number(source, footer + 12, false); var count = Number(source, footer + 16, false);
            if (Number(source, footer + 8, false) != 2000 || size < 32 || size > end || size > MaxTagBytes || count > size / 9) throw Unsafe("unsupported APE tag version/size");
            var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(footer + 20, 4));
            if ((flags & ~0x80000000u) != 0 || source.AsSpan(footer + 24, 8).ContainsAnyExcept((byte)0)) throw Unsafe("complex APE footer");
            header = (flags & 0x80000000u) != 0; start = end - size; var p = start;
            for (var i = 0; i < count; i++)
            {
                var n = Number(source, p, false); var keyEnd = Array.IndexOf(source, (byte)0, p + 8);
                if (keyEnd < p + 9 || keyEnd >= footer || n > footer - keyEnd - 1) throw Unsafe("invalid APE item");
                var key = Encoding.ASCII.GetString(source, p + 8, keyEnd - p - 8);
                entries.Add((key.ToUpperInvariant(), source[p..(keyEnd + 1 + n)])); p = keyEnd + 1 + n;
            }
            if (p != footer) throw Unsafe("APE unparsed item data");
            if (header)
            {
                start -= 32;
                if (start < 0 || !source.AsSpan(start, 8).SequenceEqual("APETAGEX"u8) || Number(source, start + 8, false) != 2000 || Number(source, start + 12, false) != size || Number(source, start + 16, false) != count || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(start + 20, 4)) != 0xa0000000u || source.AsSpan(start + 24, 8).ContainsAnyExcept((byte)0)) throw Unsafe("invalid APE header");
            }
        }
        if (wavpack && start < audioEnd) throw Unsafe("APE metadata overlaps WavPack audio");
        bool Target(string key) => (lyrics is not null && key == "LYRICS") || (cover is not null && key == "COVER ART (FRONT)");
        if (lyrics is not null && entries.Count(e => e.Key == "LYRICS") > 1) throw Unsafe("multiple APE lyric slots");
        foreach (var e in entries.Where(x => Target(x.Key)))
            if ((Number(e.Raw, 4, false) & 1) != 0) throw Unsafe("read-only APE target item");
        var kept = entries.Where(e => !Target(e.Key)).Select(e => e.Raw).ToList();
        static byte[] Item(string key, byte[] value, int flags) => [..Size(value.Length, false), ..Size(flags, false), ..Encoding.ASCII.GetBytes(key), 0, ..value];
        if (lyrics is not null) kept.Add(Item("Lyrics", Utf8.GetBytes(lyrics), 0));
        if (cover is not null) kept.Add(Item("Cover Art (Front)", [.."cover.jpg\0"u8, ..cover], 2));
        var tagSize = 32 + kept.Sum(x => x.Length); if (tagSize > MaxTagBytes) throw Unsafe("APE tag too large");
        byte[] Footer(bool isHeader) => [.."APETAGEX"u8, ..Size(2000, false), ..Size(tagSize, false), ..Size(kept.Count, false), 0, 0, 0, (byte)((header ? 0x80 : 0) | (isHeader ? 0x20 : 0)), 0, 0, 0, 0, 0, 0, 0, 0];
        using var output = new MemoryStream(); output.Write(source.AsSpan(0, start)); if (header) output.Write(Footer(true));
        foreach (var item in kept) output.Write(item); output.Write(Footer(false)); output.Write(source.AsSpan(end)); return output.ToArray();
    }
}
