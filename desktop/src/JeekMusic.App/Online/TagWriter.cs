using JeekMusic.Infrastructure;
using JeekMusic.Library;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace JeekMusic.Online;

/// <summary>
/// 把在线封面、歌词写入音乐文件（用户选择"写入音乐文件"时，同移动端 C-4、LY-11）。原生记录局部更新，
/// <b>写完恢复文件的修改时间</b>（需求 C-5："按修改时间排序"和电脑上的同步工具都依赖它）。
/// 封面和歌词都要写时在同一次保存里一起写，只改一次文件。
/// </summary>
public static class TagWriter
{
    static volatile string? _lastFormat, _lastResult, _lastReason;

    /// <summary>Same production safety policy and latest result for development and NativeAOT MCP.</summary>
    public static JsonObject Diagnostics()
    {
        return new JsonObject
        {
            ["policyVersion"] = 3, ["transaction"] = "journaled-guarded-source-and-validated-candidate",
            ["recoveryPending"] = TagWriteRecovery.PendingCount, ["recoveryProblem"] = TagWriteRecovery.LastProblem,
            ["newMp3"] = "ID3v2.4/UTF-8", ["existingId3v23"] = "retain-v2.3/UTF-16-targets",
            ["preservation"] = "opaque non-target records; native container/audio validation; reject unsafe input",
            ["maximumFileBytes"] = TagWriteFrames.MaxFileBytes,
            ["lastFormat"] = _lastFormat, ["lastResult"] = _lastResult, ["lastReason"] = _lastReason
        };
    }

    /// <summary>写入；<paramref name="coverJpeg"/>、<paramref name="lyrics"/> 为 null 的不动。成功返回 true。</summary>
    public static bool Write(string path, byte[]? coverJpeg, string? lyrics)
        => Write(path, coverJpeg, lyrics, null);

