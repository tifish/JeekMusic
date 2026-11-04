using System.Text.Json.Nodes;
using JeekMusic.Infrastructure;

namespace JeekMusic.Sync;

public sealed partial class LanSyncService
{
    readonly SemaphoreSlim _fileWork=new(1);
    LanFileSyncStore? _fileStore;
    volatile JsonObject _fileDiagnostics=new() { ["fileProtocol"]=2,["comparison"]="size+mtime-exact",["state"]="idle",["fileWorkLimit"]=1,["taskSeconds"]=900,["fileBytesLimit"]=8L*1024*1024*1024,["stagingBytesLimit"]=16L*1024*1024*1024,["backupBytesLimit"]=32L*1024*1024*1024,["deviceDailyUploadBytesLimit"]=64L*1024*1024*1024,["globalDailyUploadBytesLimit"]=256L*1024*1024*1024 };
    int _refreshingFiles;
    public long ConflictCount { get; private set; }
    public async Task<JsonObject> FileConflicts(string action="list",string? id=null,string? choice=null,int offset=0) {
        await _fileWork.WaitAsync();
        try {
            return await Task.Run(()=> {
                _fileStore??=new LanFileSyncStore(Path.Combine(AppPaths.DataDir,"lan-file-sync"),hashAdmission:_hashAdmission);
                if(action=="list") {
                    var page=_fileStore.ConflictPage(offset);
                    lock(_gate)foreach(var row in page["rows"]!.AsArray().OfType<JsonObject>())row["deviceName"]=Devices[row["device"]!.GetValue<string>()]?["name"]?.GetValue<string>()??"";
                    return page;
                }
                if(action!="choose" || id is null || choice is null)throw new ArgumentException("Invalid conflict action");
                var scope=_fileStore.ConflictScope(id);var owner=_fileStore.ConflictOwner(scope);
                lock(_gate)if(Devices[owner.Device] is not JsonObject device || device["revoked"]?.GetValue<bool>()==true)throw new UnauthorizedAccessException();
                return _fileStore.ChooseConflict(scope,RootPath(owner.Root),id,choice);
            });
        } finally { if(_fileStore!=null)ConflictCount=_fileStore.PendingConflictCount;_fileWork.Release();Notify(); }
    }
    readonly HashSet<string> _retiringUploads=new();
    void RetireFileUploads(string device) {
        lock(_retiringUploads)if(!_retiringUploads.Add(device))return;
        _=Task.Run(async ()=> {
            await _fileWork.WaitAsync();try { _fileStore?.RetireUploads(device); }catch(Exception e) when(e is IOException or Microsoft.Data.Sqlite.SqliteException) { }finally { _fileWork.Release();lock(_retiringUploads)_retiringUploads.Remove(device); }
        });
    }
    void ScheduleFileRefresh() {
        if(Interlocked.Exchange(ref _refreshingFiles,1)!=0)return;
        _=Task.Run(async ()=> {
            try {
                await Task.Delay(1000);
                for(var batch=0;batch<8;batch++) {
                    if(!await _fileWork.WaitAsync(TimeSpan.FromSeconds(5)))return;
                    IReadOnlyList<(string Id,string Root,string Path)> rows;
                    try { _fileStore??=new LanFileSyncStore(Path.Combine(AppPaths.DataDir,"lan-file-sync"),hashAdmission:_hashAdmission);rows=_fileStore.PendingRefresh(); }finally { _fileWork.Release(); }
                    if(rows.Count==0)return;
                    var completed=new List<string>();
                    foreach(var group in rows.GroupBy(r=>r.Root)) {
                        long? folderId;lock(_gate)folderId=Roots[group.Key]?["revoked"]?.GetValue<bool>()==true?null:Roots[group.Key]?["folderId"]?.GetValue<long>();
                        if(folderId!=null && await _app.Library.RefreshSyncedFilesAsync(folderId.Value,group.Select(r=>r.Path).Distinct().ToArray()))completed.AddRange(group.Select(r=>r.Id));
                    }
                    if(completed.Count==0)return;
                    if(!await _fileWork.WaitAsync(TimeSpan.FromSeconds(5)))return;
                    try { _fileStore!.Refreshed(completed); }finally { _fileWork.Release(); }
                }
            }catch(Exception e) when(e is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { /* Durable refresh receipts remain pending. */ }
            finally { Interlocked.Exchange(ref _refreshingFiles,0); }
        });
    }
    JsonObject FileDiagnostics() {
        var result=_fileDiagnostics.DeepClone().AsObject();
        result["pendingConflicts"]=ConflictCount;result["conflictProtocol"]=1;
        result["metadataObservations"]=Interlocked.Read(ref JeekMusic.Core.Sync.FileMetadata.MetadataObservations);
        result["hashBytes"]=Interlocked.Read(ref JeekMusic.Core.Sync.FileMetadata.HashBytes);
        result["transferBytes"]=Interlocked.Read(ref JeekMusic.Core.Sync.FileMetadata.TransferBytes);
        result["sidecarParseBytes"]=Interlocked.Read(ref JeekMusic.Core.Sync.FileMetadata.SidecarParseBytes);
        return result;
    }
    public async Task<JsonObject> FileBackups(string action,string? receipt=null,int offset=0) {
        if(!_fileWork.Wait(0))throw new InvalidOperationException("File work busy; retry later");
        try {
            _fileStore??=new LanFileSyncStore(Path.Combine(AppPaths.DataDir,"lan-file-sync"),hashAdmission:_hashAdmission);
            if(action=="list")return _fileStore.Backups(offset);
            if(action is not ("restore" or "purge") || receipt is null || receipt.Length!=64 || receipt.Any(c=>!char.IsAsciiHexDigitLower(c)))throw new ArgumentException("Invalid backup action/receipt");
            // Stopped service prevents remote mutation/revocation racing local administrator recovery.
            if(Port!=0)throw new InvalidOperationException("Stop LAN service before backup recovery or cleanup");
            var rootId=_fileStore.BackupRoot(receipt);var root=RootPath(rootId);var revision=_rootAuthorizationRevision;using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(15));
            return await _fileStore.MaintainBackup(receipt,root,action=="purge",deadline.Token,commit=> { lock(_gate) { deadline.Token.ThrowIfCancellationRequested();if(Port!=0 || revision!=_rootAuthorizationRevision || Roots[rootId]?["revoked"]?.GetValue<bool>()==true)throw new InvalidOperationException("Service/root changed; retry after stopping");commit(); } });
        } finally { if(_fileStore!=null)_fileDiagnostics=_fileStore.Diagnostics();_fileWork.Release();ScheduleFileRefresh(); }
    }
    string FileOwner(string device) { lock(_gate) { if(Devices[device] is not JsonObject record)throw new UnauthorizedAccessException();return Hash(device+":"+record["tokenHash"]!.GetValue<string>()); } }
    async Task<JsonObject> DispatchFile(JsonObject a,CancellationToken request,bool modern)
    {
        var op=a["op"]!.GetValue<string>();
        if(!modern || a["fileProtocol"]?.GetValue<int>()!=2)return Error("upgrade_required");
        if(op is not ("file_open" or "file_revision" or "file_schedule" or "file_rows" or "file_baseline" or "file_probe" or "file_verify" or "file_ack" or "file_begin" or "file_chunk" or "file_finish" or "file_delete" or "file_upload_status" or "file_read" or "file_abort" or "file_conflict" or "file_conflict_choose" or "file_conflict_clear"))throw new ArgumentException("Unknown file operation");
        if(!_fileWork.Wait(0))throw Busy();
        bool handedOff=false;
        var independent=op is "file_verify" or "file_ack" or "file_finish" or "file_delete";
        CancellationTokenSource? lifetime=null;
        try
        {
            var auth=Authenticate(a);var owner=FileOwner(auth.Device);var revision=_rootAuthorizationRevision;
            lifetime=CancellationTokenSource.CreateLinkedTokenSource(auth.Lifetime,_stop!.Token);
            lifetime.CancelAfter(TimeSpan.FromMinutes(15));
            var token=independent?lifetime.Token:request;
            async Task<JsonObject> Work()
            {
                try
                {
                    _fileStore??=new LanFileSyncStore(Path.Combine(AppPaths.DataDir,"lan-file-sync"),hashAdmission:_hashAdmission);
                    var store=_fileStore;
                    string scope=a["scope"]?.GetValue<string>()??"";
                    string rootId=op=="file_open"?a["rootId"]!.GetValue<string>():store.Root(scope,owner);
                    var root=RootPath(rootId);
                    var mutationPath=op=="file_finish"?store.OperationPath(scope,a["operation"]!.GetValue<string>()):op=="file_delete"?a["path"]!.GetValue<string>():null;
                    void MutationGuard(Action action) {
                        var path=Path.Combine(root,(mutationPath??throw new InvalidOperationException()).Replace('/',Path.DirectorySeparatorChar));
                        if(_app.Player.IsPathInUse(path))throw new LanFileDeferred("playing_file");
                        lock(Online.TagWriteCommit.Gate)Guard(()=> { if(_app.Player.IsPathInUse(path))throw new LanFileDeferred("playing_file");action(); });
                    }
                    void Guard(Action commit) {
                        lock(_gate) {
                            token.ThrowIfCancellationRequested();var current=Authenticate(a);RequireOperation(auth.Device,op);
                            if(current.Generation!=auth.Generation || revision!=_rootAuthorizationRevision || Roots[rootId]?["enabled"]?.GetValue<bool>()!=true)throw new UnauthorizedAccessException();
                            commit(); // Only handle renames here; no hashing, copying or SQLite I/O.
                        }
                    }
                    Guard(()=>{});
                    JsonObject result;
                    switch(op)
                    {
                        case "file_open":result=store.Open(auth.Device,owner,rootId,a["mapping"]!.GetValue<string>(),a["selection"]!.AsArray().Select(n=>n!.GetValue<string>()).ToArray());break;
                        case "file_revision":result=new() { ["revision"]=WatchRevision(rootId)+":"+store.ConflictRevision(scope),["pollSeconds"]=30,["fallbackSeconds"]=900 };break;
                        case "file_conflict":result=store.ReportConflict(scope,root,a);break;
                        case "file_conflict_choose":result=store.ChooseConflict(scope,root,a["id"]!.GetValue<string>(),a["choice"]!.GetValue<string>());break;
                        case "file_conflict_clear":result=store.ClearConvergedConflict(scope,root,a);break;
                        case "file_schedule":result=store.Schedule(scope,a);break;
                        case "file_rows":result=store.Baselines(scope,a["offset"]?.GetValue<int>()??0,a["revision"]!.GetValue<string>());break;
                        case "file_baseline":result=store.BaselineRow(scope,a["path"]!.GetValue<string>());break;
                        case "file_probe":store.PathAllowed(scope,a["path"]!.GetValue<string>());result=await store.Probe(root,a["path"]!.GetValue<string>(),token,scope);break;
                        case "file_verify":result=await store.Verify(scope,root,a["path"]!.GetValue<string>(),a,token);break;
                        case "file_ack":if(!a.ContainsKey("remoteVersion"))throw new ArgumentException("Metadata acknowledgement required");result=await store.Ack(scope,root,a,token,Guard);break;
                        case "file_begin":if(!a.ContainsKey("expectedVersion"))throw new ArgumentException("Metadata precondition required");result=store.Begin(scope,a);break;
                        case "file_chunk":TransferBudget(auth.Device,Math.Min(262144,(a["data"]?.GetValue<string>().Length??0)*3/4));result=await store.Chunk(scope,a,token);break;
                        case "file_abort":result=store.Abort(scope,a["operation"]!.GetValue<string>());break;
                        case "file_read": {
                            var path=a["path"]!.GetValue<string>();store.PathAllowed(scope,path);
                            var offset=a["offset"]!.GetValue<long>();var size=a["size"]!.GetValue<long>();
                            if(offset<0 || size<0 || size>store.MaximumFileBytes || offset>size)throw new ArgumentException("Invalid file range");
                            await using var input=JeekMusic.Core.Sync.LanFiles.OpenRead(root,path);if(input.Length!=size || JeekMusic.Core.Sync.FileMetadata.Token(JeekMusic.Core.Sync.FileMetadata.Observe(root,path))!=a["sourceVersion"]?.GetValue<string>())throw new LanFileDeferred("source_changed");
                            input.Position=offset;var bytes=new byte[(int)Math.Min(262144,size-offset)];TransferBudget(auth.Device,bytes.Length);await input.ReadExactlyAsync(bytes,token);
                            Interlocked.Add(ref JeekMusic.Core.Sync.FileMetadata.TransferBytes,bytes.Length);result=new JsonObject { ["data"]=Convert.ToBase64String(bytes) };break;
                        }
                        case "file_upload_status":result=store.UploadStatus(scope,a["operation"]!.GetValue<string>());break;
                        case "file_finish":result=await store.Finish(scope,root,a["operation"]!.GetValue<string>(),token,MutationGuard);break;
                        case "file_delete":if(!a.ContainsKey("expectedVersion"))throw new ArgumentException("Metadata precondition required");result=await store.Delete(scope,root,a,token,MutationGuard);break;
                        default:throw new ArgumentException();
                    }
                    return result;
                }
                catch(LanFileConflict e) { return new JsonObject { ["conflict"]=true,["reason"]=e.Reason }; }
                catch(LanFileDeferred e) { _fileStore?.RecordDeferred();return new JsonObject { ["deferred"]=true,["reason"]=e.Reason }; }
                catch(OperationCanceledException) { _fileStore?.RecordDeferred();return new JsonObject { ["deferred"]=true,["reason"]="cancelled_or_time_budget" }; }
                catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
                { _fileStore?.RecordDeferred();return new JsonObject { ["deferred"]=true,["reason"]=e is UnauthorizedAccessException?"authorization_changed":"file_busy_unavailable_or_invalid" }; }
                finally { if(_fileStore!=null) { var diagnostics=_fileStore.Diagnostics();diagnostics["state"]="idle";diagnostics["lastAction"]=op;ConflictCount=_fileStore.PendingConflictCount;diagnostics["pendingConflicts"]=ConflictCount;_fileDiagnostics=diagnostics; }_fileWork.Release();if(op.StartsWith("file_conflict",StringComparison.Ordinal) || op=="file_ack")Notify();if(op is "file_open" or "file_finish" or "file_delete" or "file_ack")ScheduleFileRefresh(); }
            }
            if(!independent) { handedOff=true;using var ownedLifetime=lifetime;lifetime=null;return await Work(); }
            lock(_gate)
            {
                foreach(var key in _jobs.Where(j=>j.Value.Task.IsCompleted && j.Value.Device==auth.Device).Select(j=>j.Key).ToArray()) { RetireJob(_jobs[key]);_jobs.Remove(key); }
                if(_jobs.Count>=64 || _jobs.Values.Any(j=>j.Device==auth.Device && !j.Task.IsCompleted))throw Busy();
                var id=Stamp();var job=new PlanJob(auth.Device,lifetime,op);lifetime=null;_jobs[id]=job;handedOff=true;job.Task=Task.Run(Work);return new JsonObject { ["pending"]=true,["job"]=id };
            }
        }
        catch { lifetime?.Dispose();if(!handedOff)_fileWork.Release();throw; }
    }
}
