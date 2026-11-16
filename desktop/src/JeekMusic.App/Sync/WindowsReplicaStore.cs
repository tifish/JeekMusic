using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace JeekMusic.Sync;

/// <summary>Device-local mapping checkpoints and known pending operations. Never stores credentials.</summary>
internal sealed class WindowsReplicaStore : IDisposable
{
    readonly Lock gate=new();
    readonly SqliteConnection db;
    const long ByteLimit=128L*1024*1024;
    public WindowsReplicaStore(string directory)
    {
        Directory.CreateDirectory(directory);SQLitePCL.Batteries_V2.Init();
        db=new(new SqliteConnectionStringBuilder { DataSource=Path.Combine(directory,"replicas.db"),Pooling=false }.ToString());db.Open();
        Execute("PRAGMA journal_mode=DELETE;PRAGMA synchronous=FULL;PRAGMA busy_timeout=5000;PRAGMA cache_size=-4096;PRAGMA max_page_count=131072;CREATE TABLE IF NOT EXISTS rows(kind TEXT NOT NULL,key TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(kind,key));CREATE TABLE IF NOT EXISTS quota(id INTEGER PRIMARY KEY,rows INTEGER NOT NULL,bytes INTEGER NOT NULL);INSERT OR IGNORE INTO quota VALUES(1,0,0)");
    }
    SqliteCommand Command(string sql,params (string,object?)[] arguments)
    {
        var command=db.CreateCommand();command.CommandText=sql;
        foreach(var (name,value) in arguments)command.Parameters.AddWithValue(name,value??DBNull.Value);return command;
    }
    void Execute(string sql,params (string,object?)[] arguments) { using var command=Command(sql,arguments);command.ExecuteNonQuery(); }
    object? Scalar(string sql,params (string,object?)[] arguments) { using var command=Command(sql,arguments);return command.ExecuteScalar(); }
    public JsonObject? Read(string kind,string key)
    {
        lock(gate)return Scalar("SELECT value FROM rows WHERE kind=$kind AND key=$key",("$kind",kind),("$key",key)) is string text?JsonNode.Parse(text)!.AsObject():null;
    }
    public IReadOnlyList<(string Key,JsonObject Value)> Page(string kind,string after="",int limit=200)
    {
        if(limit is <1 or >200)throw new ArgumentException("Invalid page limit");
        lock(gate)
        {
            using var command=Command("SELECT key,value FROM rows WHERE kind=$kind AND key>$after ORDER BY key LIMIT $limit",("$kind",kind),("$after",after),("$limit",limit));
            using var reader=command.ExecuteReader();var result=new List<(string,JsonObject)>();int bytes=0;
            while(reader.Read())
            {
                var text=reader.GetString(1);if(result.Count>0 && bytes+text.Length>524_288)break;
                result.Add((reader.GetString(0),JsonNode.Parse(text)!.AsObject()));bytes+=text.Length;
            }
            return result;
        }
    }
    public long Count(string kind) { lock(gate)return (long)Scalar("SELECT COUNT(*) FROM rows WHERE kind=$kind",("$kind",kind))!; }
    void Quota(long rowDelta,long byteDelta)
    {
        var count=(long)Scalar("SELECT rows FROM quota WHERE id=1")!+rowDelta;var bytes=(long)Scalar("SELECT bytes FROM quota WHERE id=1")!+byteDelta;
        if(count is <0 or >300_000 || bytes<0 || bytes>ByteLimit)throw new LanFileDeferred("client_metadata_budget");
        Execute("UPDATE quota SET rows=$rows,bytes=$bytes WHERE id=1",("$rows",count),("$bytes",bytes));
    }
    public void Put(string kind,string key,JsonObject value)
    {
        var text=value.ToJsonString();if(text.Length>262144 || kind.Length>100 || key.Length>2048)throw new LanFileDeferred("client_row_budget");
        lock(gate)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                var old=Scalar("SELECT value FROM rows WHERE kind=$kind AND key=$key",("$kind",kind),("$key",key)) as string;
                Quota(old is null?1:0,Encoding.UTF8.GetByteCount(text)-(old is null?0:Encoding.UTF8.GetByteCount(old))+(old is null?Encoding.UTF8.GetByteCount(kind)+Encoding.UTF8.GetByteCount(key):0));
                Execute("INSERT INTO rows VALUES($kind,$key,$value) ON CONFLICT(kind,key) DO UPDATE SET value=excluded.value",("$kind",kind),("$key",key),("$value",text));Execute("COMMIT");
            }
            catch { Execute("ROLLBACK");throw; }
        }
    }
    public void Delete(string kind,string key)
    {
        lock(gate)
        {
            var old=Scalar("SELECT value FROM rows WHERE kind=$kind AND key=$key",("$kind",kind),("$key",key)) as string;if(old is null)return;
            Execute("BEGIN IMMEDIATE");try
            {
                Quota(-1,-Encoding.UTF8.GetByteCount(old)-Encoding.UTF8.GetByteCount(kind)-Encoding.UTF8.GetByteCount(key));
                Execute("DELETE FROM rows WHERE kind=$kind AND key=$key",("$kind",kind),("$key",key));Execute("COMMIT");
            }
            catch { Execute("ROLLBACK");throw; }
        }
    }
    public void Clear(string kind) { while(true) { var page=Page(kind);if(page.Count==0)return;foreach(var row in page)Delete(kind,row.Key); } }
    public void Dispose() { lock(gate)db.Dispose(); }
}
