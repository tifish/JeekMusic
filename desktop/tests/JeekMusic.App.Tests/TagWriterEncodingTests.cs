using System.Security.Cryptography;
using System.Text;
using JeekMusic.Core.Text;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.Online;
using SkiaSharp;

namespace JeekMusic.App.Tests;

public sealed class TagWriterEncodingTests
{
    const string NewLyrics = "[00:00.00]中文歌词 café Ελληνικά 𠮷😀";

    public static IEnumerable<object[]> Cases()
    {
        foreach (var encoding in new[] { "utf8", "utf16", "gbk", "latin1" })
        foreach (var operation in new[] { "cover", "lyrics", "combined" })
        foreach (var legacyTag in new[] { false, true })
            yield return [encoding, operation, legacyTag];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EmbeddingPreservesTextLegacyBytesAndAudio(string encoding, string operation, bool legacyTag)
    {
        TagReader.Configure();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var dir = Path.Combine(Path.GetTempPath(), "jeek-tag-encoding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "signal.mp3");
        try
        {
            var (codec, marker, version) = encoding switch
            {
                "utf16" => (Encoding.Unicode, (byte)1, (byte)3),
                "gbk" => (Encoding.GetEncoding(936), (byte)0, (byte)3),
                "latin1" => (Encoding.Latin1, (byte)0, (byte)3),
                _ => (Encoding.UTF8, (byte)3, (byte)4),
            };
            string[] values = encoding switch
            {
                "latin1" => ["Déjà vu", "Björk", "Café del Mar", "Keep café", "[00:00.00]Déjà vu"],
                "gbk" => ["花火 (Live版)", "周深", "时光音乐会2 第6期", "保留备注", "[00:00.00]保留歌词"],
                _ => ["花火 (Live版) · Café Ελληνικά 𠮷😀", "周深 · Björk", "时光音乐会2 第6期 · العربية", "保留备注 café", "[00:00.00]保留歌词 𠮷😀"],
            };
            var preamble = marker == 1 ? codec.GetPreamble() : [];
            var frames = new MemoryStream();
            foreach (var (id, value) in new[] { ("TIT2", values[0]), ("TPE1", values[1]), ("TALB", values[2]), ("COMM", values[3]), ("USLT", values[4]) })
            {
                var text = preamble.Concat(codec.GetBytes(value)).ToArray();
                var prefix = id is "COMM" or "USLT" ? "eng"u8.ToArray().Concat(preamble).Concat(codec.GetBytes("\0")).ToArray() : [];
                var body = new[] { marker }.Concat(prefix).Concat(text).ToArray();
                frames.Write(Encoding.ASCII.GetBytes(id));
                frames.Write(Size(body.Length, version == 4));
                frames.Write([0, 0]);
                frames.Write(body);
            }
            var audio = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tag-writer-silence.mp3"));
            var v1 = "TAG"u8.ToArray().Concat(Enumerable.Repeat((byte)'X', 125)).ToArray();
            // Leave valid legacy numeric fields, and make the rest deliberately different from ID3v2.
            "2020"u8.CopyTo(v1.AsSpan(93));
            v1[127] = 255;
            File.WriteAllBytes(path, "ID3"u8.ToArray().Concat(new[] { version, (byte)0, (byte)0 })
                .Concat(Size((int)frames.Length, true)).Concat(frames.ToArray()).Concat(audio).Concat(legacyTag ? v1 : []).ToArray());
            var before = File.ReadAllBytes(path);
            var beforeText = TagReader.Read(path);
            var beforeLyrics = TagReader.ReadLyrics(path);
            var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, timestamp);
            using var bitmap = new SKBitmap(8, 8);
            bitmap.Erase(SKColors.Teal);
            using var image = SKImage.FromBitmap(bitmap);
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85);

            Assert.True(TagWriter.Write(path, operation == "lyrics" ? null : jpeg.ToArray(), operation == "cover" ? null : NewLyrics));

            var after = File.ReadAllBytes(path);
            var afterText = TagReader.Read(path);
            Assert.Equal(beforeText.Title, afterText.Title);
            Assert.Equal(beforeText.Artists, afterText.Artists);
            Assert.Equal(beforeText.Album, afterText.Album);
            Assert.Equal(values[0], CharsetFixer.Fix(afterText.Title!));
            Assert.Equal(values[1], CharsetFixer.Fix(afterText.Artists.Single()));
            Assert.Equal(values[2], CharsetFixer.Fix(afterText.Album!));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
            Assert.Equal(SHA256.HashData(audio), SHA256.HashData(Payload(after)));
            Assert.Equal(Payload(before), Payload(after));
            Assert.Equal(legacyTag, after.AsSpan(after.Length - 128, 3).SequenceEqual("TAG"u8));
            if (legacyTag) Assert.Equal(v1, after[^128..]);
            foreach (var id in new[] { "TIT2", "TPE1", "TALB", "COMM" })
                Assert.Equal(FrameText(before, id), FrameText(after, id));
            // ATL can surround an existing USLT with newlines when serializing it.
            Assert.Equal((operation == "cover" ? beforeLyrics : NewLyrics)?.Trim('\r', '\n'), TagReader.ReadLyrics(path)?.Trim('\r', '\n'));
            Assert.Equal((operation == "cover" ? FrameText(before, "USLT") : NewLyrics).Trim('\r', '\n'), FrameText(after, "USLT").Trim('\r', '\n'));
            Assert.Equal(operation != "lyrics", afterText.HasCover);
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, ".jeekmusic", "tag-writes")));
            Log.Flush();
            Assert.Contains($"写入完成：{path} cover={operation != "lyrics"} lyrics={operation != "cover"} id3v1Preserved={legacyTag}",
                File.ReadAllText(Path.Combine(AppPaths.LogsDir, "app.log")));
        }
        finally { Directory.Delete(dir, true); }
    }

    static byte[] Size(int value, bool synchsafe) => synchsafe
        ? [(byte)(value >> 21 & 127), (byte)(value >> 14 & 127), (byte)(value >> 7 & 127), (byte)(value & 127)]
        : [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    static int Size(ReadOnlySpan<byte> value, bool synchsafe) => synchsafe
        ? value[0] << 21 | value[1] << 14 | value[2] << 7 | value[3]
        : value[0] << 24 | value[1] << 16 | value[2] << 8 | value[3];

    static byte[] Payload(byte[] file)
    {
        var start = 10 + Size(file.AsSpan(6, 4), true);
        var end = file.AsSpan(file.Length - 128, 3).SequenceEqual("TAG"u8) ? file.Length - 128 : file.Length;
        return file[start..end];
    }

    // Independent ID3 byte inspection; do not use ATL to validate ATL's serialized text.
    static string FrameText(byte[] file, string id)
    {
        var end = 10 + Size(file.AsSpan(6, 4), true);
        for (var offset = 10; offset + 10 <= end;)
        {
            var length = Size(file.AsSpan(offset + 4, 4), file[3] == 4);
            var code = Encoding.ASCII.GetString(file, offset, 4);
            var body = file.AsSpan(offset + 10, length);
            if (code == id)
            {
                var codec = body[0] switch { 0 => Encoding.Latin1, 1 => Encoding.Unicode, 2 => Encoding.BigEndianUnicode, 3 => Encoding.UTF8, _ => throw new InvalidDataException() };
                var text = codec.GetString(body[(id is "COMM" or "USLT" ? 4 : 1)..]);
                if (id is "COMM" or "USLT") text = text[(text.IndexOf('\0') + 1)..];
                return text.Trim('\uFEFF', '\0');
            }
            offset += 10 + length;
        }
        throw new InvalidDataException($"Missing {id}");
    }
}
