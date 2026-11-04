using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Sync;

internal sealed partial class LanFileSyncStore
{
    void InitializeConflicts() => Run("CREATE TABLE IF NOT EXISTS file_conflicts(id TEXT PRIMARY KEY,scope TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL,choice TEXT,UNIQUE(scope,path));CREATE TABLE IF NOT EXISTS conflict_revisions(scope TEXT PRIMARY KEY,revision TEXT NOT NULL)");
    void ConflictChanged(string scope) => Run("INSERT OR REPLACE INTO conflict_revisions VALUES($s,$r)",("$s",scope),("$r",Guid.NewGuid().ToString("N")));
    public string ConflictRevision(string scope) => Scalar("SELECT revision FROM conflict_revisions WHERE scope=$s",("$s",scope)) as string ?? "0";
    public long PendingConflictCount => Convert.ToInt64(Scalar("SELECT COUNT(*) FROM file_conflicts"));
    static string Change(string? version,string? baseline,bool tracked) => !tracked ? version is null ? "absent" : "untracked_present" : version==baseline ? "unchanged" : version is null ? "deleted" : baseline is null ? "created" : "modified";
    static JsonObject VersionInfo(string? version) {
        if(version is null)return new() { ["exists"]=false };
        var p=version.Split(':');return new() { ["exists"]=true,["size"]=long.Parse(p[1]),["modifiedNs"]=long.Parse(p[2]) };
    }
    public JsonObject ReportConflict(string scope,string root,JsonObject input) {
        var path=LanFiles.Relative(S(input["path"]));PathAllowed(scope,path);
        var local=V(input["localVersion"]);var remote=V(input["remoteVersion"]);
        var baseline=RequireBaseline(scope,path,S(input["baselineVersion"]));
        if(FileMetadata.Token(FileMetadata.Observe(root,path))!=remote)throw Conflict("conflict_version_changed");
        var reason=S(input["reason"]);
        if(reason is not ("initial_different_content" or "both_modified" or "modified_vs_deleted" or "legacy_metadata_unconfirmed"))throw new ArgumentException("Invalid conflict reason");
        var mapping=(string)Scalar("SELECT mapping FROM scopes WHERE id=$s",("$s",scope))!;
        var id=Hash(string.Join("\n",mapping,scope,path,S(input["baselineVersion"]),local??"",remote??""));
        if(id!=S(input["id"]))throw new ArgumentException("Invalid conflict identity");
        if(Scalar("SELECT json FROM file_conflicts WHERE id=$i",("$i",id)) is string existing)return ConflictRow(existing,Scalar("SELECT choice FROM file_conflicts WHERE id=$i",("$i",id)) as string);
        if(PendingConflictCount>=100000)throw new LanFileDeferred("conflict_budget");
        var row=new JsonObject { ["id"]=id,["scope"]=scope,["mapping"]=mapping,["path"]=path,["baselineVersion"]=S(input["baselineVersion"]),["localVersion"]=local,["remoteVersion"]=remote,
            ["local"]=VersionInfo(local),["remote"]=VersionInfo(remote),["reason"]=reason,["reportedAt"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["localChange"]=Change(local,V(baseline?["localVersion"]),baseline!=null),["remoteChange"]=Change(remote,V(baseline?["remoteVersion"]),baseline!=null) };
        Run("INSERT OR REPLACE INTO file_conflicts(id,scope,path,json,choice) VALUES($i,$s,$p,$j,NULL)",("$i",id),("$s",scope),("$p",path),("$j",row.ToJsonString()));ConflictChanged(scope);return ConflictRow(row.ToJsonString(),null);
    }
    static JsonObject ConflictRow(string json,string? choice) {
        var row=JsonNode.Parse(json)!.AsObject();row["choice"]=choice;
        var phone=row["local"]!.AsObject();var pc=row["remote"]!.AsObject();
        foreach(var (side,other) in new[] { (phone,pc),(pc,phone) }) {
            var comparable=side["exists"]!.GetValue<bool>() && other["exists"]!.GetValue<bool>();
            side["sizeComparison"]=comparable ? Compare(side,other,"size","larger","smaller") : "unavailable";
            side["modifiedComparison"]=comparable ? Compare(side,other,"modifiedNs","newer","older") : "unavailable";
        }
        return row;
    }
    static string Compare(JsonObject side,JsonObject other,string field,string greater,string less) {
        var comparison=side[field]!.GetValue<long>().CompareTo(other[field]!.GetValue<long>());
        return comparison>0 ? greater : comparison<0 ? less : "equal";
    }
    public JsonObject ConflictPage(int offset=0,string? scope=null) {
        if(offset<0)throw new ArgumentException("Invalid offset");
        var rows=new JsonArray();using var command=Command("SELECT c.json,c.choice,s.device,s.root FROM file_conflicts c JOIN scopes s ON s.id=c.scope WHERE ($s IS NULL OR c.scope=$s) ORDER BY c.scope,c.path LIMIT 100 OFFSET $o",("$s",scope),("$o",offset));
        using var reader=command.ExecuteReader();while(reader.Read()) { var row=ConflictRow(reader.GetString(0),reader.IsDBNull(1)?null:reader.GetString(1));row["device"]=reader.GetString(2);row["rootId"]=reader.GetString(3);rows.Add((JsonNode)row); }
        var total=Convert.ToInt64(Scalar("SELECT COUNT(*) FROM file_conflicts WHERE ($s IS NULL OR scope=$s)",("$s",scope)));
        return new() { ["rows"]=rows,["total"]=total,["offset"]=offset,["next"]=offset+rows.Count,["hasMore"]=offset+rows.Count<total };
    }
    public string ConflictScope(string id)=>Scalar("SELECT scope FROM file_conflicts WHERE id=$i",("$i",id)) as string ?? throw new LanFileConflict("conflict_no_longer_current");
    public (string Device,string Root) ConflictOwner(string scope) { using var c=Command("SELECT device,root FROM scopes WHERE id=$s",("$s",scope));using var r=c.ExecuteReader();if(!r.Read())throw new UnauthorizedAccessException();return(r.GetString(0),r.GetString(1)); }
    public JsonObject ChooseConflict(string scope,string root,string id,string choice) {
        if(choice is not ("local" or "remote"))throw new ArgumentException("Choose local (phone) or remote (PC)");
        if(ConflictScope(id)!=scope)throw new UnauthorizedAccessException();
        var row=JsonNode.Parse((string)Scalar("SELECT json FROM file_conflicts WHERE id=$i",("$i",id))!)!.AsObject();
        if(FileMetadata.Token(FileMetadata.Observe(root,S(row["path"])))!=V(row["remoteVersion"]))throw Conflict("conflict_version_changed");
        var previous=Scalar("SELECT choice FROM file_conflicts WHERE id=$i",("$i",id)) as string;
        if(previous!=null && previous!=choice)throw Conflict("conflict_already_chosen");
        Run("UPDATE file_conflicts SET choice=$c WHERE id=$i",("$c",choice),("$i",id));ConflictChanged(scope);
        row["choice"]=choice;row["waitingForPhone"]=true;return ConflictRow(row.ToJsonString(),choice);
    }
    public void ClearConflict(string scope,string path) {
        if(Scalar("SELECT id FROM file_conflicts WHERE scope=$s AND path=$p",("$s",scope),("$p",path)) is not string)return;
        Run("DELETE FROM file_conflicts WHERE scope=$s AND path=$p",("$s",scope),("$p",path));ConflictChanged(scope);
    }
    public JsonObject ClearConvergedConflict(string scope,string root,JsonObject args) {
        var path=LanFiles.Relative(S(args["path"]));PathAllowed(scope,path);
        var local=V(args["localVersion"]);var remote=V(args["remoteVersion"]);var baseline=Baseline(scope,path);
        if(FileMetadata.Token(FileMetadata.Observe(root,path))!=remote || (local!=remote && (V(baseline?["localVersion"])!=local || V(baseline?["remoteVersion"])!=remote)))throw Conflict("conflict_version_changed");
        if(Scalar("SELECT id FROM file_conflicts WHERE scope=$s AND path=$p",("$s",scope),("$p",path)) is string)ClearConflict(scope,path);
        return new() { ["cleared"]=true };
    }
}
