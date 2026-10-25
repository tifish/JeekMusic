using Microsoft.Data.Sqlite;

namespace JeekMusic.Library;

/// <summary>
/// SQLite 数据库（设计 §4）。结构版本记在 PRAGMA user_version，每个版本一段迁移 SQL。
/// 连接只有一个，所有访问都要加锁（扫描、同步在后台线程，界面在 UI 线程）；SQLite 本身很快，锁的时间很短。
/// </summary>
public sealed partial class LibraryDb : IDisposable
{
    readonly SqliteConnection _conn;
    SqliteTransaction? _transaction;
    public readonly Lock Gate = new();

    static LibraryDb() => SQLitePCL.Batteries_V2.Init();

    public LibraryDb(string path)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY;");
        Migrate();
    }

    /// <summary>
    /// 每个元素是一个版本的迁移；版本号 = 下标 + 1。<b>从第一个公开版本起只能在末尾追加，不能修改已有的</b>，
    /// 否则已经装了的人数据库结构对不上。
    /// </summary>
    static readonly string[] Migrations =
    [
        """
        CREATE TABLE library_folder (
            id INTEGER PRIMARY KEY,
            volume_id TEXT NOT NULL,
            path_in_volume TEXT NOT NULL,
            last_root TEXT NOT NULL,
            display_name TEXT NOT NULL,
            available INTEGER NOT NULL DEFAULT 1,
            added_at INTEGER NOT NULL,
            UNIQUE(volume_id, path_in_volume)
        );
        CREATE TABLE track (
            id INTEGER PRIMARY KEY,
            track_key TEXT NOT NULL UNIQUE,
            folder_id INTEGER NOT NULL REFERENCES library_folder(id) ON DELETE CASCADE,
            dir TEXT NOT NULL,
            file_name TEXT NOT NULL,
            title_tag TEXT, title TEXT NOT NULL,
            album TEXT, album_artist TEXT, album_key TEXT,
            artist_raw TEXT, artist_display TEXT NOT NULL, artist_inferred INTEGER NOT NULL,
            artists TEXT NOT NULL,
            track_no INTEGER, disc_no INTEGER, year INTEGER, genre TEXT,
            duration_ms INTEGER NOT NULL,
            codec TEXT, bitrate INTEGER, sample_rate INTEGER, bits_per_sample INTEGER, channels INTEGER,
            has_lyrics INTEGER NOT NULL, has_cover INTEGER NOT NULL,
            cue_track_no INTEGER, start_ms INTEGER, end_ms INTEGER,
            mtime INTEGER NOT NULL, size INTEGER NOT NULL,
            sort_title TEXT NOT NULL, sort_file TEXT NOT NULL, sort_artist TEXT NOT NULL
        );
        CREATE INDEX track_folder ON track(folder_id, dir);
        -- .cue 文件的状态：.cue 改了，同目录的整轨文件要重新解析（增量扫描用）
        CREATE TABLE side_file (
            folder_id INTEGER NOT NULL REFERENCES library_folder(id) ON DELETE CASCADE,
            rel_path TEXT NOT NULL COLLATE NOCASE, mtime INTEGER NOT NULL, size INTEGER NOT NULL,
            PRIMARY KEY(folder_id, rel_path)
        );
        CREATE INDEX track_album ON track(album_key);

        CREATE TABLE track_features (
            track_key TEXT PRIMARY KEY,
            signature TEXT NOT NULL,
            analyzer_version INTEGER NOT NULL,
            data TEXT,
            failed INTEGER NOT NULL DEFAULT 0,
            analyzed_at INTEGER NOT NULL
        );
        CREATE TABLE track_tag (
            track_key TEXT NOT NULL, dimension TEXT NOT NULL, value TEXT NOT NULL, source TEXT NOT NULL,
            PRIMARY KEY(track_key, dimension)
        );
        CREATE TABLE tag_override (
            track_key TEXT NOT NULL, dimension TEXT NOT NULL, value TEXT, updated_at INTEGER NOT NULL,
            PRIMARY KEY(track_key, dimension)
        );
        CREATE TABLE lyrics_offset (
            track_key TEXT PRIMARY KEY, offset_ms INTEGER NOT NULL, updated_at INTEGER NOT NULL
        );
        CREATE TABLE lyrics_cache (
            track_key TEXT PRIMARY KEY, source TEXT, synced INTEGER NOT NULL, content TEXT, fetched_at INTEGER NOT NULL
        );
        CREATE TABLE cover_miss (
            album_key TEXT PRIMARY KEY, checked_at INTEGER NOT NULL
        );

        CREATE TABLE playlist (
            id INTEGER PRIMARY KEY, uid TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
            created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL
        );
        CREATE TABLE playlist_item (
            id INTEGER PRIMARY KEY,
            playlist_id INTEGER NOT NULL REFERENCES playlist(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            track_key TEXT NOT NULL,
            -- 对不上本机歌曲的条目（只在其他设备上有的）原样保留的相对路径引用（需求 SY-6）
            foreign_ref TEXT
        );
        CREATE INDEX playlist_item_playlist ON playlist_item(playlist_id, position);

        CREATE TABLE nav_item (position INTEGER PRIMARY KEY, type TEXT NOT NULL, ref TEXT);
        CREATE TABLE queue_item (position INTEGER PRIMARY KEY, track_key TEXT NOT NULL, manual INTEGER NOT NULL);
        CREATE TABLE tombstone (kind TEXT NOT NULL, uid TEXT NOT NULL, deleted_at INTEGER NOT NULL, PRIMARY KEY(kind, uid));
        CREATE TABLE sync_history (
            id INTEGER PRIMARY KEY, kind TEXT NOT NULL, uid TEXT NOT NULL, content TEXT NOT NULL,
            replaced_at INTEGER NOT NULL, from_device TEXT
        );
        CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT);
        """,
        """
        ALTER TABLE library_folder ADD COLUMN identity TEXT NOT NULL DEFAULT '';
        UPDATE library_folder SET identity = lower(hex(randomblob(16)));
        CREATE TRIGGER library_folder_identity AFTER INSERT ON library_folder
        WHEN NEW.identity = '' BEGIN
          UPDATE library_folder SET identity = lower(hex(randomblob(16))) WHERE id = NEW.id;
        END;
        """,
    ];

    void Migrate()
    {
        var version = (long)Scalar("PRAGMA user_version")!;
        for (var v = (int)version; v < Migrations.Length; v++)
        {
            using var tx = _conn.BeginTransaction();
            Exec(Migrations[v], tx);
            Exec($"PRAGMA user_version = {v + 1}", tx);
            tx.Commit();
        }
    }

    // ── 基础操作 ──

    public SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx ?? _transaction;
        return cmd;
    }

    public int Exec(string sql, SqliteTransaction? tx = null, params (string name, object? value)[] args)
    {
        using var cmd = Command(sql, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, SqliteTransaction? tx = null, params (string name, object? value)[] args)
    {
        using var cmd = Command(sql, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    public SqliteTransaction Begin() => _conn.BeginTransaction();

    /// <summary>Compose user-data operations into one commit; callers share the connection lock.</summary>
    public void InTransaction(Action action)
    {
        lock (Gate)
        {
            if (_transaction is not null) { action(); return; }
            using var tx = Begin();
            _transaction = tx;
            try { action(); tx.Commit(); }
            finally { _transaction = null; }
        }
    }

    // ── 键值（kv 表）──

    public string? GetValue(string key)
    {
        lock (Gate) return Scalar("SELECT value FROM kv WHERE key = $k", null, ("$k", key)) as string;
    }

    public void SetValue(string key, string? value)
    {
        lock (Gate) Exec("INSERT INTO kv(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", null, ("$k", key), ("$v", value));
    }

    public void Dispose()
    {
        lock (Gate)
        {
            // WAL 合并回主文件，下次打开更快
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE)"); } catch (SqliteException) { }
            _conn.Dispose();
        }
    }
}

static class SqliteReaderExtensions
{
    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static int? Int(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static long? Long(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static bool Bool(this SqliteDataReader r, int i) => !r.IsDBNull(i) && r.GetInt64(i) != 0;
}
