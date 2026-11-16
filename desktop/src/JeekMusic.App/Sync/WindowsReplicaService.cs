using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using JeekMusic.Core.Sync;
using JeekMusic.Infrastructure;

namespace JeekMusic.Sync;

/// <summary>One serialized file worker, independent of group metadata networking.
/// A durable mapping owns both its checkpoints and every unfinished transaction.</summary>
public sealed partial class WindowsReplicaService
{
    readonly AppServices app;
    readonly string directory;
    readonly WindowsReplicaStore store;
    readonly SemaphoreSlim work=new(1,1);
    readonly CancellationTokenSource lifetime=new();
    readonly Lock controlGate=new();
    readonly Dictionary<string,CancellationTokenSource> running=[];
    readonly Avalonia.Threading.DispatcherTimer timer;
    LanFileSyncStore? files;
    int turn;
    public event Action? Changed;
    static string S(JsonNode? value)=>value?.GetValue<string>()??"";
    static long N(JsonNode? value)=>SyncMerge.Number(value);
    static string Id()=>Guid.NewGuid().ToString("N");
    static string Digest(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal WindowsReplicaService(AppServices app,string? directory=null)
    {
        this.app=app;this.directory=directory??Path.Combine(AppPaths.DataDir,"windows-replicas");
        store=new(this.directory);
        timer=new() { Interval=TimeSpan.FromSeconds(30) };timer.Tick+=async (_,_)=>await Tick();timer.Start();
        app.OnExit+=()=> { timer.Stop();lifetime.Cancel();lock(controlGate)foreach(var source in running.Values)source.Cancel(); };
    }
    static bool Overlaps(string first,string second)
    {
        first=Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));second=Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return first.Equals(second,StringComparison.OrdinalIgnoreCase) || first.StartsWith(second+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || second.StartsWith(first+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
    public JsonObject Status(string? mappingId=null)
    {
        var mappings=mappingId is null?store.Page("mapping").Select(r=>r.Value):[Mapping(mappingId)];
        return new() { ["mappings"]=new JsonArray(mappings.Select(row=> {
            var copy=row.DeepClone().AsObject();copy.Remove("nativeIdentity");copy.Remove("credentialIdentity");
            copy["pendingTransactions"]=store.Count("pending:"+S(row["mappingId"]));copy["conflicts"]=store.Count("conflict:"+S(row["mappingId"]));return (JsonNode)copy;
        }).ToArray()),["comparison"]="mtime_then_size_then_last_edit",["scanReadsAudio"]=false,["fileWorkLimit"]=1 };
    }
    JsonObject Mapping(string id)=>store.Read("mapping",id)??throw new ArgumentException("Unknown mappingId");
    public JsonObject Bind(string collection,long folderId)
    {
        Dispatcher.UIThread.VerifyAccess();
        var remote=app.GroupSync.RequireCollection(collection);var group=app.GroupSync.Status();
        var folder=app.Db.Folders().Single(f=>f.Id==folderId);
        if(!folder.Available)throw new LanFileDeferred("local_directory_unavailable");
        var pin=LanFileMutation.PinRoot(folder.Root);using var handle=pin.Handle;
        var mappings=store.Page("mapping");
        if(mappings.Count>=8)throw new LanFileDeferred("scope_budget");
        foreach(var (_,row) in mappings)
            if(S(row["state"])!="unbound" && (Overlaps(folder.Root,S(row["localRoot"])) || S(row["collectionId"])==collection))throw new GroupSyncException("overlapping_replica_binding");
        var id=Id();var connection=app.GroupSync.Upstream();
        var mapping=new JsonObject { ["mappingId"]=id,["collectionId"]=collection,["name"]=remote["name"]!.DeepClone(),["rootId"]=remote["rootId"]!.DeepClone(),
            ["folderId"]=folder.Id,["folderIdentity"]=folder.Identity,["localRoot"]=folder.Root,["nativeIdentity"]=pin.Identity,
            ["groupId"]=group["groupId"]!.DeepClone(),["peerId"]=group["peerId"]!.DeepClone(),
            ["credentialIdentity"]=Digest(S(connection["pin"])+":"+S(connection["token"])),["state"]="binding",["paused"]=true,["cursor"]="" };
        store.Put("mapping",id,mapping); // Recoverable intent precedes the business identity transaction.
        CompleteBinding(mapping);Changed?.Invoke();return Status(id);
    }
    void CompleteBinding(JsonObject mapping)
    {
        var folder=app.Db.Folders().SingleOrDefault(f=>f.Id==N(mapping["folderId"]) && f.Identity==S(mapping["folderIdentity"]));
        if(folder is not { Available:true } || !folder.Root.Equals(S(mapping["localRoot"]),StringComparison.OrdinalIgnoreCase))throw new LanFileDeferred("local_directory_unavailable");
        var pin=LanFileMutation.PinRoot(folder.Root,S(mapping["nativeIdentity"]));using var handle=pin.Handle;
        var bound=app.GroupSync.BindReplicaIdentity(N(mapping["folderId"]),S(mapping["collectionId"]));
        mapping["localRootId"]=bound["localRootId"]!.DeepClone();mapping["state"]="paused";store.Put("mapping",S(mapping["mappingId"]),mapping);
    }
    public JsonObject Control(string mappingId,string action)
    {
        var mapping=Mapping(mappingId);
        lock(controlGate)
        {
            if(action is "pause" or "cancel")
            {
                mapping["paused"]=true;mapping["state"]=action=="cancel"?"cancelled":"paused";
                store.Put("mapping",mappingId,mapping);if(running.TryGetValue(mappingId,out var source))source.Cancel();
            }
            else if(action is "resume" or "start")
            {
                if(S(mapping["state"])=="unbound")throw new ArgumentException("Mapping is unbound");
                mapping["paused"]=false;mapping["state"]="scheduled";mapping["retryAt"]=0L;store.Put("mapping",mappingId,mapping);
            }
            else if(action=="unbind")
            {
                if(mapping["paused"]?.GetValue<bool>()!=true || running.ContainsKey(mappingId) || store.Count("pending:"+mappingId)>0)throw new LanFileDeferred("pause_and_confirm_transactions_before_unbind");
                mapping["state"]="unbound";store.Put("mapping",mappingId,mapping);
            }
            else throw new ArgumentException("Invalid mapping action");
        }
        Changed?.Invoke();return Status(mappingId);
    }
    async Task Tick()
    {
        if(lifetime.IsCancellationRequested || !app.GroupSync.Active || app.GroupSync.Coordinator)return;
        var mappings=store.Page("mapping").Where(r=>r.Value["paused"]?.GetValue<bool>()!=true && S(r.Value["state"])!="unbound" && N(r.Value["retryAt"])<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToArray();
        if(mappings.Length>0)await Synchronize(mappings[(uint)Interlocked.Increment(ref turn)%(uint)mappings.Length].Key);
    }
    public async Task<JsonObject> Synchronize(string mappingId,CancellationToken cancellation=default)
    {
        if(!await work.WaitAsync(0,cancellation))return Status(mappingId);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);
        linked.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var mapping=Mapping(mappingId);
            if(mapping["paused"]?.GetValue<bool>()==true || S(mapping["state"])=="unbound")return Status(mappingId);
            if(S(mapping["localRootId"]).Length==0) { await Dispatcher.UIThread.InvokeAsync(()=>CompleteBinding(mapping),DispatcherPriority.Normal,linked.Token).GetTask().ConfigureAwait(false);mapping=Mapping(mappingId);mapping["paused"]=false;store.Put("mapping",mappingId,mapping); }
            lock(controlGate)running[mappingId]=linked;
            if(app.GroupSync.Status()["paused"]?.GetValue<bool>()==true)throw new LanFileDeferred("group_paused");
            await Task.Run(()=>Round(mapping,linked.Token),linked.Token).ConfigureAwait(false);
            Update(mappingId,row=> { if(row["paused"]?.GetValue<bool>()!=true)row["state"]="idle";row["lastError"]="";row["lastSuccess"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();row["failures"]=0;row["retryAt"]=0L; });
        }
        catch(Exception exception) when(exception is IOException or OperationCanceledException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Net.Sockets.SocketException or Microsoft.Data.Sqlite.SqliteException)
        {
            var code=exception is LanPeerException peer?peer.Code:exception is GroupSyncException group?group.Code:exception is LanFileDeferred deferred?deferred.Reason:exception is LanFileConflict conflict?conflict.Reason:exception is OperationCanceledException?"cancelled":"file_unavailable";
            Update(mappingId,row=> { var failures=Math.Min(10,N(row["failures"])+1);row["lastError"]=code;row["failures"]=failures;row["retryAt"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+Math.Min(900_000,30_000L*(1L<<(int)failures));if(row["paused"]?.GetValue<bool>()!=true)row["state"]="waiting"; });
        }
        finally { lock(controlGate)running.Remove(mappingId);work.Release();Changed?.Invoke(); }
        return Status(mappingId);
    }
    void Update(string mappingId,Action<JsonObject> change)
    {
        lock(controlGate) { var row=Mapping(mappingId);change(row);store.Put("mapping",mappingId,row); }
    }
    IEnumerable<(string Key,JsonObject Value)> Rows(string kind)
    {
        string after="";while(true) { var page=store.Page(kind,after);foreach(var row in page)yield return row;if(page.Count==0)yield break;after=page[^1].Key; }
    }
    internal void StopForExit()
    {
        timer.Stop();lifetime.Cancel();
        lock(controlGate)foreach(var source in running.Values)source.Cancel();
        work.Wait();try { files?.Dispose();store.Dispose(); }finally { work.Release(); }
    }
    public JsonObject Conflicts(string mappingId,string? conflictId=null,string? replicaId=null,string? version=null,int offset=0)
    {
        Mapping(mappingId);if(offset<0)throw new ArgumentException("Invalid offset");
        if(conflictId is not null)
        {
            lock(controlGate)
            {
                var row=Rows("conflict:"+mappingId).SingleOrDefault(r=>S(r.Value["id"])==conflictId);
                if(row.Value is null)throw new LanFileConflict("stale_conflict");
                var side=replicaId==S(row.Value["localReplicaId"])?"local":replicaId==S(row.Value["remoteReplicaId"])?"remote":throw new ArgumentException("Unknown replicaId");
                if(row.Value[side+"Version"]?.GetValue<string>()!=version)throw new LanFileConflict("stale_version");
                if(row.Value["choice"] is not null && S(row.Value["choice"])!=replicaId)throw new LanFileConflict("conflict_already_chosen");
                row.Value["choice"]=replicaId;store.Put("conflict:"+mappingId,row.Key,row.Value);
            }
            Changed?.Invoke();
        }
        var rows=Rows("conflict:"+mappingId).Skip(offset).Take(101).ToArray();
        return new() { ["mappingId"]=mappingId,["rows"]=new JsonArray(rows.Take(100).Select(r=>(JsonNode)r.Value).ToArray()),["next"]=offset+Math.Min(100,rows.Length),["hasMore"]=rows.Length>100 };
    }
    public async Task<JsonObject> Backups(string mappingId,string action="list",string? receipt=null,int offset=0)
    {
        if(!await work.WaitAsync(0))throw new LanFileDeferred("file_work_busy");
        try
        {
            var mapping=Mapping(mappingId);files??=new(Path.Combine(directory,"target"));
            if(action=="list")
            {
                var page=files.Backups(offset);page["backups"]=new JsonArray(page["backups"]!.AsArray().OfType<JsonObject>().Where(r=>S(r["rootId"])==S(mapping["localRootId"])).Select(r=>r.DeepClone()).ToArray());return page;
            }
            if(action is not ("restore" or "purge") || receipt is null || receipt.Length!=64 || receipt.Any(c=>!char.IsAsciiHexDigitLower(c)))throw new ArgumentException("Invalid backup action/receipt");
            if(mapping["paused"]?.GetValue<bool>()!=true)throw new LanFileDeferred("pause_before_backup_recovery");
            if(files.BackupRoot(receipt)!=S(mapping["localRootId"]))throw new ArgumentException("Backup belongs to another mapping");
            if(!app.Db.Folders().Any(f=>f.Id==N(mapping["folderId"]) && f.Identity==S(mapping["folderIdentity"]) && f.Available && f.Root.Equals(S(mapping["localRoot"]),StringComparison.OrdinalIgnoreCase)))throw new LanFileDeferred("local_directory_unavailable");
            var target=Path.Combine(S(mapping["localRoot"]),files.BackupRelativePath(receipt).Replace('/',Path.DirectorySeparatorChar));
            if(app.Player.IsPathInUse(target))throw new LanFileDeferred("playing_file");
            var root=S(mapping["localRoot"]);var pin=LanFileMutation.PinRoot(root,S(mapping["nativeIdentity"]));using var handle=pin.Handle;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);deadline.CancelAfter(TimeSpan.FromMinutes(15));
            var result=await Task.Run(()=>files.MaintainBackup(receipt,root,action=="purge",deadline.Token,commit=> {
                lock(controlGate) { deadline.Token.ThrowIfCancellationRequested();if(Mapping(mappingId)["paused"]?.GetValue<bool>()!=true)throw new LanFileDeferred("mapping_resumed_during_recovery");if(app.Player.IsPathInUse(target))throw new LanFileDeferred("playing_file");commit(); }
            }),deadline.Token).ConfigureAwait(false);
            var refresh=files.PendingRefresh().Where(r=>r.Root==S(mapping["localRootId"])).ToArray();
            if(refresh.Length>0 && await app.Library.RefreshSyncedFilesAsync(N(mapping["folderId"]),refresh.Select(r=>r.Path).Distinct().ToArray()))files.Refreshed(refresh.Select(r=>r.Id).ToArray());
            return result;
        }
        finally { work.Release();Changed?.Invoke(); }
    }
}
