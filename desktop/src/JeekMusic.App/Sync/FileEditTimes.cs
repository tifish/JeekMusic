using JeekMusic.Core.Sync;
using JeekMusic.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace JeekMusic.Sync;

/// <summary>One successful edit time per path, bound to its observed size/mtime; no history.</summary>
internal sealed class FileEditTimeStore(string filename):IDisposable
{
    readonly object gate=new();
    SqliteConnection? connection;
    SqliteConnection Database {
        get {
            if(connection!=null)return connection;
            SQLitePCL.Batteries_V2.Init();Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filename))!);
            var opened=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=filename,Pooling=false }.ToString());
            opened.Open();
            using var create=opened.CreateCommand();create.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS latest_edit(path TEXT PRIMARY KEY COLLATE NOCASE,size INTEGER NOT NULL,modified_ns INTEGER NOT NULL,edited_at_ms INTEGER NOT NULL)";create.ExecuteNonQuery();
            return connection=opened;
        }
    }
    public long Read(string path,long size,long modifiedNs) {
        lock(gate) {
            using var query=Database.CreateCommand();query.CommandText="SELECT edited_at_ms FROM latest_edit WHERE path=$path AND size=$size AND modified_ns=$mtime";
            query.Parameters.AddWithValue("$path",Path.GetFullPath(path));query.Parameters.AddWithValue("$size",size);query.Parameters.AddWithValue("$mtime",modifiedNs);
            return (long?)query.ExecuteScalar()??0;
        }
    }
    public void Set(string path,long size,long modifiedNs,long editedAtMs) {
        ArgumentOutOfRangeException.ThrowIfNegative(editedAtMs);
        lock(gate) {
            using var write=Database.CreateCommand();write.CommandText=editedAtMs==0?"DELETE FROM latest_edit WHERE path=$path":"INSERT OR REPLACE INTO latest_edit VALUES($path,$size,$mtime,$edited)";
            write.Parameters.AddWithValue("$path",Path.GetFullPath(path));write.Parameters.AddWithValue("$size",size);write.Parameters.AddWithValue("$mtime",modifiedNs);write.Parameters.AddWithValue("$edited",editedAtMs);write.ExecuteNonQuery();
        }
    }
    public void Dispose() { lock(gate) { connection?.Dispose();connection=null; } }
}

internal static class FileEditTimes
{
    static readonly Lazy<FileEditTimeStore> store=new(()=>new(Path.Combine(AppPaths.DataDir,"last-file-edits.db")));
    static bool enabled;
    static string? problem;
    internal static void Initialize() { enabled=true;FileMetadata.LastEditTime=Read; }
    internal static long Read(string path,long size,long modifiedNs) {
        try { return store.Value.Read(path,size,modifiedNs); }
        catch(Exception e) when(e is SqliteException or IOException or UnauthorizedAccessException) { problem="last_edit_time_unavailable";return 0; }
    }
    internal static void Set(string path,long editedAtMs) {
        if(!enabled)return;
        // A committed file must not be reported as an unsuccessful tag write if the clock store fails.
        try {
            var file=new FileInfo(path);store.Value.Set(path,file.Length,checked((file.LastWriteTimeUtc.Ticks-DateTime.UnixEpoch.Ticks)*100),editedAtMs);problem=null;
        } catch(Exception e) when(e is SqliteException or IOException or UnauthorizedAccessException) { problem="last_edit_time_unavailable";Log.Warn("sync","Last file edit time could not be saved",e); }
    }
    internal static JsonObject Diagnostics()=>new() { ["policy"]="mtime_then_size_then_last_edit",["history"]=false,["unit"]="unix_milliseconds",["problem"]=problem };
}
