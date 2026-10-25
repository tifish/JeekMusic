using System.Buffers.Binary;
using System.Text;

namespace JeekMusic.Online;

internal static partial class TagWriteFrames
{
    static readonly Guid AsfExtension = new("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
    static readonly Guid AsfExtended = new("D2D0A440-E307-11D2-97F0-00A0C95EA850");
    static readonly Guid AsfMetadata = new("C5F8CBEA-5BAF-4877-8467-AA8C44FA4CCA");
    static readonly Guid AsfLibrary = new("44231C94-9498-49D1-A141-1D134E457054");
    sealed record AsfObject(Guid Id, byte[] Raw, byte[] Body);
    sealed record AsfEntry(string Name, int Type, byte[] Value, byte[] Raw);
    static bool DescriptorObject(Guid id) => id == AsfExtended || id == AsfMetadata || id == AsfLibrary;
    static byte[] AsfBox(Guid id, byte[] body)
    {
        var b = new byte[24 + body.Length]; id.ToByteArray().CopyTo(b, 0); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), (ulong)b.Length); body.CopyTo(b, 24); return b;
    }
    static List<AsfObject> AsfObjects(byte[] b, int start, int end)
    {
        var result = new List<AsfObject>();
        for (var p = start; p < end;)
        {
            if (p > end - 24) throw Unsafe("truncated ASF object"); var n = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p + 16));
            if (n < 24 || n > (ulong)(end - p)) throw Unsafe("invalid ASF object size");
            result.Add(new(new Guid(b.AsSpan(p, 16)), b[p..(p + (int)n)], b[(p + 24)..(p + (int)n)])); p += (int)n;
        }
        if (result.GroupBy(x => x.Id).Any(g => g.Count() > 1 && (DescriptorObject(g.Key) || g.Key == AsfExtension))) throw Unsafe("multiple ASF descriptor containers");
        return result;
    }
    static List<AsfEntry> AsfEntries(AsfObject obj)
    {
        var b = obj.Body; var p = 0; var result = new List<AsfEntry>(); var extended = obj.Id == AsfExtended;
        int Word() { if (p > b.Length - 2) throw Unsafe("truncated ASF descriptor word"); var n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2; return n; }
        var count = Word();
        for (var i = 0; i < count; i++)
        {
            var start = p; if (!extended) { Word(); Word(); } var length = Word(); var type = extended ? 0 : Word(); var size = extended ? 0 : Number(b, p, false); if (!extended) p += 4;
            if ((length & 1) != 0 || length > b.Length - p) throw Unsafe("invalid ASF descriptor name"); var name = Encoding.Unicode.GetString(b, p, length).TrimEnd('\0'); p += length;
            if (extended) { type = Word(); size = Word(); }
            if (size > b.Length - p) throw Unsafe("truncated ASF descriptor value"); var value = b[p..(p + size)]; p += size; result.Add(new(name, type, value, b[start..p]));
        }
        if (p != b.Length) throw Unsafe("ASF descriptor trailing bytes"); return result;
    }
    internal static byte[] RetainAsfNonTargets(byte[] before, byte[] candidate, byte[]? cover, string? lyrics)
    {
        var end = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(before.AsSpan(16))); var producedEnd = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(candidate.AsSpan(16)));
        if (end < 30 || end > before.Length || producedEnd < 30 || producedEnd > candidate.Length) throw Unsafe("invalid ASF header");
        List<AsfObject> Children(AsfObject extension)
        {
            if (extension.Body.Length < 22 || Number(extension.Body, 18, false) != extension.Body.Length - 22) throw Unsafe("invalid ASF extension");
            return AsfObjects(extension.Body, 22, extension.Body.Length);
        }
        bool Target(AsfEntry e)
        {
            if (lyrics is not null && e.Name == "WM/Lyrics_Synchronised") throw Unsafe("ASF synchronized lyric slot");
            if (lyrics is not null && e.Name == "WM/Lyrics") return true;
            return cover is not null && e.Name == "WM/Picture" && e.Type == 1 && e.Value.Length > 0 && e.Value[0] is 0 or 3;
        }
        var original = AsfObjects(before, 30, end); var produced = AsfObjects(candidate, 30, producedEnd);
        if (Number(before, 24, false) != original.Count || Number(candidate, 24, false) != produced.Count) throw Unsafe("ASF object count mismatch");
        if (lyrics is not null)
        {
            var slots = original.SelectMany(o => o.Id == AsfExtension ? Children(o) : [o]).Where(o => DescriptorObject(o.Id)).SelectMany(o => AsfEntries(o).Where(e => e.Name == "WM/Lyrics").Select(e => (Object: o, Entry: e))).ToList();
            if (slots.Count > 1 || slots.Any(x => x.Object.Id != AsfExtended && x.Entry.Raw.AsSpan(0, 4).ContainsAnyExcept((byte)0))) throw Unsafe("multiple/scoped ASF lyric slots require explicit selection");
        }
        var generated = produced.SelectMany(o => o.Id == AsfExtension ? Children(o) : [o]).Where(o => DescriptorObject(o.Id)).ToDictionary(o => o.Id, o => AsfEntries(o).Where(Target).ToList());
        var targets = generated.Values.SelectMany(e => e).ToList();
        if (lyrics is not null)
        {
            var text = targets.Where(e => e.Name == "WM/Lyrics").ToList();
            if (text.Count != 1 || text[0].Type != 0 || Encoding.Unicode.GetString(text[0].Value).TrimEnd('\0') != lyrics) throw Unsafe("ASF lyric text/type mismatch");
        }
        if (cover is not null)
        {
            var pictures = targets.Where(e => e.Name == "WM/Picture").ToList();
            if (pictures.Count != 1 || Number(pictures[0].Value, 1, false) != cover.Length || !pictures[0].Value.AsSpan(pictures[0].Value.Length - cover.Length).SequenceEqual(cover)) throw Unsafe("ASF cover content mismatch");
        }
        byte[] Entries(Guid id, List<AsfEntry> entries)
        {
            if (entries.Count > ushort.MaxValue) throw Unsafe("too many ASF descriptors");
            return AsfBox(id, [(byte)entries.Count, (byte)(entries.Count >> 8), ..entries.SelectMany(e => e.Raw)]);
        }
        List<byte[]> Merge(List<AsfObject> old, List<AsfObject> native)
        {
            var result = new List<byte[]>();
            foreach (var o in old)
            {
                if (DescriptorObject(o.Id))
                {
                    var entries = AsfEntries(o); if (lyrics is not null && entries.Count(e => e.Name == "WM/Lyrics") > 1) throw Unsafe("multiple ASF lyric slots");
                    var kept = entries.Where(e => !Target(e)).ToList(); if (generated.Remove(o.Id, out var replacement)) kept.AddRange(replacement); result.Add(Entries(o.Id, kept));
                }
                else if (o.Id == AsfExtension)
                {
                    var nativeExtension = native.SingleOrDefault(x => x.Id == AsfExtension); var children = Merge(Children(o), nativeExtension is null ? [] : Children(nativeExtension)); var payload = children.SelectMany(x => x).ToArray();
                    result.Add(AsfBox(o.Id, [..o.Body.AsSpan(0, 18), ..Size(payload.Length, false), ..payload]));
                }
                else result.Add(o.Raw);
            }
            foreach (var o in native.Where(o => old.All(a => a.Id != o.Id)))
            {
                if (DescriptorObject(o.Id) && generated.Remove(o.Id, out var replacement) && replacement.Count != 0) result.Add(Entries(o.Id, replacement));
                else if (o.Id == AsfExtension)
                {
                    var children = Merge([], Children(o)); if (children.Count == 0) continue; var payload = children.SelectMany(x => x).ToArray(); result.Add(AsfBox(o.Id, [..o.Body.AsSpan(0, 18), ..Size(payload.Length, false), ..payload]));
                }
            }
            return result;
        }
        var objects = Merge(original, produced); if (generated.Values.Any(v => v.Count != 0)) throw Unsafe("unplaced ASF target descriptors");
        var body = objects.SelectMany(x => x).ToArray(); var result = new byte[30 + body.Length + before.Length - end]; before.AsSpan(0, 30).CopyTo(result);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), (ulong)(30 + body.Length)); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), (uint)objects.Count); body.CopyTo(result, 30); before.AsSpan(end).CopyTo(result.AsSpan(30 + body.Length));
        UpdateAsfFileSize(before, result); CheckAsf(before, result, cover is not null, lyrics is not null); return result;
    }
}
