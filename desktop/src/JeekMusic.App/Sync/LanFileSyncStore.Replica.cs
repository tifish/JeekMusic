using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Sync;

internal sealed partial class LanFileSyncStore
{
    internal string BackupRelativePath(string receipt)=>(string)(Scalar("SELECT json_extract(json,'$.path') FROM operations WHERE id=$id AND backup_bytes>0",("$id",receipt))??throw new ArgumentException("Unknown backup receipt"));
    /// <summary>Private Windows client checkpoint after the upstream accepted a known landing.
    /// The client journal retains both landed versions even if the user edited again meanwhile.
    /// This records history only. Later mutations still require an exact current-file precondition.
    /// Never exposed through the LAN protocol.</summary>
    internal JsonObject ConfirmReplicaBaseline(string scope,string path,JsonObject evidence)
    {
        PathAllowed(scope,path);
        var local=V(evidence["localVersion"]);var remote=V(evidence["remoteVersion"]);
        if((local is null)!=(remote is null) || local is not null && local.Split(':')[1]!=remote!.Split(':')[1])throw new IOException("Invalid confirmed replica landing");
        var operation=Operation(S(evidence["operation"]));var previous=Baseline(scope,path);
        if(S(previous?["operation"])==operation && V(previous?["localVersion"])==local && V(previous?["remoteVersion"])==remote)return previous!;
        RequireBaseline(scope,path,S(evidence["baselineVersion"]));
        if(previous is null && (baselineCount>=limits.Rows || scopeCounts.GetValueOrDefault(scope)>=100000))throw new LanFileDeferred("baseline_budget");
        var mutation=S(evidence["mutationOperation"]);JsonObject? receipt=null;
        if(mutation.Length>0)
        {
            receipt=Task(scope,Operation(mutation))??throw new IOException("Missing local landing receipt");
            if(S(receipt["path"])!=path || S(receipt["state"]) is not ("committed" or "acknowledged") || V(receipt["resultVersion"])!=remote)throw new IOException("Local landing receipt mismatch");
        }
        var version=Guid.NewGuid().ToString("N");
        var metadata=new JsonObject { ["comparisonProtocol"]=2,["localVersion"]=local,["remoteVersion"]=remote,["localPrecisionNs"]=100,["remotePrecisionNs"]=100 };
        Run("BEGIN IMMEDIATE");try
        {
            Run("INSERT OR REPLACE INTO baseline VALUES($s,$p,$h,$v,$o,$m)",("$s",scope),("$p",path),("$h",Stamp(evidence["stamp"])),("$v",version),("$o",operation),("$m",metadata.ToJsonString()));
            Run("UPDATE scopes SET revision=$v WHERE id=$s",("$v",Guid.NewGuid().ToString("N")),("$s",scope));
            if(receipt is not null)SaveTask(scope,receipt,"acknowledged");Run("COMMIT");
        }
        catch { Run("ROLLBACK");throw; }
        if(previous is null) { baselineCount++;scopeCounts[scope]=scopeCounts.GetValueOrDefault(scope)+1; }
        return Baseline(scope,path)!;
    }
}
