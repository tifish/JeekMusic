using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Sync;

internal sealed partial class LanFileSyncStore
{
    long deltaRowsSent,deltaRounds,fullMetadataRounds;
    void InitializeDelta() => Run("CREATE TABLE IF NOT EXISTS delta_heads(scope TEXT PRIMARY KEY,cursor TEXT NOT NULL,pending TEXT,parent TEXT,reset INTEGER);CREATE TABLE IF NOT EXISTS delta_snapshots(scope TEXT,path TEXT,json TEXT,PRIMARY KEY(scope,path));CREATE TABLE IF NOT EXISTS delta_pending(scope TEXT,path TEXT,json TEXT,PRIMARY KEY(scope,path));CREATE TABLE IF NOT EXISTS delta_changes(scope TEXT,path TEXT,json TEXT,PRIMARY KEY(scope,path));");

    // One confirmed snapshot and one in-flight snapshot, never a file edit history.
    public JsonObject Delta(string scope,string cursor,IReadOnlyList<LanFiles.Entry> files,IReadOnlyList<string> selection,Func<LanFiles.Entry,long> editTime,CancellationToken ct)
    {
        if(cursor.Length>64)throw new ArgumentException("Invalid delta cursor");
        var expected=JsonNode.Parse((string)Scalar("SELECT selection FROM scopes WHERE id=$s",("$s",scope))!)!.AsArray().Select(n=>n!.GetValue<string>()).Order(StringComparer.Ordinal);
        if(!expected.SequenceEqual(selection.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))throw new ArgumentException("Delta plan selection mismatch");
        var current=(string?)Scalar("SELECT cursor FROM delta_heads WHERE scope=$s",("$s",scope))??"";
        var reset=cursor.Length==0 || cursor!=current;var token=Guid.NewGuid().ToString("N");
        Run("BEGIN IMMEDIATE");try {
            Run("DELETE FROM delta_pending WHERE scope=$s",("$s",scope));Run("DELETE FROM delta_changes WHERE scope=$s",("$s",scope));
            // Stream the existing metadata plan into SQLite, keeping only one JSON row in memory.
            foreach(var file in files) {
                ct.ThrowIfCancellationRequested();
                var row=new JsonObject { ["path"]=file.Path,["remote"]=new JsonObject { ["path"]=file.Path,["known"]=true,["exists"]=true,["size"]=file.Size,["modifiedNs"]=file.ModifiedNs,["metadataVersion"]=FileMetadata.Version(file.Size,file.ModifiedNs),["precisionNs"]=100,["editedAtMs"]=editTime(file),["dependencies"]=new JsonArray((file.Dependencies??[]).Select(p=>(JsonNode?)JsonValue.Create(p)).ToArray()) },["baseline"]=null };
                Run("INSERT INTO delta_pending VALUES($s,$p,$j)",("$s",scope),("$p",file.Path),("$j",row.ToJsonString()));
            }
            using(var query=Command("SELECT path,hash,version,metadata FROM baseline WHERE scope=$s",("$s",scope)))using(var reader=query.ExecuteReader())while(reader.Read()) {
                ct.ThrowIfCancellationRequested();var path=reader.GetString(0);
                var baseline=WithMetadata(new() { ["path"]=path,["stamp"]=reader.IsDBNull(1)?null:reader.GetString(1),["version"]=reader.GetString(2) },reader.IsDBNull(3)?null:reader.GetString(3)).ToJsonString();
                Run("INSERT INTO delta_pending VALUES($s,$p,json_object('path',$p,'remote',NULL,'baseline',json($b))) ON CONFLICT(scope,path) DO UPDATE SET json=json_set(delta_pending.json,'$.baseline',json($b))",("$s",scope),("$p",path),("$b",baseline));
            }
            if(Convert.ToInt64(Scalar("SELECT COUNT(*) FROM delta_pending WHERE scope=$s",("$s",scope)))>200000)throw new LanFileDeferred("delta_snapshot_budget");
            if(reset)Run("INSERT INTO delta_changes SELECT * FROM delta_pending WHERE scope=$s",("$s",scope));
            else {
                Run("INSERT INTO delta_changes SELECT p.* FROM delta_pending p LEFT JOIN delta_snapshots s ON p.scope=s.scope AND p.path=s.path WHERE p.scope=$s AND (s.path IS NULL OR s.json!=p.json)",("$s",scope));
                Run("INSERT INTO delta_changes SELECT s.scope,s.path,json_object('path',s.path,'remote',NULL,'baseline',NULL) FROM delta_snapshots s LEFT JOIN delta_pending p ON s.scope=p.scope AND s.path=p.path WHERE s.scope=$s AND p.path IS NULL",("$s",scope));
            }
            Run("INSERT OR REPLACE INTO delta_heads VALUES($s,$c,$t,$p,$r)",("$s",scope),("$c",current),("$t",token),("$p",cursor),("$r",reset?1:0));
            Run("COMMIT");
        }catch { Run("ROLLBACK");throw; }
        Interlocked.Increment(ref deltaRounds);if(reset)Interlocked.Increment(ref fullMetadataRounds);
        return DeltaRows(scope,token,0);
    }
    public JsonObject DeltaRows(string scope,string token,int offset)
    {
        if(token.Length>64)throw new ArgumentException("Invalid delta cursor");
        if(offset<0 || token!=(string?)Scalar("SELECT pending FROM delta_heads WHERE scope=$s",("$s",scope)))throw new LanFileDeferred("delta_checkpoint_changed");
        var rows=new JsonArray();using(var query=Command("SELECT json FROM delta_changes WHERE scope=$s ORDER BY path LIMIT 200 OFFSET $o",("$s",scope),("$o",offset)))using(var reader=query.ExecuteReader())while(reader.Read())rows.Add(JsonNode.Parse(reader.GetString(0)));
        var total=Convert.ToInt32(Scalar("SELECT COUNT(*) FROM delta_changes WHERE scope=$s",("$s",scope)));
        Interlocked.Add(ref deltaRowsSent,rows.Count);
        return new() { ["cursor"]=token,["reset"]=Convert.ToInt32(Scalar("SELECT reset FROM delta_heads WHERE scope=$s",("$s",scope)))!=0,["rows"]=rows,["total"]=total,["next"]=offset+rows.Count,["hasMore"]=offset+rows.Count<total };
    }
    public JsonObject CommitDelta(string scope,string token)
    {
        if(token.Length>64)throw new ArgumentException("Invalid delta cursor");
        var current=(string?)Scalar("SELECT cursor FROM delta_heads WHERE scope=$s",("$s",scope));
        if(token==current)return new() { ["accepted"]=true,["cursor"]=token };
        if(token.Length==0 || token!=(string?)Scalar("SELECT pending FROM delta_heads WHERE scope=$s",("$s",scope)))throw new LanFileDeferred("delta_checkpoint_changed");
        Run("BEGIN IMMEDIATE");try {
            Run("DELETE FROM delta_snapshots WHERE scope=$s",("$s",scope));Run("INSERT INTO delta_snapshots SELECT * FROM delta_pending WHERE scope=$s",("$s",scope));
            Run("DELETE FROM delta_pending WHERE scope=$s",("$s",scope));Run("DELETE FROM delta_changes WHERE scope=$s",("$s",scope));
            Run("UPDATE delta_heads SET cursor=$t,pending=NULL,parent=NULL WHERE scope=$s",("$s",scope),("$t",token));Run("COMMIT");
        }catch { Run("ROLLBACK");throw; }
        return new() { ["accepted"]=true,["cursor"]=token };
    }
    public JsonObject DeltaStatus()=>new() { ["protocol"]=1,["policy"]="confirmed_snapshot_delta",["history"]=false,["rounds"]=Interlocked.Read(ref deltaRounds),["fullRounds"]=Interlocked.Read(ref fullMetadataRounds),["rowsSent"]=Interlocked.Read(ref deltaRowsSent) };
}
