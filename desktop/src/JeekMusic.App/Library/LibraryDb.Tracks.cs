using Microsoft.Data.Sqlite;

namespace JeekMusic.Library;

/// <summary>音乐文件夹和歌曲的读写。</summary>
public sealed partial class LibraryDb
{
    public const char MultiValueSeparator = '\u0000';

    // ── 音乐文件夹 ──

    public List<LibraryFolder> Folders()
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT id, volume_id, path_in_volume, last_root, display_name, available, added_at, identity FROM library_folder ORDER BY id");
            using var r = cmd.ExecuteReader();
            var list = new List<LibraryFolder>();
            while (r.Read())
                list.Add(new LibraryFolder
                {
                    Id = r.GetInt64(0), VolumeId = r.GetString(1), PathInVolume = r.GetString(2), Root = r.GetString(3),
                    DisplayName = r.GetString(4), Available = r.Bool(5), AddedAt = r.GetInt64(6), Identity = r.GetString(7),
                });
            return list;
        }
    }

    public long AddFolder(string volumeId, string pathInVolume, string root, string displayName)
    {
        lock (Gate)
        {
            Exec("INSERT INTO library_folder(volume_id, path_in_volume, last_root, display_name, available, added_at) VALUES($v, $p, $r, $n, 1, $t)",
                null, ("$v", volumeId), ("$p", pathInVolume), ("$r", root), ("$n", displayName), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return (long)Scalar("SELECT last_insert_rowid()")!;
        }
    }

    public void UpdateFolderLocation(long id, string root, bool available)
    {
        lock (Gate) Exec("UPDATE library_folder SET last_root = $r, available = $a WHERE id = $id", null, ("$r", root), ("$a", available ? 1 : 0), ("$id", id));
    }

    /// <summary>移除音乐文件夹：其中的歌一起删除（播放列表条目引用的是 track_key，保留，显示为不可用，需求 L-2）。</summary>
    public void RemoveFolder(long id)
    {
        lock (Gate) Exec("DELETE FROM library_folder WHERE id = $id", null, ("$id", id));
    }

    // ── 歌曲 ──

    const string TrackColumns =
        "id, track_key, folder_id, dir, file_name, title_tag, title, album, album_artist, album_key, artist_raw, artist_display, " +
        "artist_inferred, artists, track_no, disc_no, year, genre, duration_ms, codec, bitrate, sample_rate, bits_per_sample, channels, " +
        "has_lyrics, has_cover, cue_track_no, start_ms, end_ms, mtime, size, sort_title, sort_file, sort_artist";

    public List<Track> AllTracks()
    {
        lock (Gate)
        {
            using var cmd = Command($"SELECT {TrackColumns} FROM track");
            return ReadTracks(cmd);
        }
    }

    /// <summary>某个文件夹某个目录里的歌（启动时先只读当前视图需要的，设计 §12）。</summary>
    public List<Track> TracksInDir(long folderId, string dir)
    {
        lock (Gate)
        {
            using var cmd = Command($"SELECT {TrackColumns} FROM track WHERE folder_id = $f AND dir = $d");
            cmd.Parameters.AddWithValue("$f", folderId);
            cmd.Parameters.AddWithValue("$d", dir);
            return ReadTracks(cmd);
        }
    }

    static List<Track> ReadTracks(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Track>();
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var artistLists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        string Pool(string value)
        {
            if (strings.TryGetValue(value, out var present)) return present;
            strings[value] = value; return value;
        }
        string? Optional(int index) => r.Str(index) is { } value ? Pool(value) : null;
        while (r.Read())
        {
            var artists = r.GetString(13);
            if (!artistLists.TryGetValue(artists, out var names))
                artistLists[artists] = names = artists.Length == 0 ? [] : artists.Split(MultiValueSeparator).Select(Pool).ToArray();
            list.Add(new Track
            {
                Id = r.GetInt64(0), TrackKey = r.GetString(1), FolderId = r.GetInt64(2), Dir = Pool(r.GetString(3)), FileName = r.GetString(4),
                TitleTag = Optional(5), Title = Pool(r.GetString(6)), Album = Optional(7), AlbumArtist = Optional(8), AlbumKey = Optional(9),
                ArtistRaw = Optional(10), ArtistDisplay = Pool(r.GetString(11)), ArtistInferred = r.Bool(12),
                Artists = names,
                TrackNo = r.Int(14), DiscNo = r.Int(15), Year = r.Int(16), Genre = Optional(17), DurationMs = r.GetInt64(18),
                Codec = Optional(19), Bitrate = r.Int(20), SampleRate = r.Int(21), BitsPerSample = r.Int(22), Channels = r.Int(23),
                HasEmbeddedLyrics = r.Bool(24), HasEmbeddedCover = r.Bool(25),
                CueTrackNo = r.Int(26), StartMs = r.Long(27), EndMs = r.Long(28), Mtime = r.GetInt64(29), Size = r.GetInt64(30),
                SortTitle = Pool(r.GetString(31)), SortFile = r.GetString(32), SortArtist = Pool(r.GetString(33)),
            });
        }
        return list;
    }

    /// <summary>增量扫描用：某个音乐文件夹里已有的文件（CUE 虚拟歌曲按实际文件合并）→ (修改时间, 大小)。</summary>
    public Dictionary<string, (long mtime, long size)> FileStates(long folderId)
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT dir, file_name, mtime, size FROM track WHERE folder_id = $f");
            cmd.Parameters.AddWithValue("$f", folderId);
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) map[r.GetString(0) + r.GetString(1)] = (r.GetInt64(2), r.GetInt64(3));
            return map;
        }
    }

    /// <summary>某个音乐文件夹里记录的 .cue 文件 → (修改时间, 大小)。</summary>
    public Dictionary<string, (long mtime, long size)> SideFiles(long folderId)
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT rel_path, mtime, size FROM side_file WHERE folder_id = $f");
            cmd.Parameters.AddWithValue("$f", folderId);
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
            return map;
        }
    }

    public void SetSideFile(SqliteTransaction tx, long folderId, string relPath, long mtime, long size) =>
        Exec("INSERT OR REPLACE INTO side_file(folder_id, rel_path, mtime, size) VALUES($f, $p, $m, $s)", tx, ("$f", folderId), ("$p", relPath), ("$m", mtime), ("$s", size));

    public void DeleteSideFile(SqliteTransaction tx, long folderId, string relPath) =>
        Exec("DELETE FROM side_file WHERE folder_id = $f AND rel_path = $p", tx, ("$f", folderId), ("$p", relPath));

    /// <summary>重建曲库（需求 L-6）：清空这个音乐文件夹的歌曲和 .cue 记录，之后全量解析。</summary>
    public void ClearFolder(long folderId)
    {
        lock (Gate)
        {
            Exec("DELETE FROM track WHERE folder_id = $f", null, ("$f", folderId));
            Exec("DELETE FROM side_file WHERE folder_id = $f", null, ("$f", folderId));
        }
    }

    /// <summary>某个文件现有的歌曲 id（普通歌曲一条，CUE 整轨多条）：重新解析时沿用 id，播放队列等不受影响。</summary>
    public Dictionary<string, long> ExistingIds(SqliteTransaction tx, long folderId, string dir, string fileName)
    {
        using var cmd = Command("SELECT track_key, id FROM track WHERE folder_id = $f AND dir = $d AND file_name = $n COLLATE NOCASE", tx);
        cmd.Parameters.AddWithValue("$f", folderId);
        cmd.Parameters.AddWithValue("$d", dir);
        cmd.Parameters.AddWithValue("$n", fileName);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, long>();
        while (r.Read()) map[r.GetString(0)] = r.GetInt64(1);
        return map;
    }

    /// <summary>写入一个文件解析出的歌曲（替换这个文件原有的全部条目）。</summary>
    public void ReplaceFile(SqliteTransaction tx, long folderId, string dir, string fileName, IReadOnlyList<Track> tracks)
    {
        var keep = tracks.Where(t => t.Id != 0).Select(t => t.Id).ToHashSet();
        foreach (var (key, id) in ExistingIds(tx, folderId, dir, fileName))
            if (!keep.Contains(id)) Exec("DELETE FROM track WHERE id = $id", tx, ("$id", id));
        foreach (var t in tracks) Upsert(tx, t);
    }

    void Upsert(SqliteTransaction tx, Track t)
    {
        using var cmd = Command(
            (t.Id == 0 ? "INSERT INTO track(" + TrackColumns[4..] + ") VALUES(" : "INSERT OR REPLACE INTO track(" + TrackColumns + ") VALUES($id, ") +
            "$key, $folder, $dir, $file, $titleTag, $title, $album, $albumArtist, $albumKey, $artistRaw, $artistDisplay, $inferred, $artists, " +
            "$trackNo, $discNo, $year, $genre, $duration, $codec, $bitrate, $rate, $bits, $channels, $lyrics, $cover, $cueNo, $start, $end, " +
            "$mtime, $size, $sortTitle, $sortFile, $sortArtist)", tx);
        void P(string name, object? value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        if (t.Id != 0) P("$id", t.Id);
        P("$key", t.TrackKey); P("$folder", t.FolderId); P("$dir", t.Dir); P("$file", t.FileName);
        P("$titleTag", t.TitleTag); P("$title", t.Title); P("$album", t.Album); P("$albumArtist", t.AlbumArtist); P("$albumKey", t.AlbumKey);
        P("$artistRaw", t.ArtistRaw); P("$artistDisplay", t.ArtistDisplay); P("$inferred", t.ArtistInferred ? 1 : 0);
        P("$artists", string.Join(MultiValueSeparator, t.Artists));
        P("$trackNo", t.TrackNo); P("$discNo", t.DiscNo); P("$year", t.Year); P("$genre", t.Genre); P("$duration", t.DurationMs);
        P("$codec", t.Codec); P("$bitrate", t.Bitrate); P("$rate", t.SampleRate); P("$bits", t.BitsPerSample); P("$channels", t.Channels);
        P("$lyrics", t.HasEmbeddedLyrics ? 1 : 0); P("$cover", t.HasEmbeddedCover ? 1 : 0);
        P("$cueNo", t.CueTrackNo); P("$start", t.StartMs); P("$end", t.EndMs); P("$mtime", t.Mtime); P("$size", t.Size);
        P("$sortTitle", t.SortTitle); P("$sortFile", t.SortFile); P("$sortArtist", t.SortArtist);
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除不再存在的文件的全部条目（包括 CUE 虚拟歌曲）。</summary>
    public void DeleteFile(SqliteTransaction tx, long folderId, string relativePath)
    {
        var slash = relativePath.LastIndexOf('\\');
        var dir = slash < 0 ? "" : relativePath[..(slash + 1)];
        var name = relativePath[(slash + 1)..];
        Exec("DELETE FROM track WHERE folder_id = $f AND dir = $d AND file_name = $n COLLATE NOCASE", tx, ("$f", folderId), ("$d", dir), ("$n", name));
    }

    /// <summary>用新规则重算的派生字段（改分隔符、例外名单、文件名推断后；需求 SY-4）。</summary>
    public void UpdateDerived(IEnumerable<Track> tracks)
    {
        lock (Gate)
        {
            using var tx = Begin();
            foreach (var t in tracks)
                Exec("UPDATE track SET title = $t, artist_display = $d, artist_inferred = $i, artists = $a, sort_title = $st, sort_artist = $sa WHERE id = $id", tx,
                    ("$t", t.Title), ("$d", t.ArtistDisplay), ("$i", t.ArtistInferred ? 1 : 0), ("$a", string.Join(MultiValueSeparator, t.Artists)),
                    ("$st", t.SortTitle), ("$sa", t.SortArtist), ("$id", t.Id));
            tx.Commit();
        }
    }

    /// <summary>改名、移动文件后更新路径（track_key 也变了，引用它的表一起改，需求 F-10）。</summary>
    public void MoveTrackFile(SqliteTransaction tx, long oldFolderId, string oldRelativePath, long newFolderId, string newDir, string newFileName,
        Func<string, string> newKeyOf, long mtime, string newSortFile)
    {
        var slash = oldRelativePath.LastIndexOf('\\');
        var oldDir = slash < 0 ? "" : oldRelativePath[..(slash + 1)];
        var oldName = oldRelativePath[(slash + 1)..];
        using var cmd = Command("SELECT id, track_key FROM track WHERE folder_id = $f AND dir = $d AND file_name = $n COLLATE NOCASE", tx);
        cmd.Parameters.AddWithValue("$f", oldFolderId);
        cmd.Parameters.AddWithValue("$d", oldDir);
        cmd.Parameters.AddWithValue("$n", oldName);
        var rows = new List<(long id, string key)>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1)));
        foreach (var (id, oldKey) in rows)
        {
            var newKey = newKeyOf(oldKey);
            Exec("UPDATE track SET folder_id = $f, dir = $d, file_name = $n, track_key = $k, mtime = $m, sort_file = $s WHERE id = $id", tx,
                ("$f", newFolderId), ("$d", newDir), ("$n", newFileName), ("$k", newKey), ("$m", mtime), ("$s", newSortFile), ("$id", id));
            RekeyReferences(tx, oldKey, newKey);
        }
    }

    /// <summary>track_key 变了：播放列表、手动修正、分析结果、歌词偏移、队列跟着改。</summary>
    public void RekeyReferences(SqliteTransaction tx, string oldKey, string newKey)
    {
        foreach (var table in new[] { "playlist_item", "tag_override", "track_tag", "track_features", "lyrics_offset", "lyrics_cache", "queue_item" })
            Exec($"UPDATE OR REPLACE {table} SET track_key = $new WHERE track_key = $old", tx, ("$new", newKey), ("$old", oldKey));
    }
}
