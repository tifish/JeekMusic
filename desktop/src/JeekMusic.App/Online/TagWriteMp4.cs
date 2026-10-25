using System.Buffers.Binary;
using System.Text;

namespace JeekMusic.Online;

internal static partial class TagWriteFrames
{
    sealed record Atom(string Type, byte[] Raw, byte[] Body, int Offset, int Header);
    static List<Atom> AtomList(byte[] b, int start = 0, int? limit = null)
    {
        var end = limit ?? b.Length; var result = new List<Atom>();
        for (var p = start; p < end;)
        {
            if (p > end - 8) throw Unsafe("truncated atom"); var n = Number(b, p); var header = 8;
            if (n == 1)
            {
                if (p > end - 16) throw Unsafe("truncated large atom"); var large = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p + 8));
                if (large > int.MaxValue) throw Unsafe("large atom"); n = (int)large; header = 16;
            }
            if (n == 0) n = end - p;
            if (n < header || n > end - p) throw Unsafe("invalid atom size");
            result.Add(new(Encoding.Latin1.GetString(b, p + 4, 4), b[p..(p + n)], b[(p + header)..(p + n)], p, header)); p += n;
        }
        return result;
    }
    static byte[] Box(string type, byte[] body) => [..Size(8 + body.Length), ..Encoding.Latin1.GetBytes(type), ..body];
    static byte[] Rebox(Atom a, byte[] body)
    {
        if (a.Header == 8) return Box(a.Type, body);
        var result = new byte[16 + body.Length]; a.Raw.AsSpan(0, 8).CopyTo(result); BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8), (ulong)result.Length); body.CopyTo(result, 16); return result;
    }
    static bool MovieContainer(string type) => type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "udta" or "meta" or "ilst";
    static List<Atom> FindIlst(Atom node, int depth = 0)
    {
        if (depth > 12) throw Unsafe("deep atom nesting");
        if (node.Type == "ilst") return [node];
        if (!MovieContainer(node.Type)) return [];
        return AtomList(node.Body, node.Type == "meta" ? 4 : 0).SelectMany(a => FindIlst(a, depth + 1)).ToList();
    }
    internal static byte[] RetainMp4NonTargets(byte[] before, byte[] candidate, byte[]? cover, string? lyrics)
    {
        CheckMp4(before, before, false, false); // validate original internal sample locations too
        var original = AtomList(before); var movies = original.Where(a => a.Type == "moov").ToList();
        if (movies.Count != 1) throw Unsafe("ambiguous MP4 movie"); var movie = movies[0];
        var produced = AtomList(candidate).SingleOrDefault(a => a.Type == "moov") ?? throw Unsafe("missing generated movie");
        var lists = FindIlst(produced); var oldLists = FindIlst(movie);
        if (lists.Count != 1 || oldLists.Count > 1) throw Unsafe("ambiguous MP4 metadata list");
        bool Target(Atom a) => (cover is not null && a.Type == "covr") || (lyrics is not null && a.Type == "©lyr");
        var replacements = AtomList(lists[0].Body).Where(Target).ToList();
        if (oldLists.Count == 1 && lyrics is not null && AtomList(oldLists[0].Body).Count(a => a.Type == "©lyr") > 1) throw Unsafe("multiple MP4 lyric slots");
        if (oldLists.Count == 1)
            foreach (var target in AtomList(oldLists[0].Body).Where(Target))
            {
                var data = AtomList(target.Body);
                if (data.Any(a => a.Type != "data") || (target.Type == "©lyr" && data.Count != 1)) throw Unsafe("complex MP4 target property");
                if (target.Type == "©lyr")
                {
                    if (data[0].Body.Length < 8) throw Unsafe("invalid native lyric value");
                    if (data[0].Body.AsSpan(4, 4).ContainsAnyExcept((byte)0)) throw Unsafe("localized MP4 lyric slot requires explicit selection");
                }
            }
        foreach (var kind in new[] { (Name: "covr", Value: cover), (Name: "©lyr", Value: lyrics is null ? null : Utf8.GetBytes(lyrics)) })
        {
            if (kind.Value is null) continue;
            var item = replacements.SingleOrDefault(a => a.Type == kind.Name) ?? throw Unsafe("missing native target atom");
            var data = AtomList(item.Body);
            if (data.Count != 1 || data[0].Type != "data" || data[0].Body.Length < 8 || Number(data[0].Body, 0) != (kind.Name == "covr" ? 13 : 1) || !data[0].Body.AsSpan(8).SequenceEqual(kind.Value)) throw Unsafe("native target type/content mismatch");
        }
        byte[] NewList() => Box("ilst", replacements.SelectMany(a => a.Raw).ToArray());
        byte[] NewMeta()
        {
            var meta = FindMeta(produced);
            var handler = AtomList(meta.Body, 4).Where(a => a.Type == "hdlr").SelectMany(a => a.Raw).ToArray();
            return Box("meta", [..meta.Body.AsSpan(0, 4), ..handler, ..NewList()]);
        }
        static Atom FindMeta(Atom node)
        {
            if (node.Type == "meta" && FindIlst(node).Count == 1) return node;
            foreach (var child in AtomList(node.Body).Where(a => a.Type is "udta" or "meta"))
                if (FindIlst(child).Count == 1) return FindMeta(child);
            throw Unsafe("missing native meta ancestor");
        }
        byte[] Edit(Atom node)
        {
            if (!MovieContainer(node.Type)) return node.Raw;
            var prefix = node.Type == "meta" ? 4 : 0; var children = AtomList(node.Body, prefix);
            if (node.Type == "meta" && children.Any(a => a.Type == "keys")) throw Unsafe("key-indexed MP4 metadata needs a separate profile");
            byte[] content;
            if (node.Type == "ilst") content = children.Where(a => !Target(a)).SelectMany(a => a.Raw).Concat(replacements.SelectMany(a => a.Raw)).ToArray();
            else
            {
                content = children.SelectMany(a => Edit(a)).ToArray();
                if (FindIlst(node).Count == 0)
                {
                    if (node.Type == "meta") content = [..content, ..NewList()];
                    else if (node.Type == "udta" && children.All(a => a.Type != "meta")) content = [..content, ..NewMeta()];
                    else if (node.Type == "moov" && children.All(a => a.Type is not ("udta" or "meta"))) content = [..content, ..Box("udta", NewMeta())];
                }
            }
            return Rebox(node, [..node.Body.AsSpan(0, prefix), ..content]);
        }
        var edited = Edit(movie); var media = original.Single(a => a.Type == "mdat");
        var delta = movie.Offset < media.Offset ? edited.Length - movie.Raw.Length : 0;
        byte[] Offsets(Atom node, int depth = 0)
        {
            if (depth > 12) throw Unsafe("deep atom nesting");
            if (node.Type is "stco" or "co64")
            {
                var body = (byte[])node.Body.Clone(); var count = Number(body, 4); var width = node.Type == "stco" ? 4 : 8;
                if (body.Length != 8 + (long)count * width) throw Unsafe("invalid MP4 chunk table");
                for (var i = 0; i < count; i++)
                {
                    var p = 8 + i * width; var value = width == 4 ? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(p)) : BinaryPrimitives.ReadUInt64BigEndian(body.AsSpan(p));
                    if (value < (ulong)(media.Offset + media.Header) || value >= (ulong)(media.Offset + media.Raw.Length)) throw Unsafe("external MP4 sample data");
                    var adjusted = checked((ulong)((long)value + delta));
                    if (width == 4) BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(p), checked((uint)adjusted)); else BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(p), adjusted);
                }
                return Rebox(node, body);
            }
            if (!MovieContainer(node.Type)) return node.Raw;
            var prefix = node.Type == "meta" ? 4 : 0;
            return Rebox(node, [..node.Body.AsSpan(0, prefix), ..AtomList(node.Body, prefix).SelectMany(a => Offsets(a, depth + 1))]);
        }
        if (delta != 0) edited = Offsets(AtomList(edited).Single());
        var result = original.SelectMany(a => a == movie ? edited : a.Raw).ToArray(); CheckMp4(before, result, cover is not null, lyrics is not null); return result;
    }
}