    internal static bool Write(string path, byte[]? coverJpeg, string? lyrics, TagWriteCommit.Operations? commitOperations)
    {
        if (coverJpeg is null && lyrics is null) return true;
        TagReader.Configure();
        lock (TagWriteCommit.Gate)
        {
            string? temporary = null;
            try
            {
                _lastFormat = null; _lastResult = "preparing"; _lastReason = null;
                if (!CanWrite(path)) throw new NotSupportedException("Unsupported embedded-tag extension");
                if (!TagWriteRecovery.RecoverBeforeScan(Path.GetDirectoryName(Path.GetFullPath(path)))) throw new IOException("Pending tag transaction needs recovery; see tagWriting/get_logs");
                // Reject invalid scalar input instead of replacing unpaired surrogates with '?'.
                if (lyrics is not null)
                {
                    _ = TagWriteFrames.Utf8.GetByteCount(lyrics);
                    if (lyrics.Contains('\0')) throw TagWriteFrames.Unsafe("NUL in a single lyric slot");
                }
                if (coverJpeg is not null && (coverJpeg.Length < 4 || coverJpeg[0] != 255 || coverJpeg[1] != 216))
                    throw new NotSupportedException("Cover must be JPEG");
                if (coverJpeg is not null)
                {
                    using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(coverJpeg));
                    if (codec is null || codec.EncodedFormat != SkiaSharp.SKEncodedImageFormat.Jpeg) throw TagWriteFrames.Unsafe("invalid JPEG data");
                }
                var original = new FileInfo(path);
                var mtime = original.LastWriteTimeUtc;
                var size = original.Length;
                if (size > TagWriteFrames.MaxFileBytes) throw TagWriteFrames.Unsafe("file exceeds bounded edit limit (256 MiB)");
                // Keep other writers from opening the contents while staging; allow atomic rename.
                // Recheck path identity/signature and SHA too, as another process can replace a path.
                using var sourceLease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var sourceStamp = TagWriteCommit.Capture(sourceLease.SafeFileHandle);
                var sourceHash = SHA256.HashData(sourceLease);
                _ = TagWriteMetadata.Read(path, sourceLease.SafeFileHandle, checkShortName: true); // refuse unsupported filesystem metadata before editing
                var staging = Path.Combine(original.DirectoryName!, ".jeekmusic", "tag-writes");
                Directory.CreateDirectory(staging);
                temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + original.Extension);
                File.Copy(path, temporary);
                var before = TagWriteFrames.Read(temporary);
                if (!SHA256.HashData(before).AsSpan().SequenceEqual(sourceHash)) throw new IOException("Source changed during staging");
                var format = Detect(before); _lastFormat = format;
                var id3v1 = format is "mp3" or "ape" ? ReadId3v1(temporary) : null;
                byte[]? edited = format switch
                {
                    "mp3" => TagWriteFrames.EditId3(before, coverJpeg, lyrics),
                    "riff" => TagWriteFrames.EditRiff(before, coverJpeg, lyrics),
                    "flac" => TagWriteFrames.EditFlac(before, coverJpeg, lyrics),
                    "ape" => TagWriteFrames.EditApe(before, coverJpeg, lyrics),
                    "ogg" => TagWriteFrames.EditOgg(before, coverJpeg, lyrics),
                    _ => null
                };
                byte[] validated;
                if (edited is not null) { File.WriteAllBytes(temporary, edited); validated = edited; }
                else
                {
                    var track = new ATL.Track(temporary);
                    // Only these elementary stream formats use the legacy footer. ATL also declares
                    // ID3v1 support for WAV, where a trailing TAG can actually be PCM inside RIFF.
                    var hasId3v1 = track.MetadataFormats.Any(f => f.ID == (int)ATL.AudioData.MetaDataIOFactory.TagType.ID3V1);
                    if (hasId3v1)
                        throw new NotSupportedException("容器末尾的 ID3v1 签名可能属于音频数据，取消写入以保留原文件");
                    var changes = new ATL.TagData();
                    if (coverJpeg is not null)
                    {
                        foreach (var picture in track.EmbeddedPictures.Where(p => p.PicType is ATL.PictureInfo.PIC_TYPE.Front or ATL.PictureInfo.PIC_TYPE.Generic))
                            changes.Pictures.Add(new ATL.PictureInfo(picture) { MarkedForDeletion = true });
                        changes.Pictures.Add(ATL.PictureInfo.fromBinaryData(coverJpeg, ATL.PictureInfo.PIC_TYPE.Front));
                    }
                    if (lyrics is not null)
                    {
                        if (track.Lyrics.Count > 1) throw TagWriteFrames.Unsafe("multiple native lyric slots");
                        changes.Lyrics = [new ATL.LyricsInfo { UnsynchronizedLyrics = lyrics, Format = ATL.LyricsInfo.LyricsFormat.UNSYNCHRONIZED }];
                    }
                    // Use ATL's public native partial-update API, not Track.Save/toTagData, which
                    // projects dates and every unrelated field through the high-level object model.
                    var audio = ATL.AudioData.AudioDataIOFactory.GetFromFormat(temporary, track.AudioFormat);
                    using (var stream = File.Open(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        var read = new ATL.AudioData.IO.MetaDataIO.ReadTagParams(true, true) { PrepareForWriting = true };
                        if (!audio.Read(stream, new ATL.AudioData.AudioDataManager.SizeInfo { FileSize = stream.Length }, read)) throw new IOException("Native tag read failed");
                        if (!((ATL.AudioData.IMetaDataIO)audio).Write(stream, changes, new ATL.AudioData.IO.MetaDataIO.WriteTagParams())) throw new IOException("Native tag serializer failed");
                    }
                    var after = TagWriteFrames.Read(temporary);
                    if (format == "ogg") { TagWriteFrames.CheckOgg(before, after, coverJpeg is not null, lyrics is not null); validated = after; }
                    else if (format == "mp4") { validated = TagWriteFrames.RetainMp4NonTargets(before, after, coverJpeg, lyrics); File.WriteAllBytes(temporary, validated); }
                    else if (format == "asf")
                    {
                        validated = TagWriteFrames.RetainAsfNonTargets(before, after, coverJpeg, lyrics); File.WriteAllBytes(temporary, validated);
                    }
                    else throw new NotSupportedException("Unsupported native envelope");
                }
                original.Refresh();
                if (original.Length != size || original.LastWriteTimeUtc != mtime) throw new IOException("文件在写入期间已变化");
                using (var current = File.OpenRead(path))
                    if (!SHA256.HashData(current).AsSpan().SequenceEqual(sourceHash)) throw new IOException("Source SHA256 changed before replacement");
                if (id3v1 is not null && !id3v1.AsSpan().SequenceEqual(ReadId3v1(temporary))) throw new IOException("Legacy footer changed");
                var validatedHash = SHA256.HashData(validated);
                temporary = TagWriteMetadata.Prepare(path, temporary, validatedHash);
                File.SetLastWriteTimeUtc(temporary, mtime);
                File.SetCreationTimeUtc(temporary, original.CreationTimeUtc);
                TagWriteCommit.Stamp candidateIdentity;
                using (var candidate = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    if (!SHA256.HashData(candidate).AsSpan().SequenceEqual(validatedHash)) throw new IOException("Candidate no longer matches validated content");
                    candidate.Flush(true); candidateIdentity = TagWriteCommit.Capture(candidate.SafeFileHandle);
                }
                sourceLease.Dispose(); // Commit acquires a fresh identity-checked DELETE/read guard.
                TagWriteCommit.Commit(path, temporary, sourceStamp, sourceHash, candidateIdentity, validatedHash, commitOperations);
                _lastResult = "saved";
                Log.Info("tagwriter", $"写入完成：{path} cover={coverJpeg is not null} lyrics={lyrics is not null} id3v1Preserved={id3v1 is not null}");
                Log.Info("tagwriter", $"preservation=verified policy=3 format={format}");
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException or System.Security.SecurityException)
            {
                _lastResult = "rejected"; _lastReason = e.Message;
                Log.Warn("tagwriter", $"写入失败：{path}", e);
                return false;
            }
            finally
            {
                if (temporary is not null)
                    try { File.Delete(temporary); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("tagwriter", "清理临时文件失败", e); }
            }
        }
    }

    static string Detect(byte[] b)
    {
        if (b.Length < 12) throw TagWriteFrames.Unsafe("truncated container");
        if (b.AsSpan(0, 4).SequenceEqual("fLaC"u8)) return "flac";
        if (b.AsSpan(0, 4).SequenceEqual("RIFF"u8) || b.AsSpan(0, 4).SequenceEqual("FORM"u8)) return "riff";
        if (b.AsSpan(0, 4).SequenceEqual("OggS"u8)) return "ogg";
        if (b.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return "mp4";
        if (new Guid(b.AsSpan(0, 16)) == Guid.Parse("75B22630-668E-11CF-A6D9-00AA0062CE6C")) return "asf";
        if (b.AsSpan(0, 4).SequenceEqual("MAC "u8) || b.AsSpan(0, 4).SequenceEqual("wvpk"u8) || b.AsSpan(0, 4).SequenceEqual("tBaK"u8) || b.AsSpan(0, 4).SequenceEqual("MPCK"u8) || b.AsSpan(0, 3).SequenceEqual("MP+"u8)) return "ape";
        var tag = TagWriteFrames.ParseId3(b, true);
        if (tag.End < b.Length - 1 && b[tag.End] == 255 && (b[tag.End + 1] & 0xe0) == 0xe0) return "mp3";
        throw TagWriteFrames.Unsafe("unrecognized audio envelope");
    }

    static byte[]? ReadId3v1(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 128) return null;
        file.Seek(-128, SeekOrigin.End);
        var tag = new byte[128];
        file.ReadExactly(tag);
        return tag.AsSpan(0, 3).SequenceEqual("TAG"u8) ? tag : null;
    }

    /// <summary>能不能写入这种格式（CUE 整轨的一首歌不能写进整个文件，由调用方判断）。</summary>
    public static bool CanWrite(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp3" or ".flac" or ".m4a" or ".mp4" or ".ogg" or ".oga" or ".opus" or ".wma" or ".ape" or ".wv" or ".aif" or ".aiff" or ".wav" or ".tak" or ".mpc";
    }
}
