using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using JeekMusic.Online;
using JeekMusic.Library;

namespace JeekMusic.App.Tests;

[Collection("Tag writing")]
public sealed class TagWriterPolicyTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TagWritingFixtures", name);
    public static IEnumerable<object[]> NativeCases() => new[] { "id3v23.mp3", "id3v24.mp3", "untagged.mp3", "native.flac", "native.ogg", "native.opus", "native.m4a", "faststart.m4a", "untagged.m4a", "native.wav", "native.aiff", "native.wma", "native.wv" }
        .SelectMany(f => new[] { "cover", "lyrics", "combined" }.Select(op => new object[] { f, op }));
    [Theory, MemberData(nameof(NativeCases))]
    public void SharedSyntheticCorpusPreservesNonTargetsAndEncodedAudio(string name, string operation)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-policy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name); File.Copy(Fixture(name), path);
        try
        {
            var before = File.ReadAllBytes(path); var old = new ATL.Track(path); var oldLyrics = TagReader.ReadLyrics(path);
            var rawLyricsBefore = RawLyrics(before, name);
            var lyrics = File.ReadAllText(Fixture("lyrics-after.lrc")); var cover = File.ReadAllBytes(Fixture("front.jpg"));
            Assert.True(TagWriter.Write(path, operation == "lyrics" ? null : cover, operation == "cover" ? null : lyrics), TagWriter.Diagnostics().ToJsonString());
            var after = File.ReadAllBytes(path); var now = new ATL.Track(path);
            Assert.Equal(old.Title, now.Title); Assert.Equal(old.Artist, now.Artist); Assert.Equal(old.Album, now.Album); Assert.Equal(old.Comment, now.Comment); Assert.Equal(old.Year, now.Year);
            Assert.Equal(Audio(before, name), Audio(after, name));
            var opaque = File.ReadAllBytes(Fixture("opaque-marker.bin"));
            if (before.AsSpan().IndexOf(opaque) >= 0) Assert.True(after.AsSpan().IndexOf(opaque) >= 0, "Opaque native record lost");
            if (operation == "cover") Assert.Equal(oldLyrics, TagReader.ReadLyrics(path));
            else
            {
                // Production parser preserves translations at matching millisecond timestamps.
                var actual = TagReader.ReadLyrics(path)!; Assert.Contains("新的合成中文译文 العربية", actual); Assert.Contains("𠮷😀", actual);
            }
            Assert.Equal(operation == "cover" ? rawLyricsBefore : lyrics, RawLyrics(after, name));
            if (name.StartsWith("id3v", StringComparison.Ordinal))
            {
                Assert.Equal(before[3], after[3]); Assert.Equal(before[^128..], after[^128..]);
                var a = Frames(before); var b = Frames(after);
                foreach (var id in new[] { "TIT2", "TPE1", "TALB", "TDRC", "TYER", "PRIV", "TXXX" }) Assert.Equal(a.Where(x => x.Id == id).Select(x => x.Hex), b.Where(x => x.Id == id).Select(x => x.Hex));
                Assert.Contains(a, x => x.Id == "APIC"); // the back cover must remain complete, including non-ASCII/private data.
                Assert.Contains(a.Single(x => x.Id == "APIC").Hex, b.Where(x => x.Id == "APIC").Select(x => x.Hex));
                if (operation == "cover") Assert.Equal(a.Single(x => x.Id == "USLT").Hex, b.Single(x => x.Id == "USLT").Hex);
                else Assert.Equal(before[3] == 3 ? (byte)1 : (byte)3, Convert.FromHexString(b.Single(x => x.Id == "USLT").Hex)[10]);
            }
            if (name == "untagged.mp3") { Assert.Equal(4, after[3]); Assert.Equal("ID3", Encoding.ASCII.GetString(after, 0, 3)); }
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void AmbiguousLyricSlotsRefuseButCoverKeepsAllOriginalLyrics()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-policy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var path = Path.Combine(dir, "signal.mp3"); File.Copy(Fixture("multiple-lyrics.mp3"), path);
        try
        {
            var original = File.ReadAllBytes(path); var mtime = File.GetLastWriteTimeUtc(path);
            Assert.False(TagWriter.Write(path, null, "new text")); Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
            Assert.True(TagWriter.Write(path, File.ReadAllBytes(Fixture("front.jpg")), null));
            Assert.Equal(Frames(original).Where(x => x.Id == "USLT").Select(x => x.Hex), Frames(File.ReadAllBytes(path)).Where(x => x.Id == "USLT").Select(x => x.Hex));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Theory]
    [InlineData("extended")]
    [InlineData("stacked")]
    [InlineData("truncated")]
    [InlineData("surrogate")]
    [InlineData("version22")]
    public void UnsafeInputLeavesSourceBytesAndTimeUnchanged(string input)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-policy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var path = Path.Combine(dir, "signal.mp3");
        try
        {
            var bytes = File.ReadAllBytes(Fixture("id3v24.mp3"));
            if (input == "extended") bytes[5] = 64;
            if (input == "version22") bytes[3] = 2;
            if (input == "stacked") bytes = bytes[..(10 + Sync(bytes, 6))].Concat(bytes).ToArray();
            if (input == "truncated") bytes[9] = 127;
            File.WriteAllBytes(path, bytes); var time = File.GetLastWriteTimeUtc(path);
            Assert.False(TagWriter.Write(path, null, input == "surrogate" ? "invalid\ud800" : "new text"));
            Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(time, File.GetLastWriteTimeUtc(path)); Assert.Equal("rejected", TagWriter.Diagnostics()["lastResult"]!.GetValue<string>());
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void NativeGuardRejectsUnknownAtomChangesAndAudioChanges()
    {
        var m4a = File.ReadAllBytes(Fixture("native.m4a")); var changed = (byte[])m4a.Clone(); changed[^1] ^= 1;
        Assert.Throws<NotSupportedException>(() => TagWriteFrames.CheckMp4(m4a, changed, false, false));
        var ogg = File.ReadAllBytes(Fixture("native.ogg")); changed = (byte[])ogg.Clone(); changed[^1] ^= 1;
        Assert.Throws<NotSupportedException>(() => TagWriteFrames.CheckOgg(ogg, changed, false, false));
    }
    [Fact]
    public void DiagnosticsAdvertiseSameNativePolicyWithoutLyricsContent()
    {
        var d = TagWriter.Diagnostics(); Assert.Equal(1, d["policyVersion"]!.GetValue<int>()); Assert.Equal("ID3v2.4/UTF-8", d["newMp3"]!.GetValue<string>()); Assert.DoesNotContain("lyricsText", d.ToJsonString());
    }
    [Fact]
    public void ApeNativeEditKeepsId3v1AndOpaqueItemsAndHonorsReadOnly()
    {
        var source = File.ReadAllBytes(Fixture("native.wv")); var v1 = "TAG"u8.ToArray().Concat(Enumerable.Repeat((byte)0xe9, 125)).ToArray(); source = source.Concat(v1).ToArray();
        var dir = Path.Combine(Path.GetTempPath(), "jeek-policy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var path = Path.Combine(dir, "signal.wv"); File.WriteAllBytes(path, source);
        try
        {
            Assert.True(TagWriter.Write(path, null, "[00:00.123]中文 café 𠮷😀")); var after = File.ReadAllBytes(path); Assert.Equal(v1, after[^128..]); Assert.Equal(Audio(source, "source.wv"), Audio(after, "after.wv"));
            // Read-only bit belongs to the original Lyrics item, not a new global encoding policy.
            var key = Encoding.ASCII.GetBytes("Lyrics\0"); var offset = after.AsSpan().IndexOf(key); Assert.True(offset > 8); after[offset - 4] |= 1; File.WriteAllBytes(path, after);
            Assert.False(TagWriter.Write(path, null, "replacement")); Assert.Equal(after, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Theory]
    [InlineData("native.ogg")]
    [InlineData("native.opus")]
    public void LargeLyricPacketRepaginationKeepsAllAudioAndTiming(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-policy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var path = Path.Combine(dir, name); File.Copy(Fixture(name), path);
        try
        {
            var before = File.ReadAllBytes(path); var text = File.ReadAllText(Fixture("lyrics-after.lrc")) + string.Concat(Enumerable.Repeat("[00:00.123]合成译文 Ελληνικά 𠮷😀\n", 2500));
            Assert.True(TagWriter.Write(path, null, text), TagWriter.Diagnostics().ToJsonString()); var after = File.ReadAllBytes(path);
            Assert.Equal(Audio(before, name), Audio(after, name)); Assert.Equal(text, RawLyrics(after, name));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void WavPackAudioEndingInTagIsNotSplitToCreateAFalseFooter()
    {
        var bytes = Audio(File.ReadAllBytes(Fixture("native.wv")), "native.wv"); Assert.True(bytes.Length > 160); "TAG"u8.CopyTo(bytes.AsSpan(bytes.Length - 128));
        var dir = Path.Combine(Path.GetTempPath(), "jeek-policy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var path = Path.Combine(dir, "signal.wv"); File.WriteAllBytes(path, bytes);
        try { Assert.False(TagWriter.Write(path, null, "[00:00.123]new text")); Assert.Equal(bytes, File.ReadAllBytes(path)); }
        finally { Directory.Delete(dir, true); }
    }
    static int Sync(byte[] b, int p) => b[p] << 21 | b[p + 1] << 14 | b[p + 2] << 7 | b[p + 3];
    static List<(string Id, string Hex)> Frames(byte[] b)
    {
        var frames = new List<(string, string)>(); var end = 10 + Sync(b, 6);
        for (var p = 10; p + 10 <= end && b[p] != 0;)
        {
            var n = b[3] == 4 ? Sync(b, p + 4) : BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p + 4)); frames.Add((Encoding.ASCII.GetString(b, p, 4), Convert.ToHexString(b.AsSpan(p, 10 + n)))); p += 10 + n;
        }
        return frames;
    }
    // Independent native framing, not the production validator or decoded PCM.
    static byte[] Audio(byte[] b, string name)
    {
        var ext = Path.GetExtension(name);
        if (ext == ".mp3")
        {
            var start = b.AsSpan(0, 3).SequenceEqual("ID3"u8) ? 10 + Sync(b, 6) : 0; var end = b.AsSpan(b.Length - 128, 3).SequenceEqual("TAG"u8) ? b.Length - 128 : b.Length; return b[start..end];
        }
        if (ext == ".flac")
        {
            var p = 4; while (true) { var last = (b[p] & 128) != 0; var n = b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]; p += 4 + n; if (last) return b[p..]; }
        }
        if (ext is ".wav" or ".aiff")
        {
            for (var p = 12; p < b.Length;)
            {
                var id = Encoding.ASCII.GetString(b, p, 4); var n = ext == ".wav" ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 4)) : BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p + 4));
                if (id is "data" or "SSND") return b[(p + 8)..(p + 8 + n)]; p += 8 + n + (n & 1);
            }
        }
        if (ext == ".m4a")
        {
            for (var p = 0; p < b.Length;)
            {
                var n = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p)); if (Encoding.ASCII.GetString(b, p + 4, 4) == "mdat") return b[(p + 8)..(p + n)]; p += n;
            }
        }
        if (ext == ".wma") return b[(int)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16))..];
        if (ext == ".wv")
        {
            var end = b.Length; if (b.AsSpan(end - 128, 3).SequenceEqual("TAG"u8)) end -= 128;
            if (b.AsSpan(end - 32, 8).SequenceEqual("APETAGEX"u8)) { var n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(end - 20)); end -= n; if (end >= 32 && b.AsSpan(end - 32, 8).SequenceEqual("APETAGEX"u8)) end -= 32; } return b[..end];
        }
        if (ext is ".ogg" or ".opus")
        {
            var packets = new List<byte[]>(); using var packet = new MemoryStream();
            for (var p = 0; p < b.Length;)
            {
                var count = b[p + 26]; var s = p + 27 + count;
                for (var i = 0; i < count; i++) { var n = b[p + 27 + i]; packet.Write(b, s, n); s += n; if (n < 255) { packets.Add(packet.ToArray()); packet.SetLength(0); } } p = s;
            }
            return packets.SelectMany((packet, i) => i == 1 ? Array.Empty<byte>() : SHA256.HashData(packet)).ToArray();
        }
        throw new InvalidOperationException("Missing independent format check");
    }
    static string? RawLyrics(byte[] b, string name)
    {
        var ext = Path.GetExtension(name);
        if (ext == ".mp3")
        {
            if (!b.AsSpan(0, 3).SequenceEqual("ID3"u8)) return null;
            var frame = Frames(b).FirstOrDefault(f => f.Id == "USLT"); if (frame.Hex is null) return null; var body = Convert.FromHexString(frame.Hex)[10..];
            var encoding = body[0] switch { 0 => Encoding.Latin1, 1 => Encoding.Unicode, 2 => Encoding.BigEndianUnicode, 3 => Encoding.UTF8, _ => throw new InvalidDataException() };
            var text = encoding.GetString(body, 4, body.Length - 4).TrimStart('\ufeff'); return text[(text.IndexOf('\0') + 1)..].TrimStart('\ufeff').TrimEnd('\0');
        }
        if (ext is ".flac" or ".ogg" or ".opus")
        {
            byte[]? comment = null;
            if (ext == ".flac")
            {
                for (var p = 4; p < b.Length;) { var n = b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]; if ((b[p] & 127) == 4) comment = b[(p + 4)..(p + 4 + n)]; var last = (b[p] & 128) != 0; p += 4 + n; if (last) break; }
            }
            else
            {
                var packets = new List<byte[]>(); using var packet = new MemoryStream();
                for (var p = 0; p < b.Length && packets.Count < 2;)
                {
                    var count = b[p + 26]; var q = p + 27 + count; for (var i = 0; i < count; i++) { var n = b[p + 27 + i]; packet.Write(b, q, n); q += n; if (n < 255) { packets.Add(packet.ToArray()); packet.SetLength(0); } } p = q;
                }
                comment = packets[1][(ext == ".opus" ? 8 : 7)..];
            }
            if (comment is null) return null; var pos = 4 + BinaryPrimitives.ReadInt32LittleEndian(comment); var fields = BinaryPrimitives.ReadInt32LittleEndian(comment.AsSpan(pos)); pos += 4;
            for (var i = 0; i < fields; i++) { var n = BinaryPrimitives.ReadInt32LittleEndian(comment.AsSpan(pos)); pos += 4; var value = Encoding.UTF8.GetString(comment, pos, n); pos += n; if (value.StartsWith("LYRICS=", StringComparison.OrdinalIgnoreCase)) return value[7..]; } return null;
        }
        if (ext is ".wav" or ".aiff")
        {
            for (var p = 12; p < b.Length;) { var n = ext == ".wav" ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 4)) : BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p + 4)); if (Encoding.ASCII.GetString(b, p, 4).Equals("id3 ", StringComparison.OrdinalIgnoreCase)) return RawLyrics(b[(p + 8)..(p + 8 + n)], "chunk.mp3"); p += 8 + n + (n & 1); } return null;
        }
        if (ext == ".m4a")
        {
            string? Walk(byte[] bytes)
            {
                for (var p = 0; p < bytes.Length;)
                {
                    var n = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(p)); var type = Encoding.Latin1.GetString(bytes, p + 4, 4); var body = bytes[(p + 8)..(p + n)];
                    if (type == "©lyr") { Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(8))); return Encoding.UTF8.GetString(body[16..]); }
                    if (type is "moov" or "udta" or "meta" or "ilst") { var result = Walk(body[(type == "meta" ? 4 : 0)..]); if (result is not null) return result; } p += n;
                }
                return null;
            }
            return Walk(b);
        }
        if (ext == ".wv")
        {
            var end = b.Length; if (b.AsSpan(end - 128, 3).SequenceEqual("TAG"u8)) end -= 128; if (!b.AsSpan(end - 32, 8).SequenceEqual("APETAGEX"u8)) return null;
            var footer = end - 32; var p = end - BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(footer + 12)); var count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(footer + 16));
            for (var i = 0; i < count; i++) { var n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); var k = Array.IndexOf(b, (byte)0, p + 8); var key = Encoding.ASCII.GetString(b, p + 8, k - p - 8); if (key.Equals("Lyrics", StringComparison.OrdinalIgnoreCase)) return Encoding.UTF8.GetString(b, k + 1, n); p = k + 1 + n; } return null;
        }
        if (ext == ".wma")
        {
            string? Objects(int start, int end)
            {
                for (var p = start; p < end;)
                {
                    var id = new Guid(b.AsSpan(p, 16)).ToString(); var n = (int)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p + 16)); var q = p + 24;
                    if (id == "5fbf03b5-a92e-11cf-8ee3-00c00c205365") { var found = Objects(q + 22, p + n); if (found is not null) return found; }
                    if (id is "d2d0a440-e307-11d2-97f0-00a0c95ea850" or "c5f8cbea-5baf-4877-8467-aa8c44fa4cca" or "44231c94-9498-49d1-a141-1d134e457054")
                    {
                        var extended = id.StartsWith("d2d0", StringComparison.Ordinal); var count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(q)); q += 2;
                        for (var i = 0; i < count; i++)
                        {
                            if (!extended) q += 4; var len = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(q)); q += 2; var size = 0;
                            if (!extended) { q += 2; size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(q)); q += 4; }
                            var key = Encoding.Unicode.GetString(b, q, len).TrimEnd('\0'); q += len;
                            if (extended) { q += 2; size = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(q)); q += 2; }
                            if (key == "WM/Lyrics") return Encoding.Unicode.GetString(b, q, size).TrimEnd('\0'); q += size;
                        }
                    }
                    p += n;
                }
                return null;
            }
            return Objects(30, (int)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16)));
        }
        throw new InvalidOperationException("Missing raw lyric check");
    }
}

[CollectionDefinition("Tag writing", DisableParallelization = true)]
public sealed class TagWritingCollection { }
