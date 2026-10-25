using JeekMusic.Infrastructure;
using JeekMusic.Library;

namespace JeekMusic.Online;

/// <summary>
/// 把在线封面、歌词写入音乐文件（用户选择"写入音乐文件"时，同移动端 C-4、LY-11）。用 ATL 写，
/// <b>写完恢复文件的修改时间</b>（需求 C-5："按修改时间排序"和电脑上的同步工具都依赖它）。
/// 封面和歌词都要写时在同一次保存里一起写，只改一次文件。
/// </summary>
public static class TagWriter
{
    static readonly Lock Gate = new();

    /// <summary>写入；<paramref name="coverJpeg"/>、<paramref name="lyrics"/> 为 null 的不动。成功返回 true。</summary>
    public static bool Write(string path, byte[]? coverJpeg, string? lyrics)
    {
        if (coverJpeg is null && lyrics is null) return true;
        TagReader.Configure();
        lock (Gate)
        {
            string? temporary = null;
            try
            {
                var original = new FileInfo(path);
                var mtime = original.LastWriteTimeUtc;
                var size = original.Length;
                // ATL may rewrite the entire file. Stage on the same volume, then replace only after a successful save.
                var staging = Path.Combine(original.DirectoryName!, ".jeekmusic", "tag-writes");
                Directory.CreateDirectory(staging);
                temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + original.Extension);
                File.Copy(path, temporary);
                var track = new ATL.Track(temporary);
                // Only these elementary stream formats use the legacy footer. ATL also declares
                // ID3v1 support for WAV, where a trailing TAG can actually be PCM inside RIFF.
                var footerFormat = original.Extension.ToLowerInvariant() is ".mp3" or ".ape" or ".wv" or ".tak" or ".mpc";
                var hasId3v1 = track.MetadataFormats.Any(f => f.ID == (int)ATL.AudioData.MetaDataIOFactory.TagType.ID3V1);
                if (hasId3v1 && !footerFormat)
                    throw new NotSupportedException("容器末尾的 ID3v1 签名可能属于音频数据，取消写入以保留原文件");
                var id3v1 = footerFormat ? ReadId3v1(temporary) : null;
                if (coverJpeg is not null)
                {
                    // 换掉原来的封面（正面），其他类型的图片保留
                    for (var i = track.EmbeddedPictures.Count - 1; i >= 0; i--)
                        if (track.EmbeddedPictures[i].PicType is ATL.PictureInfo.PIC_TYPE.Front or ATL.PictureInfo.PIC_TYPE.Generic)
                            track.EmbeddedPictures.RemoveAt(i);
                    track.EmbeddedPictures.Insert(0, ATL.PictureInfo.fromBinaryData(coverJpeg, ATL.PictureInfo.PIC_TYPE.Front));
                }
                if (lyrics is not null)
                {
                    // 歌词原样写成文字（LRC 带时间轴的也是），读的时候再解析，和移动端写 LYRICS 字段一样
                    track.Lyrics = [new ATL.LyricsInfo { UnsynchronizedLyrics = lyrics.Replace("\r\n", "\n"), Format = ATL.LyricsInfo.LyricsFormat.UNSYNCHRONIZED }];
                }
                var ok = track.Save();
                if (!ok) Log.Warn("tagwriter", $"写入失败：{path}");
                if (ok)
                {
                    // ID3v1 has no artwork/lyrics and cannot represent Unicode. ATL synchronizes the
                    // ID3v2 text back into it on Save; retain the user's original legacy bytes instead.
                    RestoreId3v1(temporary, id3v1);
                    original.Refresh();
                    if (original.Length != size || original.LastWriteTimeUtc != mtime) throw new IOException("文件在写入期间已变化");
                    File.SetLastWriteTimeUtc(temporary, mtime);
                    File.SetCreationTimeUtc(temporary, original.CreationTimeUtc);
                    File.Replace(temporary, path, null);
                    Log.Info("tagwriter", $"写入完成：{path} cover={coverJpeg is not null} lyrics={lyrics is not null} id3v1Preserved={id3v1 is not null}");
                }
                return ok;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
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

    static byte[]? ReadId3v1(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 128) return null;
        file.Seek(-128, SeekOrigin.End);
        var tag = new byte[128];
        file.ReadExactly(tag);
        return tag.AsSpan(0, 3).SequenceEqual("TAG"u8) ? tag : null;
    }

    static void RestoreId3v1(string path, byte[]? tag)
    {
        if (tag is null) return;
        using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Span<byte> marker = stackalloc byte[3];
        var hasTag = false;
        if (file.Length >= 128)
        {
            file.Seek(-128, SeekOrigin.End);
            file.ReadExactly(marker);
            hasTag = marker.SequenceEqual("TAG"u8);
        }
        file.Seek(hasTag ? -128 : 0, SeekOrigin.End);
        file.Write(tag);
    }

    /// <summary>能不能写入这种格式（CUE 整轨的一首歌不能写进整个文件，由调用方判断）。</summary>
    public static bool CanWrite(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp3" or ".flac" or ".m4a" or ".mp4" or ".ogg" or ".oga" or ".opus" or ".wma" or ".ape" or ".wv" or ".aif" or ".aiff" or ".wav" or ".tak" or ".mpc";
    }
}
