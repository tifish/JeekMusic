namespace JeekMusic.Library;

/// <summary>在线封面、歌词写入音乐文件后的记录。</summary>
public sealed partial class LibraryDb
{
    /// <summary>
    /// 写入后记下文件里有封面 / 歌词，以及新的大小和修改时间（修改时间保持不变，大小变了）：
    /// 这样下次增量扫描不会因为大小变了重新解析，内嵌歌词也马上能读到（LyricsService 只读标了有歌词的文件）。
    /// </summary>
    public void MarkEmbedded(long folderId, string dir, string fileName, bool cover, bool lyrics, long size, long mtime)
    {
        InTransaction(() =>
        {
            Exec("UPDATE track_features SET signature = $s WHERE EXISTS (SELECT 1 FROM track t WHERE t.track_key = track_features.track_key " +
                 "AND t.folder_id = $f AND t.dir = $d AND t.file_name = $n COLLATE NOCASE AND track_features.signature = t.mtime || ':' || t.size)", null,
                ("$s", $"{mtime}:{size}"), ("$f", folderId), ("$d", dir), ("$n", fileName));
            Exec("UPDATE track SET has_cover = has_cover OR $c, has_lyrics = has_lyrics OR $l, size = $s, mtime = $m " +
                 "WHERE folder_id = $f AND dir = $d AND file_name = $n COLLATE NOCASE", null,
                ("$c", cover ? 1 : 0), ("$l", lyrics ? 1 : 0), ("$s", size), ("$m", mtime), ("$f", folderId), ("$d", dir), ("$n", fileName));
        });
    }

    /// <summary>App 内缓存了歌词内容的歌（"已下载的歌词改为写入文件"用，需求 LY-14）。</summary>
    public Dictionary<string, string> CachedLyricsWithContent()
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT track_key, content FROM lyrics_cache WHERE content IS NOT NULL");
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, string>();
            while (r.Read()) map[r.GetString(0)] = r.GetString(1);
            return map;
        }
    }

    public void ClearLyricsCache()
    {
        lock (Gate) Exec("DELETE FROM lyrics_cache");
    }
}
