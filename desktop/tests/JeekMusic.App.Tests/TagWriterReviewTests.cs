using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
using JeekMusic.Online;

namespace JeekMusic.App.Tests;

[Collection("Tag writing")]
public sealed class TagWriterReviewTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TagWritingFixtures", name);
    static byte[] Be(int n) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, n); return b; }
    static byte[] Box(string id, byte[] body) => [..Be(8 + body.Length), ..Encoding.Latin1.GetBytes(id), ..body];
    static byte[] Sync(int n) => [(byte)(n >> 21 & 127), (byte)(n >> 14 & 127), (byte)(n >> 7 & 127), (byte)(n & 127)];
    static byte[] Frame(string id, byte[] body, int version = 4) => [..Encoding.ASCII.GetBytes(id), ..(version == 4 ? Sync(body.Length) : Be(body.Length)), 0, 0, ..body];
    static byte[] Tag(byte[] frames, int version = 4) => [.."ID3"u8, (byte)version, 0, 0, ..Sync(frames.Length), ..frames, ..File.ReadAllBytes(Fixture("untagged.mp3"))];
    static void FileCase(string name, byte[] bytes, Action<string> run)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var path = Path.Combine(dir, name); File.WriteAllBytes(path, bytes);
        try { run(path); } finally { Directory.Delete(dir, true); }
    }
    static void Refuses(string path, byte[] original)
    {
        var time = File.GetLastWriteTimeUtc(path);
        var cover = File.ReadAllBytes(Fixture("front.jpg"));
        foreach (var op in new[] { "cover", "lyrics", "combined" })
        {
            Assert.False(TagWriter.Write(path, op == "lyrics" ? null : cover, op == "cover" ? null : new string('x', 5000)), TagWriter.Diagnostics().ToJsonString());
            Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(time, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData(3, false)] [InlineData(4, false)] [InlineData(3, true)] [InlineData(4, true)]
    public void ChaptersWithAbsoluteOffsetsAndNestedTocRefuseWithoutChangingSource(int version, bool nested)
    {
        // Absolute offset 38 is the original audio start in the minimal CHAP repro.
        var chapter = Frame("CHAP", [.."c\0"u8, ..Be(0), ..Be(1000), ..Be(38), ..Be(38)], version);
        var frames = nested ? Frame("CTOC", [.."toc\0"u8, 3, 1, .."c\0"u8, ..chapter], version) : chapter;
        var chapterIndex = frames.AsSpan().IndexOf("CHAP"u8);
        BinaryPrimitives.WriteInt32BigEndian(frames.AsSpan(chapterIndex + 20), 10 + frames.Length);
        BinaryPrimitives.WriteInt32BigEndian(frames.AsSpan(chapterIndex + 24), 10 + frames.Length);
        var bytes = Tag(frames, version); FileCase("chapter.mp3", bytes, p => Refuses(p, bytes));
    }
    [Theory]
    [InlineData("SEEK")] [InlineData("ASPI")] [InlineData("MLLT")]
    public void OtherId3PositionIndexesRefuse(string id)
    {
        var bytes = Tag(Frame(id, new byte[16])); FileCase("index.mp3", bytes, p => Refuses(p, bytes));
    }
    [Theory]
    [InlineData(3, 0x80)] [InlineData(4, 0x40)]
    public void UnknownDiscardOnTagAlterationFramesRefuseForTheirActualVersion(int version, byte status)
    {
        var frame = Frame("XPOS", Be(24), version); frame[8] = status;
        var bytes = Tag(frame, version); FileCase("discard.mp3", bytes, p => Refuses(p, bytes));
    }

    static byte[] AddMp4Child(byte[] input, string parent, byte[] child, bool first = false)
    {
        byte[] Walk(byte[] bytes)
        {
            using var output = new MemoryStream();
            for (var p = 0; p < bytes.Length;)
            {
                var n = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(p)); var id = Encoding.Latin1.GetString(bytes, p + 4, 4); var body = bytes[(p + 8)..(p + n)];
                if (id == parent) body = first ? [..child, ..body] : [..body, ..child];
                else if (id is "moov" or "trak" or "mdia" or "minf" or "stbl" or "udta") body = Walk(body);
                output.Write(Box(id, body)); p += n;
            }
            return output.ToArray();
        }
        return Walk(input);
    }
    [Theory]
    [InlineData("saio")] [InlineData("senc")] [InlineData("saiz")]
    [InlineData("iloc")] [InlineData("sidx")] [InlineData("tfra")] [InlineData("pssh")] [InlineData("meco")]
    [InlineData("uuid")]
    public void UnsupportedMp4PositionDependenciesRefuseEvenWhenMdatIsUnchanged(string id)
    {
        var source = File.ReadAllBytes(Fixture("native.m4a"));
        // Put udta before trak so expanding metadata also moves a sample-side index.
        var bytes = AddMp4Child(source, "stbl", Box(id, [..Be(0), ..Be(1), ..Be(856)]));
        FileCase("position.m4a", bytes, p => Refuses(p, bytes));
    }
    [Theory]
    [InlineData("enca")] [InlineData("encv")] [InlineData("drms")]
    public void ProtectedSampleEntriesRefuse(string type)
    {
        var bytes = File.ReadAllBytes(Fixture("native.m4a")); var pos = bytes.AsSpan().IndexOf("mp4a"u8); Assert.True(pos > 0); Encoding.ASCII.GetBytes(type).CopyTo(bytes, pos);
        FileCase("protected.m4a", bytes, p => Refuses(p, bytes));
    }
    [Fact]
    public void ProtectionAtomInsidePlainSampleEntryRefuses()
    {
        var source = File.ReadAllBytes(Fixture("native.m4a")); var entry = source.AsSpan().IndexOf("mp4a"u8); Assert.True(entry > 0);
        // Construct the sample-description envelope independently, not via the production walker.
        var sample = Box("mp4a", [..new byte[28], ..Box("sinf", Box("schi", Box("tenc", new byte[24])))]);
        var bytes = AddMp4Child(source, "stbl", Box("stsd", [..Be(0), ..Be(1), ..sample]));
        FileCase("protection.m4a", bytes, p => Refuses(p, bytes));
    }
    [Fact]
    public void EncryptionSampleGroupRefusesWithoutChangingMdat()
    {
        var bytes = AddMp4Child(File.ReadAllBytes(Fixture("native.m4a")), "stbl", Box("sgpd", [..Be(0), .."seig"u8, ..Be(0)]));
        FileCase("group.m4a", bytes, p => Refuses(p, bytes));
    }
    [Theory]
    [InlineData("iloc")] [InlineData("uuid")]
    public void LocationOrVendorAtomInsideUdtaFullBoxMetaRefuses(string type)
    {
        var bytes = AddMp4Child(File.ReadAllBytes(Fixture("native.m4a")), "meta", Box(type, new byte[24]));
        FileCase("nested-location.m4a", bytes, p => Refuses(p, bytes));
    }

    static uint Crc(byte[] page)
    {
        uint crc = 0;
        foreach (var value in page) { crc ^= (uint)value << 24; for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) == 0 ? crc << 1 : crc << 1 ^ 0x04c11db7; }
        return crc;
    }
    static void AssertEveryOggPage(byte[] bytes)
    {
        var pending = false; var pages = 0; var emptyCompletionPages = 0; uint? serial = null;
        for (var p = 0; p < bytes.Length;)
        {
            Assert.True(p <= bytes.Length - 27);
            Assert.Equal("OggS", Encoding.ASCII.GetString(bytes, p, 4)); Assert.Equal(0, bytes[p + 4]); var flags = bytes[p + 5]; Assert.Equal(0, flags & ~7);
            Assert.Equal(pending, (flags & 1) != 0); Assert.Equal(p == 0, (flags & 2) != 0); Assert.Equal((uint)pages++, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 18)));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 14)); serial ??= id; Assert.Equal(serial.Value, id);
            var count = bytes[p + 26]; var end = p + 27 + count; var complete = 0;
            Assert.True(end <= bytes.Length);
            for (var i = 0; i < count; i++) { var n = bytes[p + 27 + i]; end += n; pending = n == 255; if (!pending) complete++; }
            Assert.InRange(end, p + 27, bytes.Length); Assert.Equal(end == bytes.Length, (flags & 4) != 0);
            var granule = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(p + 6)); if (complete == 0) { Assert.Equal(-1, granule); emptyCompletionPages++; } else Assert.True(granule >= 0);
            var page = bytes[p..end]; var stored = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22)); page.AsSpan(22, 4).Clear(); Assert.Equal(stored, Crc(page)); p = end;
        }
        Assert.False(pending); Assert.True(emptyCompletionPages > 0);
    }
    [Theory]
    [InlineData("native.ogg", 70000)] [InlineData("native.opus", 70000)]
    [InlineData("native.ogg", 140000)] [InlineData("native.opus", 140000)]
    public void NoCompletedPacketPagesHaveMinusOneGranuleAndAllPageConstraintsHold(string fixture, int size)
    {
        FileCase(Path.GetFileName(fixture), File.ReadAllBytes(Fixture(fixture)), p =>
        {
            Assert.True(TagWriter.Write(p, null, new string('x', size)), TagWriter.Diagnostics().ToJsonString()); AssertEveryOggPage(File.ReadAllBytes(p));
        });
    }

    static void Compete(Action action)
    {
        // Run real filesystem mutations on another thread, deterministically at a commit boundary.
        Task.Run(action).GetAwaiter().GetResult();
    }
    [Fact]
    public void ExternalReplacementAfterPreparationIsNotOverwritten()
    {
        FileCase("race.mp3", File.ReadAllBytes(Fixture("id3v24.mp3")), p =>
        {
            byte[] external = "external replacement"u8.ToArray();
            var operations = new TagWriteCommit.Operations(stage => { if (stage == "before-guard") Compete(() => File.WriteAllBytes(p, external)); });
            Assert.False(TagWriter.Write(p, null, "new lyrics", operations)); Assert.Equal(external, File.ReadAllBytes(p));
        });
    }
    [Fact]
    public void SameBytesAndTimestampsInANewFileIdentityStillRefuse()
    {
        FileCase("race.mp3", File.ReadAllBytes(Fixture("id3v24.mp3")), p =>
        {
            var original = File.ReadAllBytes(p); var mtime = File.GetLastWriteTimeUtc(p); var created = File.GetCreationTimeUtc(p); var replacement = p + ".external";
            File.WriteAllBytes(replacement, original); File.SetLastWriteTimeUtc(replacement, mtime); File.SetCreationTimeUtc(replacement, created);
            var operations = new TagWriteCommit.Operations(stage => { if (stage == "before-guard") Compete(() => File.Move(replacement, p, true)); });
            Assert.False(TagWriter.Write(p, null, "new lyrics", operations)); Assert.Equal(original, File.ReadAllBytes(p));
        });
    }
    [Fact]
    public void WritersAndOrdinaryRenamersAreDeniedAfterFinalHashWhileGuardIsHeld()
    {
        FileCase("race.mp3", File.ReadAllBytes(Fixture("id3v24.mp3")), p =>
        {
            var tested = false; var replacement = p + ".external"; File.WriteAllText(replacement, "external");
            var operations = new TagWriteCommit.Operations(stage =>
            {
                if (stage != "verified-guard") return;
                Compete(() =>
                {
                    Assert.Throws<IOException>(() => File.WriteAllText(p, "external"));
                    var error = Record.Exception(() => File.Move(replacement, p, true)); Assert.True(error is IOException or UnauthorizedAccessException);
                }); tested = true;
            });
            Assert.True(TagWriter.Write(p, null, "new lyrics", operations), TagWriter.Diagnostics().ToJsonString()); Assert.True(tested); Assert.Equal("external", File.ReadAllText(replacement));
        });
    }
    [Theory]
    [InlineData("source-reserved")] [InlineData("before-candidate-rename")]
    public void ExternalCreationInNameHandoffGapWinsAndOriginalIsRetained(string boundary)
    {
        FileCase("race.mp3", File.ReadAllBytes(Fixture("id3v24.mp3")), p =>
        {
            var original = File.ReadAllBytes(p); byte[] external = "new external contents"u8.ToArray(); var raced = false;
            var operations = new TagWriteCommit.Operations(stage => { if (!raced && stage == boundary) { Compete(() => File.WriteAllBytes(p, external)); raced = true; } });
            Assert.False(TagWriter.Write(p, null, "new lyrics", operations)); Assert.True(raced); Assert.Equal(external, File.ReadAllBytes(p));
            var retained = Assert.Single(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(p)!, ".jeekmusic", "tag-writes"), "*.original")); Assert.Equal(original, File.ReadAllBytes(retained));
        });
    }
    [Fact]
    public void ExternalCreationDuringRetryWaitIsCheckedAfterWaitAndNeverOverwritten()
    {
        FileCase("race.mp3", File.ReadAllBytes(Fixture("id3v24.mp3")), p =>
        {
            byte[] external = "external during sleep"u8.ToArray(); var attempts = 0; var delayed = false;
            var operations = new TagWriteCommit.Operations(BeforeCandidateRename: () => { if (attempts++ == 0) throw new IOException("controlled sharing violation", new Win32Exception(32)); }, Delay: _ => { Compete(() => File.WriteAllBytes(p, external)); delayed = true; });
            Assert.False(TagWriter.Write(p, null, "new lyrics", operations)); Assert.True(delayed); Assert.Equal(1, attempts); Assert.Equal(external, File.ReadAllBytes(p));
        });
    }
    [Fact]
    public void FailedCandidateRenameRollsBackOriginalBytesAndTimesWhenNameIsFree()
    {
        FileCase("race.mp3", File.ReadAllBytes(Fixture("id3v24.mp3")), p =>
        {
            var original = File.ReadAllBytes(p); var time = File.GetLastWriteTimeUtc(p);
            var operations = new TagWriteCommit.Operations(BeforeCandidateRename: () => throw new IOException("controlled permanent failure"));
            Assert.False(TagWriter.Write(p, null, "new lyrics", operations)); Assert.Equal(original, File.ReadAllBytes(p)); Assert.Equal(time, File.GetLastWriteTimeUtc(p));
            Assert.Empty(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(p)!, ".jeekmusic", "tag-writes")));
        });
    }
    [Fact]
    public void TransientFailureRechecksAfterWaitAndCanCommitWhenSourceIsUnchanged()
    {
        FileCase("retry.mp3", File.ReadAllBytes(Fixture("id3v24.mp3")), p =>
        {
            var attempts = 0; var waits = 0;
            var operations = new TagWriteCommit.Operations(BeforeCandidateRename: () => { if (attempts++ == 0) throw new IOException("controlled sharing violation", new Win32Exception(32)); }, Delay: _ => waits++);
            Assert.True(TagWriter.Write(p, null, "new lyrics", operations), TagWriter.Diagnostics().ToJsonString());
            Assert.Equal(2, attempts); Assert.Equal(1, waits);
            Assert.Empty(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(p)!, ".jeekmusic", "tag-writes")));
        });
    }
}
