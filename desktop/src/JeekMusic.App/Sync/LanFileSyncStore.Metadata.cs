using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Sync;

internal sealed partial class LanFileSyncStore
{
    readonly Dictionary<string,JsonObject> schedules=new();
    public JsonObject Schedule(string scope,JsonObject args)
    {
        var row=args["schedule"]?.AsObject()??throw new ArgumentException("Schedule required");
        var allowed=new HashSet<string> { "trigger","dirty","running","paused","cancelled","foreground","backoffUntil","lastSuccess","nextReconcile","lastMetadataObservations","lastHashBytes","lastTransferBytes","state","watchDirectories","watchFailed" };
        if(row.Count>allowed.Count || row.Any(p=>!allowed.Contains(p.Key)) || row.ToJsonString().Length>4096)throw new ArgumentException("Invalid schedule diagnostics");
        foreach(var p in row)if(p.Value is JsonObject or JsonArray || p.Value?.ToJsonString().Length>256)throw new ArgumentException("Invalid schedule diagnostic value");
        var copy=row.DeepClone().AsObject();copy["reportedAt"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();schedules[scope]=copy;
        return new() { ["accepted"]=true };
    }
    JsonArray Schedules()=>new(schedules.Select(p=>(JsonNode)new JsonObject { ["scope"]=p.Key,["schedule"]=p.Value.DeepClone() }).ToArray());
    static JsonObject WithMetadata(JsonObject row,string? metadata)
    {
        if(metadata!=null)foreach(var pair in JsonNode.Parse(metadata)!.AsObject())row[pair.Key]=pair.Value?.DeepClone();
        else if(row["hash"] is null) { row["comparisonProtocol"]=2;row["localVersion"]=null;row["remoteVersion"]=null;row["migration"]="legacy_tombstone"; }
        else row["migration"]="legacy_metadata_unconfirmed";
        return row;
    }
    static string? V(JsonNode? node)
    {
        if(node is null)return null;
        var value=node.GetValue<string>();var pieces=value.Split(':');
        if(pieces.Length!=3 || pieces[0]!="m2" || !long.TryParse(pieces[1],out var size) || size<0 || !long.TryParse(pieces[2],out _))throw new IOException("Invalid metadata version");
        return value;
    }
    void RequireMetadataBaseline(JsonObject? baseline,JsonObject args)
    {
        if(args["resolve"]?.GetValue<bool>()==true)return;
        if(baseline?["migration"]?.GetValue<string>()=="legacy_metadata_unconfirmed")throw Conflict("legacy_metadata_unconfirmed");
        if(V(baseline?["remoteVersion"])!=V(args["expectedVersion"]))throw Conflict("remote_not_at_baseline");
    }
    public async Task<JsonObject> Verify(string scope,string root,string path,JsonObject args,CancellationToken ct)
    {
        PathAllowed(scope,path);var before=FileMetadata.Observe(root,path);
        if(FileMetadata.Token(before)!=V(args["expectedVersion"]))throw Conflict("source_changed_before_verification");
        using var file=new LanFileMutation(root,path,false);ReserveHash(scope,file.FullPath);
        var hash=await file.Observe(limits.FileBytes,ct);
        var after=FileMetadata.Observe(root,path);
        if(FileMetadata.Token(before)!=FileMetadata.Token(after))throw Conflict("source_changed_during_verification");
        after["hash"]=hash;return after;
    }
    async Task<JsonObject> AckMetadata(string scope,string root,JsonObject a,CancellationToken ct,Action<Action> commit)
    {
        await System.Threading.Tasks.Task.CompletedTask;ct.ThrowIfCancellationRequested();
        var path=S(a["path"]);PathAllowed(scope,path);var operation=Operation(S(a["operation"]));
        var remote=V(a["remoteVersion"]);var local=V(a["localVersion"]);
        if(a["localKnown"]?.GetValue<bool>()!=true || (remote is null)!=(local is null))throw new IOException("Both sides must confirm presence");
        var previous=Baseline(scope,path);
        if(S(previous?["operation"])==operation && V(previous?["remoteVersion"])==remote && V(previous?["localVersion"])==local)return previous!;
        RequireBaseline(scope,path,S(a["baselineVersion"]));
        var receiptId=S(a["mutationOperation"]);JsonObject? receipt=null;
        if(receiptId.Length>0) {
            receipt=Task(scope,Operation(receiptId))??throw new IOException("Unknown mutation receipt");
            if(S(receipt["path"])!=path || S(receipt["state"]) is not ("committed" or "acknowledged") || Digest(receipt["hash"])!=Digest(a["hash"]))throw new IOException("Mutation receipt does not match acknowledgement");
        }
        if(local!=remote && (local is null || remote is null || local.Split(':')[1]!=remote.Split(':')[1] || S(a["decision"]) is not ("download" or "upload" or "ack")))throw new IOException("Invalid landing version mapping");
        var observed=FileMetadata.Observe(root,path);
        if(FileMetadata.Token(observed)!=remote)throw Conflict("remote_changed_before_ack");
        if(previous is null && remote is null)return new() { ["version"]="",["comparisonProtocol"]=2,["localVersion"]=null,["remoteVersion"]=null };
        if(previous is null && (baselineCount>=limits.Rows || scopeCounts.GetValueOrDefault(scope)>=100000))throw new LanFileDeferred("baseline_budget");
        var version=Guid.NewGuid().ToString("N");
        var metadata=new JsonObject { ["comparisonProtocol"]=2,["localVersion"]=local,["remoteVersion"]=remote,["localPrecisionNs"]=a["localPrecisionNs"]?.DeepClone(),["remotePrecisionNs"]=100 };
        commit(()=>{});ct.ThrowIfCancellationRequested();RequireBaseline(scope,path,S(a["baselineVersion"]));
        if(FileMetadata.Token(FileMetadata.Observe(root,path))!=remote)throw Conflict("remote_changed_before_ack");
        Run("BEGIN IMMEDIATE");try {
            Run("INSERT OR REPLACE INTO baseline(scope,path,hash,version,operation,metadata) VALUES($s,$p,$h,$v,$o,$m)",("$s",scope),("$p",path),("$h",Digest(a["hash"])),("$v",version),("$o",operation),("$m",metadata.ToJsonString()));
            Run("UPDATE scopes SET revision=$v WHERE id=$s",("$v",Guid.NewGuid().ToString("N")),("$s",scope));if(receipt!=null)SaveTask(scope,receipt,"acknowledged");Run("COMMIT");
        }catch { Run("ROLLBACK");throw; }
        if(previous is null) { baselineCount++;scopeCounts[scope]=scopeCounts.GetValueOrDefault(scope)+1; }
        var decision=S(a["decision"]);if(decision.Length>0)confirmations[decision]=confirmations.GetValueOrDefault(decision)+1;
        metadata["version"]=version;metadata["hash"]=a["hash"]?.DeepClone();return metadata;
    }
}
