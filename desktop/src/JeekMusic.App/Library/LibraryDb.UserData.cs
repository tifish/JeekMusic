using Microsoft.Data.Sqlite;

namespace JeekMusic.Library;

public sealed record Playlist(long Id, string Uid, string Name, long CreatedAt, long UpdatedAt);

/// <summary>播放列表里的一条。<see cref="ForeignRef"/> 不为 null 时是对不上本机歌曲的条目（来自其他设备，需求 SY-6）。</summary>
public sealed record PlaylistItem(long Id, int Position, string TrackKey, string? ForeignRef);

public sealed record TagOverride(string TrackKey, string Dimension, string? Value, long UpdatedAt);

public sealed record LyricsOffset(string TrackKey, int OffsetMs, long UpdatedAt);

public sealed record NavItem(string Type, string? Ref);

public sealed record QueueItemRow(string TrackKey, bool Manual);

/// <summary>智能标签：分析结果（JSON）和物化的标签。</summary>
public sealed record FeaturesRow(string TrackKey, string Signature, int AnalyzerVersion, string? Data, bool Failed);

public sealed partial class LibraryDb
{
    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── 播放列表 ──

    public List<Playlist> Playlists()
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT id, uid, name, created_at, updated_at FROM playlist ORDER BY name COLLATE NOCASE");
            using var r = cmd.ExecuteReader();
            var list = new List<Playlist>();
            while (r.Read()) list.Add(new Playlist(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
            return list;
        }
    }

