using JeekMusic.Analysis;
using JeekMusic.Online;
using SkiaSharp;

namespace JeekMusic.App.Tests;

[Collection("Tag writing")]
public sealed class TagWriterTests
{
    [Theory]
    [InlineData("flac")]
    [InlineData("m4a")]
    [InlineData("ogg")]
    public async Task NativeMetadataFormatsPreserveUnicodeAndDecodedAudio(string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-tag-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "signal." + extension);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tag-writer-silence." + extension), path);
            var metadata = new ATL.Track(path);
            var original = File.ReadAllBytes(path);
            var before = extension == "ogg" ? null : await PcmDecoder.Window(path, 0, 1000, CancellationToken.None);
            using var bitmap = new SKBitmap(8, 8);
            bitmap.Erase(SKColors.Teal);
            using var image = SKImage.FromBitmap(bitmap);
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            const string lyrics = "[00:00.00]中文歌词 Ελληνικά 𠮷😀";
            Assert.True(TagWriter.Write(path, jpeg.ToArray(), lyrics), TagWriter.Diagnostics().ToJsonString());
            if (extension == "ogg")
            {
                // Ogg repagination changes page bytes; compare every codec/audio packet except
                // the Vorbis comment packet. This also avoids mpv short-window pipe timing.
                var expected = OggPackets(original);
                var actual = OggPackets(File.ReadAllBytes(path));
                Assert.Equal(expected.Count, actual.Count);
                for (var i = 0; i < expected.Count; i++) if (i != 1) Assert.Equal(expected[i], actual[i]);
            }
            else Assert.Equal(before, await PcmDecoder.Window(path, 0, 1000, CancellationToken.None));
            var result = JeekMusic.Library.TagReader.Read(path);
            Assert.Equal("花火 café 𠮷😀", result.Title);
            Assert.Equal("周深 · Björk", Assert.Single(result.Artists));
            Assert.Equal("时光音乐会 · العربية", result.Album);
            Assert.Equal(metadata.Comment, new ATL.Track(path).Comment);
            Assert.True(result.HasCover);
            Assert.Equal(lyrics, JeekMusic.Library.TagReader.ReadLyrics(path)?.Trim('\r', '\n'));
        }
        finally { Directory.Delete(dir, true); }
    }

    static List<byte[]> OggPackets(byte[] bytes)
    {
        var packets = new List<byte[]>();
        using var packet = new MemoryStream();
        for (var offset = 0; offset < bytes.Length;)
        {
            Assert.True(bytes.AsSpan(offset, 4).SequenceEqual("OggS"u8));
            var segments = bytes[offset + 26];
            var start = offset + 27 + segments;
            for (var i = 0; i < segments; i++)
            {
                var length = bytes[offset + 27 + i];
                packet.Write(bytes, start, length);
                start += length;
                if (length < 255) { packets.Add(packet.ToArray()); packet.SetLength(0); }
            }
            offset = start;
        }
        Assert.Equal(0, packet.Length);
        return packets;
    }

    [Fact]
    public async Task CombinedSavePreservesAudioMetadataAndModificationTime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-tag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "signal.wav");
        try
        {
            Wave(path);
            var metadata = new ATL.Track(path) { Title = "花火 café 𠮷😀", Artist = "周深 · Björk", Album = "时光音乐会 · العربية", Comment = "保留备注" };
            Assert.True(metadata.Save());
            var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, timestamp);
            var before = await PcmDecoder.Window(path, 0, 1000, CancellationToken.None);
            using var bitmap = new SKBitmap(16, 16);
            bitmap.Erase(SKColors.Teal);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            Assert.True(TagWriter.Write(path, data.ToArray(), "[00:00.00]中文歌词 Ελληνικά 𠮷😀"));
            var after = await PcmDecoder.Window(path, 0, 1000, CancellationToken.None);
            Assert.Equal(before, after);
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
            var result = new ATL.Track(path);
            Assert.Equal(metadata.Title, result.Title);
            Assert.Equal(metadata.Artist, result.Artist);
            Assert.Equal(metadata.Album, result.Album);
            Assert.Equal(metadata.Comment, result.Comment);
            Assert.Equal("[00:00.00]中文歌词 Ελληνικά 𠮷😀", JeekMusic.Library.TagReader.ReadLyrics(path));
            Assert.NotEmpty(result.EmbeddedPictures);
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, ".jeekmusic", "tag-writes")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WavAudioEndingInTagSignatureIsNotRestoredAsId3v1()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-tag-footer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "signal.wav");
        try
        {
            Wave(path);
            var before = File.ReadAllBytes(path);
            "TAG"u8.CopyTo(before.AsSpan(before.Length - 128));
            File.WriteAllBytes(path, before);
            // The bounded RIFF editor never interprets PCM as a footer, even if ATL would.
            Assert.True(TagWriter.Write(path, null, "[00:00.00]保留所有音频字节"));
            var after = File.ReadAllBytes(path);
            Assert.Equal(after.Length, BitConverter.ToInt32(after, 4) + 8);
            using var reader = new BinaryReader(new MemoryStream(after));
            reader.BaseStream.Position = 12;
            byte[]? pcm = null;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                var id = reader.ReadBytes(4);
                var size = reader.ReadInt32();
                if (id.AsSpan().SequenceEqual("data"u8)) pcm = reader.ReadBytes(size);
                else reader.BaseStream.Position += size;
                reader.BaseStream.Position += size % 2;
            }
            Assert.Equal(before[44..], pcm);
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, ".jeekmusic", "tag-writes")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LockedFileIsNotPartiallyOverwritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-tag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "signal.wav");
        try
        {
            Wave(path);
            var original = File.ReadAllBytes(path);
            var timestamp = File.GetLastWriteTimeUtc(path);
            using (var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.False(TagWriter.Write(path, null, "New lyrics"));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    static void Wave(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        const int rate = 16000;
        writer.Write("RIFF"u8); writer.Write(36 + rate * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(rate * 2);
        for (int i = 0; i < rate; i++) writer.Write((short)(Math.Sin(i * Math.PI * 2 * 440 / rate) * 8192));
    }
}
