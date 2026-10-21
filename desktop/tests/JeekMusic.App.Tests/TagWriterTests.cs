using JeekMusic.Analysis;
using JeekMusic.Online;
using SkiaSharp;

namespace JeekMusic.App.Tests;

public sealed class TagWriterTests
{
    [Fact]
    public async Task CombinedSavePreservesAudioMetadataAndModificationTime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-tag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "signal.wav");
        try
        {
            Wave(path);
            var metadata = new ATL.Track(path) { Title = "Signal", Artist = "Test Artist", Album = "Test Album", Comment = "Keep me" };
            Assert.True(metadata.Save());
            var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, timestamp);
            var before = await PcmDecoder.Window(path, 0, 1000, CancellationToken.None);
            using var bitmap = new SKBitmap(16, 16);
            bitmap.Erase(SKColors.Teal);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            Assert.True(TagWriter.Write(path, data.ToArray(), "[00:00.00]Test lyrics"));
            var after = await PcmDecoder.Window(path, 0, 1000, CancellationToken.None);
            Assert.Equal(before, after);
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
            var result = new ATL.Track(path);
            Assert.Equal("Signal", result.Title);
            Assert.Equal("Test Artist", result.Artist);
            Assert.Equal("Test Album", result.Album);
            Assert.Equal("Keep me", result.Comment);
            Assert.Equal("[00:00.00]Test lyrics", JeekMusic.Library.TagReader.ReadLyrics(path));
            Assert.NotEmpty(result.EmbeddedPictures);
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