    public List<PlaylistItem> PlaylistItems(long playlistId)
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT id, position, track_key, foreign_ref FROM playlist_item WHERE playlist_id = $p ORDER BY position");
            cmd.Parameters.AddWithValue("$p", playlistId);
            using var r = cmd.ExecuteReader();
            var list = new List<PlaylistItem>();
            while (r.Read()) list.Add(new PlaylistItem(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.Str(3)));
            return list;
        }
    }

    /// <summary>所有播放列表的条目，按播放列表分组（快照用）。</summary>
    public Dictionary<long, List<PlaylistItem>> AllPlaylistItems()
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT playlist_id, id, position, track_key, foreign_ref FROM playlist_item ORDER BY playlist_id, position");
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<long, List<PlaylistItem>>();
            while (r.Read())
            {
                var pid = r.GetInt64(0);
                if (!map.TryGetValue(pid, out var list)) map[pid] = list = new();
                list.Add(new PlaylistItem(r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.Str(4)));
            }
            return map;
        }
    }

    public long CreatePlaylist(string name, string? uid = null, long? updatedAt = null)
    {
        lock (Gate)
        {
            var now = Now();
            Exec("INSERT INTO playlist(uid, name, created_at, updated_at) VALUES($u, $n, $c, $t)", null,
                ("$u", uid ?? Guid.NewGuid().ToString("N")), ("$n", name), ("$c", now), ("$t", updatedAt ?? now));
            return (long)Scalar("SELECT last_insert_rowid()")!;
        }
    }

    public void RenamePlaylist(long id, string name)
    {
        lock (Gate) Exec("UPDATE playlist SET name = $n, updated_at = $t WHERE id = $id", null, ("$n", name), ("$t", Now()), ("$id", id));
    }

    /// <summary>删除播放列表，并记下删除记录（同步时别的设备也删掉它，需求 SY-3）。</summary>
    public void DeletePlaylist(long id, long? deletedAt = null)
    {
        lock (Gate)
        {
            using var tx = Begin();
            var uid = Scalar("SELECT uid FROM playlist WHERE id = $id", tx, ("$id", id)) as string;
            if (uid is not null)
                Exec("INSERT OR REPLACE INTO tombstone(kind, uid, deleted_at) VALUES('PLAYLIST', $u, $t)", tx, ("$u", uid), ("$t", deletedAt ?? Now()));
            Exec("DELETE FROM playlist WHERE id = $id", tx, ("$id", id));
            tx.Commit();
        }
    }

    /// <summary>替换播放列表的全部条目（添加、移除、拖动排序都用它；列表通常不长）。</summary>
    public void SetPlaylistItems(long id, IReadOnlyList<(string trackKey, string? foreignRef)> items, long? updatedAt = null, string? name = null)
    {
        lock (Gate)
        {
            using var tx = Begin();
            Exec("DELETE FROM playlist_item WHERE playlist_id = $p", tx, ("$p", id));
            for (var i = 0; i < items.Count; i++)
                Exec("INSERT INTO playlist_item(playlist_id, position, track_key, foreign_ref) VALUES($p, $i, $k, $f)", tx,
                    ("$p", id), ("$i", i), ("$k", items[i].trackKey), ("$f", items[i].foreignRef));
            if (name is null) Exec("UPDATE playlist SET updated_at = $t WHERE id = $p", tx, ("$t", updatedAt ?? Now()), ("$p", id));
            else Exec("UPDATE playlist SET updated_at = $t, name = $n WHERE id = $p", tx, ("$t", updatedAt ?? Now()), ("$n", name), ("$p", id));
            tx.Commit();
        }
    }

    // ── 删除记录、同步历史 ──

    public Dictionary<string, long> Tombstones(string kind)
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT uid, deleted_at FROM tombstone WHERE kind = $k");
            cmd.Parameters.AddWithValue("$k", kind);
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, long>();
            while (r.Read()) map[r.GetString(0)] = r.GetInt64(1);
            return map;
        }
    }

    public void AddTombstone(string kind, string uid, long deletedAt)
    {
        lock (Gate) Exec("INSERT INTO tombstone(kind, uid, deleted_at) VALUES($k, $u, $t) ON CONFLICT(kind, uid) DO UPDATE SET deleted_at = MAX(deleted_at, excluded.deleted_at)",
            null, ("$k", kind), ("$u", uid), ("$t", deletedAt));
    }

    public void AddSyncHistory(string kind, string uid, string content, string? fromDevice)
    {
        lock (Gate) Exec("INSERT INTO sync_history(kind, uid, content, replaced_at, from_device) VALUES($k, $u, $c, $t, $d)",
            null, ("$k", kind), ("$u", uid), ("$c", content), ("$t", Now()), ("$d", fromDevice));
    }

    public List<(long id, string kind, string uid, string content, long replacedAt, string? fromDevice)> SyncHistory()
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT id, kind, uid, content, replaced_at, from_device FROM sync_history ORDER BY replaced_at DESC");
            using var r = cmd.ExecuteReader();
            var list = new List<(long, string, string, string, long, string?)>();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.Str(5)));
            return list;
        }
    }

    /// <summary>清理过期的删除记录（180 天）和同步历史（30 天）。</summary>
    public void PruneSyncData()
    {
        var now = Now();
        lock (Gate)
        {
            Exec("DELETE FROM tombstone WHERE deleted_at < $t", null, ("$t", now - 180L * 86_400_000));
            Exec("DELETE FROM sync_history WHERE replaced_at < $t", null, ("$t", now - 30L * 86_400_000));
        }
    }

    // ── 手动修正、歌词偏移 ──

    public List<TagOverride> TagOverrides()
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT track_key, dimension, value, updated_at FROM tag_override");
            using var r = cmd.ExecuteReader();
            var list = new List<TagOverride>();
            while (r.Read()) list.Add(new TagOverride(r.GetString(0), r.GetString(1), r.Str(2), r.GetInt64(3)));
            return list;
        }
    }

    /// <summary>手动修正一个维度；<paramref name="value"/> 为 null 表示改回自动（这一行保留，同步时告诉别的设备）。</summary>
    public void SetTagOverride(string trackKey, string dimension, string? value, long? updatedAt = null)
    {
        lock (Gate) Exec("INSERT OR REPLACE INTO tag_override(track_key, dimension, value, updated_at) VALUES($k, $d, $v, $t)", null,
            ("$k", trackKey), ("$d", dimension), ("$v", value), ("$t", updatedAt ?? Now()));
    }

    public List<LyricsOffset> LyricsOffsets()
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT track_key, offset_ms, updated_at FROM lyrics_offset");
            using var r = cmd.ExecuteReader();
            var list = new List<LyricsOffset>();
            while (r.Read()) list.Add(new LyricsOffset(r.GetString(0), r.GetInt32(1), r.GetInt64(2)));
            return list;
        }
    }

    public void SetLyricsOffset(string trackKey, int offsetMs, long? updatedAt = null)
    {
        lock (Gate) Exec("INSERT OR REPLACE INTO lyrics_offset(track_key, offset_ms, updated_at) VALUES($k, $o, $t)", null,
            ("$k", trackKey), ("$o", offsetMs), ("$t", updatedAt ?? Now()));
    }

    // ── 导航栏 ──

    public List<NavItem> NavItems()
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT type, ref FROM nav_item ORDER BY position");
            using var r = cmd.ExecuteReader();
            var list = new List<NavItem>();
            while (r.Read()) list.Add(new NavItem(r.GetString(0), r.Str(1)));
            return list;
        }
    }

    public void SetNavItems(IReadOnlyList<NavItem> items, long? updatedAt = null)
    {
        lock (Gate)
        {
            using var tx = Begin();
            Exec("DELETE FROM nav_item", tx);
            for (var i = 0; i < items.Count; i++)
                Exec("INSERT INTO nav_item(position, type, ref) VALUES($i, $t, $r)", tx, ("$i", i), ("$t", items[i].Type), ("$r", items[i].Ref));
            Exec("INSERT INTO kv(key, value) VALUES('nav_updated_at', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", tx,
                ("$v", (updatedAt ?? Now()).ToString()));
            tx.Commit();
        }
    }

    public long NavUpdatedAt => long.TryParse(GetValue("nav_updated_at"), out var v) ? v : 0;

    // ── 续播队列 ──

    public List<QueueItemRow> QueueItems()
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT track_key, manual FROM queue_item ORDER BY position");
            using var r = cmd.ExecuteReader();
            var list = new List<QueueItemRow>();
            while (r.Read()) list.Add(new QueueItemRow(r.GetString(0), r.Bool(1)));
            return list;
        }
    }

    public void SetQueueItems(IReadOnlyList<QueueItemRow> items)
    {
        lock (Gate)
        {
            using var tx = Begin();
            Exec("DELETE FROM queue_item", tx);
            for (var i = 0; i < items.Count; i++)
                Exec("INSERT INTO queue_item(position, track_key, manual) VALUES($i, $k, $m)", tx, ("$i", i), ("$k", items[i].TrackKey), ("$m", items[i].Manual ? 1 : 0));
            tx.Commit();
        }
    }

    // ── 智能标签 ──

    public List<FeaturesRow> AllFeatures()
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT track_key, signature, analyzer_version, data, failed FROM track_features");
            using var r = cmd.ExecuteReader();
            var list = new List<FeaturesRow>();
            while (r.Read()) list.Add(new FeaturesRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.Str(3), r.Bool(4)));
            return list;
        }
    }

    public void SaveFeatures(FeaturesRow row)
    {
        lock (Gate) Exec("INSERT OR REPLACE INTO track_features(track_key, signature, analyzer_version, data, failed, analyzed_at) VALUES($k, $s, $v, $d, $f, $t)", null,
            ("$k", row.TrackKey), ("$s", row.Signature), ("$v", row.AnalyzerVersion), ("$d", row.Data), ("$f", row.Failed ? 1 : 0), ("$t", Now()));
    }

    /// <summary>物化的标签：trackKey → (维度 → (值, 来源))。</summary>
    public Dictionary<string, Dictionary<string, (string value, string source)>> AllTags()
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT track_key, dimension, value, source FROM track_tag");
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, Dictionary<string, (string, string)>>();
            while (r.Read())
            {
                var key = r.GetString(0);
                if (!map.TryGetValue(key, out var dims)) map[key] = dims = new();
                dims[r.GetString(1)] = (r.GetString(2), r.GetString(3));
            }
            return map;
        }
    }

    public void SetTags(IEnumerable<(string trackKey, string dimension, string value, string source)> tags)
    {
        lock (Gate)
        {
            using var tx = Begin();
            foreach (var (k, d, v, s) in tags)
                Exec("INSERT OR REPLACE INTO track_tag(track_key, dimension, value, source) VALUES($k, $d, $v, $s)", tx, ("$k", k), ("$d", d), ("$v", v), ("$s", s));
            tx.Commit();
        }
    }

    // ── 歌词缓存 ──

    public (string? content, bool synced, string? source, long fetchedAt)? CachedLyrics(string trackKey)
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT content, synced, source, fetched_at FROM lyrics_cache WHERE track_key = $k");
            cmd.Parameters.AddWithValue("$k", trackKey);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.Str(0), r.Bool(1), r.Str(2), r.GetInt64(3)) : null;
        }
    }

    /// <summary>content 为 null 表示"在线也没找到"（负缓存，7 天内不再查，同移动端 LY-8）。</summary>
    public void CacheLyrics(string trackKey, string? content, bool synced, string? source)
    {
        lock (Gate) Exec("INSERT OR REPLACE INTO lyrics_cache(track_key, source, synced, content, fetched_at) VALUES($k, $s, $y, $c, $t)", null,
            ("$k", trackKey), ("$s", source), ("$y", synced ? 1 : 0), ("$c", content), ("$t", Now()));
    }

    public void DeleteCachedLyrics(string trackKey)
    {
        lock (Gate) Exec("DELETE FROM lyrics_cache WHERE track_key = $k", null, ("$k", trackKey));
    }

    public long? CoverMissAt(string albumKey)
    {
        lock (Gate) return Scalar("SELECT checked_at FROM cover_miss WHERE album_key = $k", null, ("$k", albumKey)) as long?;
    }

    public void SetCoverMiss(string albumKey, bool missing)
    {
        lock (Gate)
        {
            if (missing) Exec("INSERT OR REPLACE INTO cover_miss(album_key, checked_at) VALUES($k, $t)", null, ("$k", albumKey), ("$t", Now()));
            else Exec("DELETE FROM cover_miss WHERE album_key = $k", null, ("$k", albumKey));
        }
    }
}
