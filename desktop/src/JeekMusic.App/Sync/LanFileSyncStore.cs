using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;
using Microsoft.Data.Sqlite;

namespace JeekMusic.Sync;

internal sealed class LanFileConflict(string reason):IOException(reason) { public string Reason=reason; }
internal sealed class LanFileDeferred(string reason):IOException(reason) { public string Reason=reason; }

/// <summary>Durable per-credential/per-mapping baselines and idempotent file transactions.
/// All calls are serialized by the service's dedicated file-work permit, away from UI/accept locks.</summary>
internal sealed partial class LanFileSyncStore:IDisposable
{
    internal sealed record Limits(long FileBytes=8L*1024*1024*1024,long StagingBytes=16L*1024*1024*1024,long BackupBytes=32L*1024*1024*1024,int Rows=1000000,int Scopes=128,int Operations=250000);
    readonly SqliteConnection db;
    readonly string staging;
    readonly Limits limits;
    long actions,conflicts,deferred;
    readonly Dictionary<string,long> confirmations=new();
    readonly LanHashBudget hashAdmission;
    void ReserveHash(string scope,string path) {
        var owner=(string)(Scalar("SELECT owner FROM scopes WHERE id=$s",("$s",scope))??throw new UnauthorizedAccessException());
        var bytes=File.Exists(path)?new FileInfo(path).Length:0;
        if(bytes>limits.FileBytes || !hashAdmission.Reserve(owner,bytes))throw new LanFileDeferred("hash_work_byte_budget");
    }
    long baselineCount,operationCount,backupTotal;readonly Dictionary<string,long> scopeCounts=new();
    public LanFileSyncStore(string directory,Limits? limits=null,LanHashBudget? hashAdmission=null)
    {
        this.hashAdmission=hashAdmission??new();this.limits=limits??new();Directory.CreateDirectory(directory);staging=Path.Combine(directory,"uploads");Directory.CreateDirectory(staging);
        SQLitePCL.Batteries_V2.Init();db=new(new SqliteConnectionStringBuilder { DataSource=Path.Combine(directory,"file-sync.db"),Pooling=false }.ToString());db.Open();
        Run("PRAGMA journal_mode=DELETE;PRAGMA synchronous=FULL;PRAGMA busy_timeout=5000;PRAGMA cache_size=-8192;PRAGMA max_page_count=262144;CREATE TABLE IF NOT EXISTS scopes(id TEXT PRIMARY KEY,device TEXT,owner TEXT,root TEXT,mapping TEXT,selection TEXT,revision TEXT);CREATE TABLE IF NOT EXISTS baseline(scope TEXT,path TEXT,hash TEXT,version TEXT,operation TEXT,PRIMARY KEY(scope,path));CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY,scope TEXT,json TEXT,state TEXT,created INTEGER);CREATE INDEX IF NOT EXISTS op_scope ON operations(scope);CREATE INDEX IF NOT EXISTS op_state ON operations(state);");
        bool backupColumn=false;using(var columns=Command("PRAGMA table_info(operations)"))using(var reader=columns.ExecuteReader())while(reader.Read())if(reader.GetString(1)=="backup_bytes")backupColumn=true;
        if(!backupColumn) { Run("ALTER TABLE operations ADD COLUMN backup_bytes INTEGER NOT NULL DEFAULT 0");Run("UPDATE operations SET backup_bytes=CASE WHEN json_extract(json,'$.backup') IS NOT NULL THEN COALESCE(json_extract(json,'$.backupBytes'),0) ELSE 0 END"); }
        bool refreshColumn=false;using(var columns=Command("PRAGMA table_info(operations)"))using(var reader=columns.ExecuteReader())while(reader.Read())if(reader.GetString(1)=="refresh_pending")refreshColumn=true;
        if(!refreshColumn)Run("ALTER TABLE operations ADD COLUMN refresh_pending INTEGER NOT NULL DEFAULT 0");Run("CREATE INDEX IF NOT EXISTS op_refresh ON operations(refresh_pending,state)");
        Run("CREATE TABLE IF NOT EXISTS write_budget(day TEXT,owner TEXT,bytes INTEGER,PRIMARY KEY(day,owner))");
        bool metadataColumn=false;using(var columns=Command("PRAGMA table_info(baseline)"))using(var reader=columns.ExecuteReader())while(reader.Read())if(reader.GetString(1)=="metadata")metadataColumn=true;
        if(!metadataColumn)Run("ALTER TABLE baseline ADD COLUMN metadata TEXT");
        operationCount=Convert.ToInt64(Scalar("SELECT COUNT(*) FROM operations"));backupTotal=Convert.ToInt64(Scalar("SELECT COALESCE(SUM(backup_bytes),0) FROM operations"));
        using var count=Command("SELECT scope,COUNT(*) FROM baseline GROUP BY scope");using var rows=count.ExecuteReader();while(rows.Read()) { var n=rows.GetInt64(1);scopeCounts[rows.GetString(0)]=n;baselineCount+=n; }

    }
    SqliteCommand Command(string sql,params (string,object?)[] args) { var c=db.CreateCommand();c.CommandText=sql;foreach(var(k,v) in args)c.Parameters.AddWithValue(k,v??DBNull.Value);return c; }
    void Run(string sql,params (string,object?)[] args) { using var c=Command(sql,args);c.ExecuteNonQuery(); }
    object? Scalar(string sql,params (string,object?)[] args) { using var c=Command(sql,args);return c.ExecuteScalar(); }
    static string Hash(string text)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static string S(JsonNode? n)=>n?.GetValue<string>()??"";
    static string? Digest(JsonNode? n)
    {
        if(n is null)return null;var s=n.GetValue<string>();if(s.Length!=64 || s.Any(c=>!char.IsAsciiHexDigitLower(c)))throw new IOException("Invalid content digest");return s;
    }
    static long N(JsonNode? node) {
        if(node is JsonValue value) { if(value.TryGetValue<long>(out var n))return n;if(value.TryGetValue<int>(out var i))return i; }
        throw new IOException("Integer field required");
    }
    static string Operation(string value) { if(!Guid.TryParseExact(value,"N",out _))throw new IOException("Invalid operation identity");return value; }
    static string Key(string scope,string operation)=>Hash(scope+":"+Operation(operation));
    public JsonObject Open(string device,string owner,string root,string mapping,IReadOnlyList<string> selection)
    {
        if(!Guid.TryParse(mapping,out _) || selection.Count is <1 or >1000)throw new IOException("Invalid mapping scope");
        var selected=selection.Select(p=>LanFiles.Relative(p,true)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var json=new JsonArray(selected.Select(p=>(JsonNode?)JsonValue.Create(p)).ToArray()).ToJsonString();
        var id=Hash(owner+":"+root+":"+mapping);
        if(Scalar("SELECT selection FROM scopes WHERE id=$s",("$s",id)) is string previous) { if(previous!=json)throw new LanFileConflict("mapping_selection_changed"); }
        else
        {
            if(Convert.ToInt64(Scalar("SELECT COUNT(*) FROM scopes"))>=limits.Scopes || Convert.ToInt64(Scalar("SELECT COUNT(*) FROM scopes WHERE owner=$o",("$o",owner)))>=8)throw new LanFileDeferred("scope_budget");
            Run("INSERT INTO scopes VALUES($s,$d,$o,$r,$m,$selection,$v)",("$s",id),("$d",device),("$o",owner),("$r",root),("$m",mapping),("$selection",json),("$v",Guid.NewGuid().ToString("N")));
        }
        return new JsonObject { ["fileProtocol"]=2,["comparison"]="size+mtime-exact",["scope"]=id,["revision"]=Revision(id),["total"]=Convert.ToInt64(Scalar("SELECT COUNT(*) FROM baseline WHERE scope=$s",("$s",id))) };
    }
    string Revision(string scope)=>(string)(Scalar("SELECT revision FROM scopes WHERE id=$s",("$s",scope))??throw new UnauthorizedAccessException());
    public string Root(string scope,string owner)
    {
        if(scope.Length!=64)throw new UnauthorizedAccessException();
        return (string)(Scalar("SELECT root FROM scopes WHERE id=$s AND owner=$o",("$s",scope),("$o",owner))??throw new UnauthorizedAccessException());
    }
    public void PathAllowed(string scope,string path)
    {
        LanFiles.Relative(path);
        if(!LanFileContent.Supported(path))throw new IOException("Unsupported file extension");
        var selected=JsonNode.Parse((string)Scalar("SELECT selection FROM scopes WHERE id=$s",("$s",scope))!)!.AsArray().Select(n=>n!.GetValue<string>());
        if(!selected.Any(p=>p=="" || path.StartsWith(p+"/",StringComparison.Ordinal) || path==p))throw new UnauthorizedAccessException();
    }
    public JsonObject Baselines(string scope,int offset,string revision)
    {
        if(offset<0 || Revision(scope)!=revision)throw new LanFileDeferred("baseline_changed");
        using var c=Command("SELECT path,hash,version,metadata FROM baseline WHERE scope=$s ORDER BY path LIMIT 200 OFFSET $offset",("$s",scope),("$offset",offset));using var r=c.ExecuteReader();var rows=new JsonArray();
        while(r.Read())rows.Add((JsonNode)WithMetadata(new JsonObject { ["path"]=r.GetString(0),["hash"]=r.IsDBNull(1)?null:r.GetString(1),["version"]=r.GetString(2) },r.IsDBNull(3)?null:r.GetString(3)));
        return new JsonObject { ["rows"]=rows,["revision"]=revision,["next"]=offset+rows.Count,["hasMore"]=rows.Count==200 };
    }
    JsonObject? Baseline(string scope,string path)
    {
        using var c=Command("SELECT hash,version,operation,metadata FROM baseline WHERE scope=$s AND path=$p",("$s",scope),("$p",path));using var r=c.ExecuteReader();
        return r.Read()?WithMetadata(new JsonObject { ["hash"]=r.IsDBNull(0)?null:r.GetString(0),["version"]=r.GetString(1),["operation"]=r.GetString(2) },r.IsDBNull(3)?null:r.GetString(3)):null;
    }
    public JsonObject BaselineRow(string scope,string path) { PathAllowed(scope,path);return Baseline(scope,path)??new JsonObject { ["hash"]=null,["version"]="",["tracked"]=false }; }
    JsonObject? RequireBaseline(string scope,string path,string version)
    {
        var baseline=Baseline(scope,path);if(S(baseline?["version"])!=version)throw new LanFileDeferred("baseline_changed");return baseline;
    }
    public async Task<JsonObject> Probe(string root,string path,CancellationToken ct,string? scope=null)
    {
        ct.ThrowIfCancellationRequested();await System.Threading.Tasks.Task.CompletedTask;return FileMetadata.Observe(root,path);
    }
    public async Task<JsonObject> Ack(string scope,string root,JsonObject a,CancellationToken ct,Action<Action> commit)
    {
        if(a.ContainsKey("remoteVersion"))return await AckMetadata(scope,root,a,ct,commit);
        var path=S(a["path"]);PathAllowed(scope,path);var operation=Operation(S(a["operation"]));var desired=Digest(a["hash"]);
        if(a["localKnown"]?.GetValue<bool>()!=true || Digest(a["localHash"])!=desired)throw new IOException("Both sides must confirm the same state");
        var previous=Baseline(scope,path);
        if(S(previous?["operation"])==operation && Digest(previous?["hash"])==desired)return previous!;
        RequireBaseline(scope,path,S(a["baselineVersion"]));
        var mutation=S(a["mutationOperation"]);JsonObject? receipt=null;
        if(mutation.Length>0) {
            receipt=Task(scope,Operation(mutation))??throw new IOException("Unknown mutation receipt");
            if(S(receipt["path"])!=path || Digest(receipt["hash"])!=desired || S(receipt["state"]) is not ("committed" or "acknowledged"))throw new IOException("Mutation receipt does not match acknowledgement");
        }
        using var file=new LanFileMutation(root,path,false);
        ReserveHash(scope,file.FullPath);
        if(await file.Observe(limits.FileBytes,ct)!=desired)throw Conflict("remote_changed_before_ack");
        if(previous is null && desired is null)return new JsonObject { ["hash"]=null,["version"]="" };
        if(previous is null && (baselineCount>=limits.Rows || scopeCounts.GetValueOrDefault(scope)>=100000))throw new LanFileDeferred("baseline_budget");
        var version=Guid.NewGuid().ToString("N");
        commit(()=>{});ct.ThrowIfCancellationRequested();RequireBaseline(scope,path,S(a["baselineVersion"]));
        Run("BEGIN IMMEDIATE");try {
                Run("INSERT OR REPLACE INTO baseline(scope,path,hash,version,operation) VALUES($s,$p,$h,$v,$o)",("$s",scope),("$p",path),("$h",desired),("$v",version),("$o",operation));
                Run("UPDATE scopes SET revision=$v WHERE id=$s",("$v",Guid.NewGuid().ToString("N")),("$s",scope));if(receipt!=null)SaveTask(scope,receipt,"acknowledged");Run("COMMIT");
        } catch { Run("ROLLBACK");throw; }
        if(previous is null) { baselineCount++;scopeCounts[scope]=scopeCounts.GetValueOrDefault(scope)+1; }
        if(S(a["decision"]) is "download" or "upload" or "delete_local" or "delete_remote" or "ack") { var decision=S(a["decision"]);confirmations[decision]=confirmations.GetValueOrDefault(decision)+1; }
        return new JsonObject { ["hash"]=desired,["version"]=version };
    }
    JsonObject? Task(string scope,string operation)
    {
        var raw=Scalar("SELECT json FROM operations WHERE id=$id AND scope=$s",("$id",Key(scope,operation)),("$s",scope)) as string;return raw==null?null:JsonNode.Parse(raw)!.AsObject();
    }
    void SaveTask(string scope,JsonObject task,string state)
    {
        if(state=="committed" && !task.ContainsKey("resultVersion") && task["rootPath"] is not null)
            task["resultVersion"]=FileMetadata.Observe(S(task["rootPath"]),S(task["path"]))["metadataVersion"]?.DeepClone();
        var id=Key(scope,S(task["operation"]));var old=Scalar("SELECT backup_bytes FROM operations WHERE id=$id",("$id",id));
        var bytes=task["backup"] is null?0:task["backupBytes"]?.GetValue<long>()??0;
        task["state"]=state;task["rootId"]??=Scalar("SELECT root FROM scopes WHERE id=$s",("$s",scope)) as string;
        Run("INSERT OR REPLACE INTO operations(id,scope,json,state,created,backup_bytes,refresh_pending) VALUES($id,$s,$j,$state,$time,$bytes,$refresh)",("$id",id),("$s",scope),("$j",task.ToJsonString()),("$state",state),("$time",DateTimeOffset.UtcNow.ToUnixTimeSeconds()),("$bytes",bytes),("$refresh",task["refreshPending"]?.GetValue<bool>()==true?1:0));
        backupTotal=checked(backupTotal-(old is null?0:Convert.ToInt64(old))+bytes);if(old is null)operationCount++;
    }
    string Partial(string scope,string operation)=>Path.Combine(staging,Key(scope,operation)+".part");
    public JsonObject Begin(string scope,JsonObject a)
    {
        Reap();
        var path=S(a["path"]);PathAllowed(scope,path);var op=Operation(S(a["operation"]));var hash=Digest(a["hash"])??throw new IOException("Upload digest required");var expected=Digest(a["expectedHash"]);var size=N(a["size"]);
        if(size is <0 || size>limits.FileBytes)throw new LanFileDeferred("file_byte_budget");
        if(Task(scope,op) is { } existing)
        {
            if(S(existing["path"])!=path || S(existing["hash"])!=hash || existing["size"]!.GetValue<long>()!=size || Digest(existing["expectedHash"])!=expected || S(existing["baselineVersion"])!=S(a["baselineVersion"]))throw new IOException("Operation identity reused");
            if(a.ContainsKey("expectedVersion") && (V(existing["expectedVersion"])!=V(a["expectedVersion"]) || existing["modifiedNs"]?.ToJsonString()!=a["modifiedNs"]?.ToJsonString()))throw new IOException("Operation metadata identity reused");
            return UploadStatus(scope,op);
        }
        var baseline=RequireBaseline(scope,path,S(a["baselineVersion"]));
        if(a.ContainsKey("expectedVersion"))RequireMetadataBaseline(baseline,a);else if(Digest(baseline?["hash"])!=expected && a["resolve"]?.GetValue<bool>()!=true)throw Conflict("remote_not_at_baseline");
        if(operationCount>=limits.Operations || Convert.ToInt64(Scalar("SELECT COUNT(*) FROM operations WHERE state IN ('uploading','prepared')"))>=4)throw new LanFileDeferred("operation_budget");
        long reserved=0;using(var q=Command("SELECT json FROM operations WHERE state IN ('uploading','prepared')"))using(var rows=q.ExecuteReader())while(rows.Read()) { var t=JsonNode.Parse(rows.GetString(0))!;reserved=checked(reserved+(t["size"]?.GetValue<long>()??0));if(S(t["device"])==S(a["deviceId"]))throw new LanFileDeferred("device_upload_budget"); }
        if(Math.Max(reserved,StagingBytes())+size>limits.StagingBytes || new DriveInfo(Path.GetPathRoot(staging)!).AvailableFreeSpace<size+64L*1024*1024)throw new LanFileDeferred("staging_byte_budget");
        var day=DateTime.UtcNow.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);var owner=(string)Scalar("SELECT owner FROM scopes WHERE id=$s",("$s",scope))!;
        Run("DELETE FROM write_budget WHERE day!=$d",("$d",day));
        var used=Convert.ToInt64(Scalar("SELECT COALESCE(SUM(bytes),0) FROM write_budget"));var own=Convert.ToInt64(Scalar("SELECT bytes FROM write_budget WHERE day=$d AND owner=$o",("$d",day),("$o",owner))??0L);
        if(used+size>256L*1024*1024*1024 || own+size>64L*1024*1024*1024)throw new LanFileDeferred("daily_upload_byte_budget");
        Run("INSERT OR REPLACE INTO write_budget VALUES($d,$o,$b)",("$d",day),("$o",owner),("$b",own+size));
        var task=new JsonObject { ["kind"]="upload",["device"]=S(a["deviceId"]),["operation"]=op,["path"]=path,["hash"]=hash,["expectedHash"]=expected,["baselineVersion"]=S(a["baselineVersion"]),["size"]=size,["modifiedNs"]=a["modifiedNs"]?.DeepClone(),["createdAt"]=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),["resolve"]=a["resolve"]?.GetValue<bool>()==true };
        if(a.ContainsKey("expectedVersion"))task["expectedVersion"]=a["expectedVersion"]?.DeepClone();
        SaveTask(scope,task,"uploading");
        if(size==0) { using var empty=LanFileMutation.OpenPrivate(Partial(scope,op));task["partialIdentity"]=LanFileMutation.Identity(empty);SaveTask(scope,task,"uploading"); }
        return UploadStatus(scope,op);
    }
    public JsonObject UploadStatus(string scope,string op)
    {
        var t=Task(scope,Operation(op));if(t is null)return new JsonObject { ["state"]="not_found",["operation"]=op,["offset"]=0,["committed"]=false };
        return new JsonObject { ["operation"]=op,["state"]=S(t["state"]),["offset"]=File.Exists(Partial(scope,op))?new FileInfo(Partial(scope,op)).Length:0,["hash"]=t["hash"]?.DeepClone(),["resultVersion"]=t["resultVersion"]?.DeepClone(),["committed"]=S(t["state"]) is "committed" or "acknowledged" };
    }
    public long MaximumFileBytes=>limits.FileBytes;
    public string OperationPath(string scope,string operation)=>S((Task(scope,Operation(operation))??throw new UnauthorizedAccessException())["path"]);
    public JsonObject Abort(string scope,string operation) {
        var task=Task(scope,Operation(operation))??throw new UnauthorizedAccessException();
        if(S(task["state"])=="committed" || S(task["state"])=="acknowledged")return UploadStatus(scope,operation);
        if(S(task["state"])=="prepared")return new JsonObject { ["deferred"]=true,["reason"]="transaction_needs_recovery" };
        CleanupPartial(scope,task);SaveTask(scope,task,"aborted");return new JsonObject { ["aborted"]=true };
    }
    public async Task<JsonObject> Chunk(string scope,JsonObject a,CancellationToken ct)
    {
        var op=Operation(S(a["operation"]));var task=Task(scope,op)??throw new UnauthorizedAccessException();if(S(task["state"])!="uploading")return UploadStatus(scope,op);
        var text=S(a["data"]);if(text.Length>349528)throw new IOException("Upload chunk budget exceeded");var bytes=Convert.FromBase64String(text);if(bytes.Length is <1 or >262144)throw new IOException("Upload chunk budget exceeded");
        var offset=N(a["offset"]);var file=Partial(scope,op);
        if(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-(task["createdAt"]?.GetValue<long>()??0)>7200)throw new LanFileDeferred("upload_expired");
        await using var output=LanFileMutation.OpenPrivate(file);
        var identity=LanFileMutation.Identity(output);
        if(task["partialIdentity"]!=null && S(task["partialIdentity"])!=identity)throw new LanFileConflict("private_staging_changed");
        task["partialIdentity"]=identity;SaveTask(scope,task,"uploading");
        if(offset<0 || offset>output.Length || offset+bytes.Length>task["size"]!.GetValue<long>())throw new IOException("Upload range rejected");
        if(offset<output.Length) { if(offset+bytes.Length>output.Length)throw new IOException("Ambiguous upload replay");output.Position=offset;var old=new byte[bytes.Length];await output.ReadExactlyAsync(old,ct);if(!old.AsSpan().SequenceEqual(bytes))throw new IOException("Upload replay changed"); }
        else { output.Position=offset;await output.WriteAsync(bytes,ct);Interlocked.Add(ref FileMetadata.TransferBytes,bytes.Length);await output.FlushAsync(ct);output.Flush(true); }
        return new JsonObject { ["offset"]=output.Length };
    }
    void BackupBudget(long bytes,string operationKey)
    {
        var old=Convert.ToInt64(Scalar("SELECT backup_bytes FROM operations WHERE id=$id",("$id",operationKey))??0L);
        if(backupTotal-old+bytes>limits.BackupBytes)throw new LanFileDeferred("backup_byte_budget");
    }
    public async Task<JsonObject> Finish(string scope,string root,string op,CancellationToken ct,Action<Action> commit)
    {
        var task=Task(scope,Operation(op))??throw new UnauthorizedAccessException();if(S(task["state"]) is "committed" or "acknowledged")return UploadStatus(scope,op);
        task["rootPath"]=Path.GetFullPath(root);
        var path=S(task["path"]);PathAllowed(scope,path);RequireBaseline(scope,path,S(task["baselineVersion"]));var expected=Digest(task["expectedHash"]);var desired=S(task["hash"]);var part=Partial(scope,op);
        if(!File.Exists(part) || new FileInfo(part).Length!=task["size"]!.GetValue<long>())throw new LanFileDeferred("upload_incomplete");
        await using(var input=new FileStream(part,FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,FileOptions.Asynchronous)) if(Convert.ToHexStringLower(await FileMetadata.Digest(input,ct))!=desired)throw new IOException("Upload integrity rejected");
        var references=await LanFileContent.Validate(part,path,ct);
        foreach(var reference in references) {
            PathAllowed(scope,reference);
            await using var audio=LanFiles.OpenRead(root,reference);
            if(audio.Length==0)throw new LanFileDeferred("cue_dependency_unavailable");
        }
        using var mutation=new LanFileMutation(root,path,true);
        ReserveHash(scope,mutation.FullPath);
        var targetDrive=new DriveInfo(Path.GetPathRoot(root)!);
        if(targetDrive.AvailableFreeSpace<task["size"]!.GetValue<long>()+Math.Min(5L*1024*1024*1024,Math.Max(256L*1024*1024,targetDrive.TotalSize/20)))throw new LanFileDeferred("target_free_space_reserve");
        // Stage first so a lengthy cross-volume copy never locks the user's existing file.
        await mutation.Stage(part,desired,task["size"]!.GetValue<long>(),op,ct);
        if(task["modifiedNs"] is not null)mutation.SetModified(N(task["modifiedNs"]));
        var current=await mutation.Observe(limits.FileBytes,ct,true);
        if(current==desired && (task["modifiedNs"] is null || FileMetadata.Token(FileMetadata.Observe(root,path))==FileMetadata.Version(N(task["size"]),N(task["modifiedNs"])/100*100))) { SaveTask(scope,task,"committed");CleanupPartial(scope,task);return UploadStatus(scope,op); }
        if(task.ContainsKey("expectedVersion") && current!=null && FileMetadata.Token(FileMetadata.Observe(root,path))!=V(task["expectedVersion"]))throw Conflict("concurrent_remote_metadata_change");
        var resumedBackup=current is null && expected!=null && S(task["state"])=="prepared"?await mutation.VerifiedBackup(op,expected,limits.FileBytes,ct):null;
        if(current!=expected && resumedBackup is null) { SaveTask(scope,task,"conflict");CleanupPartial(scope,task);throw Conflict("concurrent_remote_change"); }
        var backupBytes=resumedBackup??(current is null?0:new FileInfo(mutation.FullPath).Length);BackupBudget(backupBytes,Key(scope,op));
        task["refreshPending"]=true;task["rootPath"]=Path.GetFullPath(root);task["backup"]=current!=null || resumedBackup!=null?BackupPath(path,op):null;task["backupBytes"]=backupBytes;SaveTask(scope,task,"prepared");
        try { ct.ThrowIfCancellationRequested();mutation.Commit(op,false,commit); }
        catch {
            if(!mutation.Changed) { task.Remove("backup");task.Remove("backupBytes");var state=ct.IsCancellationRequested?"aborted":"uploading";if(state=="aborted")CleanupPartial(scope,task);SaveTask(scope,task,state); }throw;
        }
        if(mutation.BackupRelative!=null)task["backup"]=mutation.BackupRelative;SaveTask(scope,task,"committed");CleanupPartial(scope,task);Interlocked.Increment(ref actions);return UploadStatus(scope,op);
    }
    public async Task<JsonObject> Delete(string scope,string root,JsonObject a,CancellationToken ct,Action<Action> commit)
    {
        var path=S(a["path"]);PathAllowed(scope,path);var op=Operation(S(a["operation"]));var expected=Digest(a["expectedHash"])??throw new IOException("Tracked deletion requires an existing digest");
        var existing=Task(scope,op);
        if(existing!=null) {
            if(S(existing["kind"])!="delete" || S(existing["path"])!=path || Digest(existing["expectedHash"])!=expected || S(existing["baselineVersion"])!=S(a["baselineVersion"]))throw new IOException("Operation identity reused");
            if(a.ContainsKey("expectedVersion") && existing.ContainsKey("expectedVersion") && V(existing["expectedVersion"])!=V(a["expectedVersion"]))throw new IOException("Operation metadata identity reused");
            if(S(existing["state"]) is "committed" or "acknowledged")return new JsonObject { ["committed"]=true,["hash"]=null };
        }
        var baseline=RequireBaseline(scope,path,S(a["baselineVersion"]));if(a.ContainsKey("expectedVersion")) { RequireMetadataBaseline(baseline,a);if(baseline is null || baseline["remoteVersion"] is null && a["resolve"]?.GetValue<bool>()!=true)throw Conflict("deletion_not_tracked"); }else if(baseline is null || Digest(baseline["hash"]) is null || Digest(baseline["hash"])!=expected && a["resolve"]?.GetValue<bool>()!=true)throw Conflict("deletion_not_tracked");
        using var mutation=new LanFileMutation(root,path,false);ReserveHash(scope,mutation.FullPath);var current=await mutation.Observe(limits.FileBytes,ct,true);
        if(current!=expected && current!=null || a.ContainsKey("expectedVersion") && current!=null && FileMetadata.Token(FileMetadata.Observe(root,path))!=V(a["expectedVersion"]))throw Conflict("modified_vs_deleted");
        var task=existing??new JsonObject { ["kind"]="delete",["operation"]=op,["path"]=path,["expectedHash"]=expected,["baselineVersion"]=S(a["baselineVersion"]),["backup"]=current is null?null:BackupPath(path,op),["backupBytes"]=current is null?0:new FileInfo(mutation.FullPath).Length };
        if(a.ContainsKey("expectedVersion"))task["expectedVersion"]=a["expectedVersion"]?.DeepClone();
        task["refreshPending"]=true;task["rootPath"]=Path.GetFullPath(root);BackupBudget(task["backupBytes"]!.GetValue<long>(),Key(scope,op));SaveTask(scope,task,"prepared");
        if(current!=null) { try { ct.ThrowIfCancellationRequested();mutation.Commit(op,true,commit);task["backup"]=mutation.BackupRelative; }catch { if(!mutation.Changed) { task.Remove("backup");task.Remove("backupBytes");SaveTask(scope,task,"aborted"); }throw; } }
        SaveTask(scope,task,"committed");Interlocked.Increment(ref actions);return new JsonObject { ["committed"]=true,["hash"]=null };
    }
    long StagingBytes()
    {
        long bytes=0;var count=0;foreach(var file in Directory.EnumerateFiles(staging,"*.part")) { if(++count>4096)throw new LanFileDeferred("staging_count_budget");bytes=checked(bytes+new FileInfo(file).Length);if(bytes>limits.StagingBytes)break; }return bytes;
    }
    void CleanupPartial(string scope,JsonObject task)
    {
        var path=Partial(scope,S(task["operation"]));if(!File.Exists(path) || task["partialIdentity"] is null)return;
        try { using var file=LanFileMutation.OpenPrivate(path,false);LanFileMutation.DeleteOwned(file,S(task["partialIdentity"])); }catch(IOException) { /* Count retained bytes against admission; never unlink a substituted object. */ }
    }
    void Reap()
    {
        var stale=new List<(string Scope,JsonObject Task)>();using(var c=Command("SELECT scope,json,created FROM operations WHERE state='uploading'"))using(var r=c.ExecuteReader())while(r.Read()) {
            var task=JsonNode.Parse(r.GetString(1))!.AsObject();var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if(now-r.GetInt64(2)>600 || now-(task["createdAt"]?.GetValue<long>()??now)>7200)stale.Add((r.GetString(0),task));
        }
        foreach(var (scope,task) in stale) { CleanupPartial(scope,task);SaveTask(scope,task,"expired"); }
    }
    public void RetireUploads(string device) {
        var tasks=new List<(string Scope,JsonObject Task)>();using(var query=Command("SELECT scope,json FROM operations WHERE state='uploading'"))using(var reader=query.ExecuteReader())while(reader.Read()) { var row=JsonNode.Parse(reader.GetString(1))!.AsObject();if(S(row["device"])==device)tasks.Add((reader.GetString(0),row)); }
        foreach(var (scope,row) in tasks) { CleanupPartial(scope,row);SaveTask(scope,row,"aborted"); }
    }
    static string BackupPath(string path,string operation)=>path.Contains('/')?path[..(path.LastIndexOf('/')+1)]+".jeek-sync-"+operation+".bak":".jeek-sync-"+operation+".bak";
    LanFileConflict Conflict(string reason) { Interlocked.Increment(ref conflicts);return new(reason); }
    public void RecordDeferred()=>Interlocked.Increment(ref deferred);
    public IReadOnlyList<(string Id,string Root,string Path)> PendingRefresh() {
        using var command=Command("SELECT id,json FROM operations WHERE refresh_pending=1 AND state IN ('committed','acknowledged') LIMIT 200");using var reader=command.ExecuteReader();var rows=new List<(string,string,string)>();
        while(reader.Read()) { var row=JsonNode.Parse(reader.GetString(1))!;rows.Add((reader.GetString(0),S(row["rootId"]),S(row["path"]))); }return rows;
    }
    public void Refreshed(IEnumerable<string> receipts) { foreach(var id in receipts) { var json=Scalar("SELECT json FROM operations WHERE id=$i",("$i",id)) as string;if(json==null)continue;var row=JsonNode.Parse(json)!;row["refreshPending"]=false;Run("UPDATE operations SET refresh_pending=0,json=$j WHERE id=$i",("$j",row.ToJsonString()),("$i",id)); } }
    public JsonObject Backups(int offset=0) {
        if(offset<0)throw new ArgumentException("Invalid offset");
        var rows=new JsonArray();using var q=Command("SELECT id,json FROM operations WHERE backup_bytes>0 ORDER BY id LIMIT 100 OFFSET $offset",("$offset",offset));using var reader=q.ExecuteReader();
        while(reader.Read()) { var row=JsonNode.Parse(reader.GetString(1))!;rows.Add((JsonNode)new JsonObject { ["receipt"]=reader.GetString(0),["rootId"]=row["rootId"]?.DeepClone(),["path"]=row["path"]?.DeepClone(),["bytes"]=row["backupBytes"]?.DeepClone(),["state"]=row["state"]?.DeepClone(),["kind"]=row["kind"]?.DeepClone() }); }
        return new JsonObject { ["backups"]=rows,["next"]=offset+rows.Count,["hasMore"]=rows.Count==100,["retainedBytes"]=backupTotal };
    }
    public string BackupRoot(string receipt)=>(string)(Scalar("SELECT json_extract(json,'$.rootId') FROM operations WHERE id=$id AND backup_bytes>0",("$id",receipt))??throw new ArgumentException("Unknown backup receipt"));
    public async Task<JsonObject> MaintainBackup(string receipt,string root,bool purge,CancellationToken ct,Action<Action> commit) {
        var raw=Scalar("SELECT json FROM operations WHERE id=$id AND backup_bytes>0",("$id",receipt)) as string??throw new ArgumentException("Unknown backup receipt");
        var row=JsonNode.Parse(raw)!.AsObject();var scope=(string)Scalar("SELECT scope FROM operations WHERE id=$id",("$id",receipt))!;
        var path=S(row["path"]);var op=S(row["operation"]);var original=Digest(row["expectedHash"])??throw new IOException("No recoverable original");
        using var mutation=new LanFileMutation(root,path,true);
        if(await mutation.VerifiedBackup(op,original,limits.FileBytes,ct) is null)throw new LanFileConflict("backup_changed_or_missing");
        if(purge) {
            if(S(row["state"])!="acknowledged")throw new LanFileDeferred("unconfirmed_backup_retained");
            await mutation.PurgeBackup(op,original,limits.FileBytes,ct,commit);row.Remove("backup");row.Remove("backupBytes");SaveTask(scope,row,S(row["state"]));return new JsonObject { ["purged"]=true };
        }
        // Restore only when the live path still equals the exact result of this transaction.
        // This local administrator action is itself journaled and leaves a backup of that result.
        var desired=Digest(row["hash"]);var actual=await mutation.Observe(limits.FileBytes,ct,true);
        if(actual!=desired && !(S(row["state"])=="prepared" && actual is null))throw new LanFileConflict("live_file_changed_since_backup");
        var restore=Guid.NewGuid().ToString("N");var backup=Path.Combine(Path.GetDirectoryName(mutation.FullPath)!,".jeek-sync-"+op+".bak");
        var bytes=new FileInfo(backup).Length;BackupBudget(actual is null?0:new FileInfo(mutation.FullPath).Length,Key(scope,restore));
        var task=new JsonObject { ["kind"]="admin_restore",["operation"]=restore,["path"]=path,["hash"]=original,["expectedHash"]=actual,["backup"]=actual is null?null:BackupPath(path,restore),["backupBytes"]=actual is null?0:new FileInfo(mutation.FullPath).Length,["size"]=bytes };
        task["refreshPending"]=true;task["rootPath"]=Path.GetFullPath(root);SaveTask(scope,task,"prepared");await mutation.Stage(backup,original,bytes,restore,ct);ct.ThrowIfCancellationRequested();lock(Online.TagWriteCommit.Gate)mutation.Commit(restore,false,commit);SaveTask(scope,task,"acknowledged");
        if(S(row["state"])=="prepared") { CleanupPartial(scope,row);SaveTask(scope,row,"aborted"); }
        return new JsonObject { ["restored"]=true,["baselineChanged"]=false };
    }
    public JsonObject Diagnostics()=>new() { ["fileProtocol"]=2,["comparison"]="size+mtime-exact",["sidecarParseBytes"]=Interlocked.Read(ref FileMetadata.SidecarParseBytes),["metadataObservations"]=Interlocked.Read(ref FileMetadata.MetadataObservations),["hashBytes"]=Interlocked.Read(ref FileMetadata.HashBytes),["transferBytes"]=Interlocked.Read(ref FileMetadata.TransferBytes),["hashPurpose"]="operation_integrity_commit_recovery_only",["schedules"]=Schedules(),["fileWorkLimit"]=1,["taskSeconds"]=900,["deviceDailyUploadBytesLimit"]=64L*1024*1024*1024,["globalDailyUploadBytesLimit"]=256L*1024*1024*1024,["actions"]=Interlocked.Read(ref actions),["conflicts"]=Interlocked.Read(ref conflicts),["peerConfirmedActions"]=new JsonObject(confirmations.Select(p=>new KeyValuePair<string,JsonNode?>(p.Key,JsonValue.Create(p.Value)))),["deferred"]=Interlocked.Read(ref deferred),["baselineRows"]=baselineCount,["operationRecords"]=operationCount,["retainedBackupBytes"]=backupTotal,["baselineRowsLimit"]=limits.Rows,["scopeRowsLimit"]=100000,["metadataBytesLimit"]=1024L*1024*1024,["fileBytesLimit"]=limits.FileBytes,["stagingBytesLimit"]=limits.StagingBytes,["backupBytesLimit"]=limits.BackupBytes };
    public void Dispose()=>db.Dispose();
    public static bool BlocksScan(string directory,string root) {
        var file=Path.Combine(directory,"file-sync.db");if(!File.Exists(file))return false;
        try {
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=file,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1 }.ToString());connection.Open();
            using var query=connection.CreateCommand();query.CommandText="SELECT json FROM operations WHERE state='prepared'";using var reader=query.ExecuteReader();
            while(reader.Read())if(string.Equals(Path.TrimEndingDirectorySeparator(S(JsonNode.Parse(reader.GetString(0))?["rootPath"])),Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),StringComparison.OrdinalIgnoreCase))return true;
            return false;
        }catch(Exception e) when(e is SqliteException or IOException or System.Text.Json.JsonException) { return true; }
    }
}
