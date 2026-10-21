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
                    original.Refresh();
                    if (original.Length != size || original.LastWriteTimeUtc != mtime) throw new IOException("文件在写入期间已变化");
                    File.SetLastWriteTimeUtc(temporary, mtime);
                    File.SetCreationTimeUtc(temporary, original.CreationTimeUtc);
                    File.Replace(temporary, path, null);
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

    /// <summary>能不能写入这种格式（CUE 整轨的一首歌不能写进整个文件，由调用方判断）。</summary>
    public static bool CanWrite(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp3" or ".flac" or ".m4a" or ".mp4" or ".ogg" or ".oga" or ".opus" or ".wma" or ".ape" or ".wv" or ".aif" or ".aiff" or ".wav" or ".tak" or ".mpc";
    }
}
