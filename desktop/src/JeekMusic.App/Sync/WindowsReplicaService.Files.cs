using System.Text.Json.Nodes;
using Avalonia.Threading;
using JeekMusic.Core.Sync;

namespace JeekMusic.Sync;

public sealed partial class WindowsReplicaService
{
    static JsonObject Absent()=>new() { ["known"]=true,["exists"]=false,["metadataVersion"]=null };
    static FileReconciliation.State State(JsonObject row)=>new(row["known"]?.GetValue<bool>()==true,FileMetadata.Token(row),N(row["editedAtMs"]));
    static FileReconciliation.Baseline? Baseline(JsonObject? row)=>S(row?["version"]) is { Length:>0 } version?new(row?["localVersion"]?.GetValue<string>(),version,row?["remoteVersion"]?.GetValue<string>(),true,S(row?["migration"])=="legacy_metadata_unconfirmed"):null;
    async Task Round(JsonObject mapping,CancellationToken token)
    {
        var id=S(mapping["mappingId"]);var group=app.GroupSync.Status();
        if(S(group["groupId"])!=S(mapping["groupId"]) || S(group["peerId"])!=S(mapping["peerId"]))throw new LanFileDeferred("mapping_group_changed");
        var collection=app.GroupSync.RequireCollection(S(mapping["collectionId"]));
        if(S(collection["rootId"])!=S(mapping["rootId"]))throw new LanFileDeferred("collection_root_changed");
        var connection=app.GroupSync.Upstream();
        if(Digest(S(connection["pin"])+":"+S(connection["token"]))!=S(mapping["credentialIdentity"]))throw new LanFileDeferred("mapping_credential_changed");
        var folder=app.Db.Folders().SingleOrDefault(f=>f.Id==N(mapping["folderId"]) && f.Identity==S(mapping["folderIdentity"]));
        if(folder is not { Available:true } || !folder.Root.Equals(S(mapping["localRoot"]),StringComparison.OrdinalIgnoreCase))throw new LanFileDeferred("local_directory_unavailable");
        LanFiles.Resolve(folder.Root,"",true);
        var pin=LanFileMutation.PinRoot(folder.Root,S(mapping["nativeIdentity"]));using var pinned=pin.Handle;
        files??=new(Path.Combine(directory,"target"));
        await using var client=new LanPeerClient(connection);
        var scope=S((await client.Operation("file_open",new() { ["fileProtocol"]=3,["rootId"]=S(mapping["rootId"]),["mapping"]=id,["selection"]=new JsonArray("") },token))["scope"]);
        var localScope=S(files.Open(S(group["coordinatorDeviceId"]),id,S(mapping["localRootId"]),id,[""])["scope"]);
        if(S(mapping["scope"]) is { Length:>0 } oldScope && oldScope!=scope || S(mapping["localScope"]) is { Length:>0 } oldLocal && oldLocal!=localScope)throw new LanFileDeferred("mapping_scope_changed");
        mapping["scope"]=scope;mapping["localScope"]=localScope;
        Update(id,row=> { row["scope"]=scope;row["localScope"]=localScope;row["state"]="scanning"; });
        async Task<JsonObject> Call(string op,JsonObject? args=null)
        {
            args??=new();args["fileProtocol"]=3;args["scope"]=scope;return await client.Operation(op,args,token);
        }
        // A failed scan, including inaccessible ancestors, stops the round before absence is used.
        var local=await LanFiles.Manifest(folder.Root,[""],token,metadataOnly:true,cueCacheDirectory:Path.Combine(directory,"cue-cache"));
        var plan=await client.Operation("plan",new() { ["fileProtocol"]=3,["rootId"]=S(mapping["rootId"]),["selection"]=new JsonArray("") },token);
        var delta=await Call("file_delta",new() { ["plan"]=S(plan["plan"]),["cursor"]=S(mapping["cursor"]) });
        var cursor=S(delta["cursor"]);bool reset=delta["reset"]!.GetValue<bool>();
        store.Clear("delta:"+id);
        while(true)
        {
            foreach(var row in delta["rows"]!.AsArray().OfType<JsonObject>()) { var path=S(row["path"]);LanFiles.Relative(path);store.Put("delta:"+id,path,row); }
            if(delta["hasMore"]?.GetValue<bool>()!=true)break;
            delta=await Call("file_delta_rows",new() { ["cursor"]=cursor,["offset"]=N(delta["next"]) });
        }
        if(reset)store.Clear("remote:"+id);
        foreach(var row in Rows("delta:"+id))store.Put("remote:"+id,row.Key,row.Value);
        store.Clear("delta:"+id);
        var paths=new HashSet<string>(local.Select(r=>r.Path),StringComparer.Ordinal);
        foreach(var kind in new[] { "remote:","pending:","conflict:" })foreach(var row in Rows(kind+id))paths.Add(row.Key);
        if(paths.Count>200000)throw new LanFileDeferred("client_snapshot_budget");
        LanFiles.CheckCollisions(paths);
        var failed=false;long changed=0;long nextProgress=0;
        // Audio and other dependencies land before CUE descriptors.
        var localByPath=local.ToDictionary(e=>e.Path,StringComparer.Ordinal);
        int Order(string path)
        {
            if(!path.EndsWith(".cue",StringComparison.OrdinalIgnoreCase))return 1;
            var row=store.Read("remote:"+id,path);
            return S(row?["baseline"]?["version"]).Length>0 && (!localByPath.ContainsKey(path) || row?["remote"] is null)?0:2;
        }
        foreach(var path in paths.OrderBy(Order).ThenBy(p=>p,StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if(Environment.TickCount64>=nextProgress) { Update(id,row=> { row["state"]="reconciling";row["currentPath"]=path; });nextProgress=Environment.TickCount64+250; }
            try
            {
                if(await Reconcile(mapping,folder.Root,path,localByPath,Call,token))changed++;
            }
            catch(Exception exception) when(exception is LanFileConflict or LanFileDeferred)
            {
                failed=true;Update(id,row=>row["lastError"]=exception is LanFileConflict conflict?conflict.Reason:((LanFileDeferred)exception).Reason);
            }
        }
        var refresh=files.PendingRefresh().Where(r=>r.Root==S(mapping["localRootId"])).ToArray();
        if(refresh.Length>0 && await app.Library.RefreshSyncedFilesAsync(N(mapping["folderId"]),refresh.Select(r=>r.Path).Distinct().ToArray()))files.Refreshed(refresh.Select(r=>r.Id).ToArray());
        Update(id,row=> { row["lastActions"]=changed;row["currentPath"]=""; });
        if(failed || store.Count("pending:"+id)>0 || store.Count("conflict:"+id)>0)throw new LanFileDeferred("unconfirmed_files");
        token.ThrowIfCancellationRequested();await Call("file_delta_commit",new() { ["cursor"]=cursor });
        Update(id,row=>row["cursor"]=cursor);
    }
    async Task<bool> Reconcile(JsonObject mapping,string root,string path,IReadOnlyDictionary<string,LanFiles.Entry> localManifest,Func<string,JsonObject?,Task<JsonObject>> call,CancellationToken token)
    {
        var id=S(mapping["mappingId"]);var localScope=S(mapping["localScope"]);
        var local=FileMetadata.Observe(root,path,true);
        var cached=store.Read("remote:"+id,path);
        var remote=cached?["remote"]?.DeepClone().AsObject()??Absent();
        var baseline=cached?["baseline"]?.DeepClone().AsObject();
        var pending=store.Read("pending:"+id,path);
        if(pending is not null)
        {
            await Continue(mapping,root,path,pending,call,token);
            local=FileMetadata.Observe(root,path,true);remote=await call("file_probe",new() { ["path"]=path });baseline=await call("file_baseline",new() { ["path"]=path });
        }
        JsonObject Effective(JsonObject? value)
        {
            var landing=store.Read("landing:"+id,path);var effective=value?.DeepClone().AsObject()??new JsonObject();
            if(landing is not null && S(landing["baselineVersion"])==S(effective["version"]))
            { effective["localVersion"]=landing["landedLocal"]?.DeepClone();effective["remoteVersion"]=landing["landedRemote"]?.DeepClone();effective["comparisonProtocol"]=2;if(S(effective["version"]).Length==0)effective["version"]="landing"; }
            return effective;
        }
        var decision=FileReconciliation.Decide(Baseline(Effective(baseline)),State(local),State(remote),newestWins:true);
        if(decision.Action=="none" && store.Read("landing:"+id,path) is not null)decision=new("ack");
        if(decision.Action=="none") { store.Delete("conflict:"+id,path);return pending is not null; }
        // Recheck both files immediately before creating the immutable transaction intent.
        local=FileMetadata.Observe(root,path,true);remote=await call("file_probe",new() { ["path"]=path });baseline=await call("file_baseline",new() { ["path"]=path });
        decision=FileReconciliation.Decide(Baseline(Effective(baseline)),State(local),State(remote),newestWins:true);
        if(decision.Action=="none" && store.Read("landing:"+id,path) is not null)decision=new("ack");
        var issue=store.Read("conflict:"+id,path);
        bool resolve=decision.Reason is "newer_file_mtime" or "newer_edit_time" || store.Read("landing:"+id,path) is not null;
        if(decision.Action=="conflict")
        {
            if(issue is not null && issue["choice"] is not null && JsonNode.DeepEquals(issue["localVersion"],local["metadataVersion"]) && JsonNode.DeepEquals(issue["remoteVersion"],remote["metadataVersion"]) && S(issue["baselineVersion"])==S(baseline["version"]))
            {
                var keepLocal=S(issue["choice"])==S(mapping["localRootId"]);
                decision=new(keepLocal?(FileMetadata.Token(local) is null?"delete_remote":"upload"):(FileMetadata.Token(remote) is null?"delete_local":"download"));resolve=true;
            }
            else
            {
                store.Put("conflict:"+id,path,new() { ["id"]=Digest(id+"\n"+path+"\n"+S(baseline["version"])+"\n"+FileMetadata.Token(local)+"\n"+FileMetadata.Token(remote)),["mappingId"]=id,["path"]=path,["localReplicaId"]=S(mapping["localRootId"]),["remoteReplicaId"]=S(mapping["rootId"]),["localDeviceId"]=app.GroupSync.Status()["actorId"]!.DeepClone(),["remoteDeviceId"]=app.GroupSync.Status()["coordinatorDeviceId"]!.DeepClone(),["localVersion"]=local["metadataVersion"]?.DeepClone(),["remoteVersion"]=remote["metadataVersion"]?.DeepClone(),["baselineVersion"]=S(baseline["version"]),["reason"]=decision.Reason });
                throw new LanFileConflict(decision.Reason??"both_modified");
            }
        }
        if(decision.Action=="none")return false;
        if(decision.Action=="defer")throw new LanFileDeferred(decision.Reason??"state_unknown");
        if(decision.Action=="delete_local" && localManifest.Values.Any(e=>e.Dependencies?.Contains(path)==true && FileMetadata.Token(FileMetadata.Observe(root,e.Path)) is not null))throw new LanFileDeferred("cue_reference_before_deletion");
        if(decision.Action=="delete_remote" && Rows("remote:"+id).Any(r=>r.Value["remote"]?["exists"]?.GetValue<bool>()==true && r.Value["remote"]?["dependencies"] is JsonArray deps && deps.Any(d=>S(d)==path)))throw new LanFileDeferred("cue_reference_before_deletion");
        var localBaseline=files!.BaselineRow(localScope,path);
        pending=new() { ["operation"]=Id(),["ackOperation"]=Id(),["localAckOperation"]=Id(),["kind"]=decision.Action,["path"]=path,["state"]="created",["localBefore"]=local.DeepClone(),["remoteBefore"]=remote.DeepClone(),["baselineVersion"]=S(baseline["version"]),["localBaselineVersion"]=S(localBaseline["version"]),["resolve"]=resolve };
        var dependencies=decision.Action=="upload"?localManifest.GetValueOrDefault(path)?.Dependencies??[]:(cached?["remote"]?["dependencies"] as JsonArray)?.Select(d=>S(d)).ToArray()??[];
        pending["dependencies"]=new JsonArray(dependencies.Select(d=>(JsonNode?)JsonValue.Create(d)).ToArray());
        store.Put("pending:"+id,path,pending);
        await Continue(mapping,root,path,pending,call,token);store.Delete("conflict:"+id,path);return true;
    }
    async Task CheckCueDependencies(string root,JsonObject pending,Func<string,JsonObject?,Task<JsonObject>> call)
    {
        if(!S(pending["path"]).EndsWith(".cue",StringComparison.OrdinalIgnoreCase))return;
        foreach(var item in pending["dependencies"]!.AsArray())
        {
            var dependency=LanFiles.Relative(S(item));var local=FileMetadata.Observe(root,dependency);var remote=await call("file_probe",new() { ["path"]=dependency });
            var baseline=await call("file_baseline",new() { ["path"]=dependency });
            if(FileMetadata.Token(local) is null || FileMetadata.Token(remote) is null || FileReconciliation.Decide(Baseline(baseline),State(local),State(remote),true).Action!="none")throw new LanFileDeferred("cue_dependency_not_converged");
        }
    }
    void Guard(JsonObject mapping,string root,string path,CancellationToken token,Action action)
    {
        token.ThrowIfCancellationRequested();
        var pin=LanFileMutation.PinRoot(root,S(mapping["nativeIdentity"]));using var handle=pin.Handle;
        lock(controlGate)
        {
            if(Mapping(S(mapping["mappingId"]))["paused"]?.GetValue<bool>()==true || app.GroupSync.Status()["paused"]?.GetValue<bool>()==true)throw new LanFileDeferred("paused");
            lock(Online.TagWriteCommit.Gate)
            {
                token.ThrowIfCancellationRequested();if(app.Player.IsPathInUse(Path.Combine(root,path.Replace('/',Path.DirectorySeparatorChar))))throw new LanFileDeferred("playing_file");action();
            }
        }
    }
    async Task Continue(JsonObject mapping,string root,string path,JsonObject pending,Func<string,JsonObject?,Task<JsonObject>> call,CancellationToken token)
    {
        var id=S(mapping["mappingId"]);var kind=S(pending["kind"]);var op=S(pending["operation"]);var scope=S(mapping["localScope"]);
        var local=pending["localBefore"]!.AsObject();var remote=pending["remoteBefore"]!.AsObject();
        void Save(string state) { pending["state"]=state;store.Put("pending:"+id,path,pending); }
        var source=kind=="upload"?local:remote;var target=kind=="upload"?remote:local;
        void Retire(string reason)
        {
            CleanupCapture(pending);pending["retiredReason"]=reason;store.Put("history:"+id,op,pending);store.Delete("pending:"+id,path);
        }
        JsonObject Arguments()=>new() { ["path"]=path,["operation"]=op,["stamp"]=source["metadataVersion"]?.DeepClone(),["expectedStamp"]=target["metadataVersion"]?.DeepClone(),["expectedVersion"]=target["metadataVersion"]?.DeepClone(),["baselineVersion"]=S(pending[kind is "upload" or "delete_remote"?"baselineVersion":"localBaselineVersion"]),["size"]=N(source["size"]),["modifiedNs"]=N(source["modifiedNs"]),["editedAtMs"]=N(source["editedAtMs"]),["resolve"]=pending["resolve"]!.DeepClone(),["deviceId"]=S(mapping["peerId"]) };
        if(S(pending["state"])!="landed" && S(pending["state"])!="remote_acknowledged")
        {
            JsonObject result;
            if(kind=="upload")
            {
                var status=await call("file_upload_status",new() { ["operation"]=op });
                if(status["committed"]?.GetValue<bool>()==true)result=status;
                else
                {
                    if(S(status["state"])=="prepared") { await CheckCueDependencies(root,pending,call);result=await call("file_finish",new() { ["operation"]=op }); }
                    else
                    {
                        var currentTarget=await call("file_probe",new() { ["path"]=path });
                        if(S(status["state"])=="conflict" || FileMetadata.Token(currentTarget)!=FileMetadata.Token(target))
                        {
                            if(S(status["state"])!="not_found")await call("file_abort",new() { ["operation"]=op });
                            Retire("target_changed_before_commit");throw new LanFileDeferred("target_changed_before_commit");
                        }
                        if(FileMetadata.Token(FileMetadata.Observe(root,path))!=FileMetadata.Token(local) && S(pending["captureStamp"]).Length==0)
                        {
                            if(S(status["state"])!="not_found")await call("file_abort",new() { ["operation"]=op });
                            CleanupCapture(pending);store.Delete("pending:"+id,path);throw new LanFileDeferred("source_changed_before_capture");
                        }
                        try { await CaptureSource(mapping,root,path,pending,token);Save("captured"); }
                        catch(LanFileDeferred error) when(error.Reason=="capture_changed")
                        {
                            if(S(status["state"])!="not_found")await call("file_abort",new() { ["operation"]=op });
                            Retire("capture_changed");throw;
                        }
                        status=await call("file_begin",Arguments());
                        if(status["committed"]?.GetValue<bool>()==true)result=status;
                        else
                        {
                            await using var input=OpenCapture(pending);long offset=N(status["offset"]);
                            if(offset<0 || offset>input.Length)throw new LanFileDeferred("invalid_upload_prefix");input.Position=offset;
                            var buffer=new byte[262144];while(input.Position<input.Length)
                            {
                                var count=await input.ReadAsync(buffer,token);await call("file_chunk",new() { ["operation"]=op,["offset"]=offset,["data"]=Convert.ToBase64String(buffer,0,count) });offset+=count;
                            }
                            await CheckCueDependencies(root,pending,call);result=await call("file_finish",new() { ["operation"]=op });
                        }
                    }
                }
                pending["landedLocal"]=local["metadataVersion"]?.DeepClone();pending["landedRemote"]=result["resultVersion"]?.DeepClone();
            }
            else if(kind=="download")
            {
                var status=files!.UploadStatus(scope,op);
                if(status["committed"]?.GetValue<bool>()==true)result=status;
                else
                {
                    if(S(status["state"])!="prepared")
                    {
                        if(S(status["state"])=="conflict" || FileMetadata.Token(FileMetadata.Observe(root,path))!=FileMetadata.Token(target))
                        {
                            if(S(status["state"])!="not_found")files.Abort(scope,op);
                            Retire("target_changed_before_commit");throw new LanFileDeferred("target_changed_before_commit");
                        }
                        var current=await call("file_probe",new() { ["path"]=path });
                        if(FileMetadata.Token(current)!=FileMetadata.Token(source))
                        {
                            if(S(status["state"])!="not_found")files.Abort(scope,op);
                            store.Delete("pending:"+id,path);throw new LanFileDeferred("source_changed_before_download");
                        }
                        status=files.Begin(scope,Arguments());var offset=N(status["offset"]);var size=N(source["size"]);
                        while(offset<size)
                        {
                            var block=await call("file_read",new() { ["path"]=path,["size"]=size,["offset"]=offset,["sourceVersion"]=source["metadataVersion"]!.DeepClone(),["count"]=262144 });
                            var bytes=Convert.FromBase64String(S(block["data"]));if(bytes.Length==0 || bytes.Length>Math.Min(262144,size-offset))throw new IOException("Invalid file block");
                            await files.Chunk(scope,new() { ["operation"]=op,["offset"]=offset,["data"]=block["data"]!.DeepClone() },token);offset+=bytes.Length;
                        }
                    }
                    await CheckCueDependencies(root,pending,call);result=await files.Finish(scope,root,op,token,action=>Guard(mapping,root,path,token,action));
                }
                pending["landedLocal"]=result["resultVersion"]?.DeepClone();pending["landedRemote"]=remote["metadataVersion"]?.DeepClone();
            }
            else if(kind is "delete_local" or "delete_remote")
            {
                var args=Arguments();args["stamp"]=null;
                // Deletion source is absence; its exact target remains the precondition.
                args["expectedStamp"]=(kind=="delete_local"?local:remote)["metadataVersion"]?.DeepClone();args["expectedVersion"]=args["expectedStamp"]?.DeepClone();
                if(kind=="delete_remote")await call("file_delete",args);
                else await files!.Delete(scope,root,args,token,action=>Guard(mapping,root,path,token,action));
                pending["landedLocal"]=null;pending["landedRemote"]=null;
            }
            else if(kind=="ack") { pending["landedLocal"]=local["metadataVersion"]?.DeepClone();pending["landedRemote"]=remote["metadataVersion"]?.DeepClone(); }
            else throw new IOException("Invalid pending action");
            Save("landed");
        }
        var stamp=kind is "upload" or "download"?source["metadataVersion"]?.DeepClone():null;
        JsonObject? baseline=null;
        if(S(pending["state"])!="remote_acknowledged")
        {
            var currentBaseline=await call("file_baseline",new() { ["path"]=path });
            var alreadyConfirmed=S(currentBaseline["version"]).Length>0 && JsonNode.DeepEquals(currentBaseline["localVersion"],pending["landedLocal"]) && JsonNode.DeepEquals(currentBaseline["remoteVersion"],pending["landedRemote"]);
            var currentRemote=await call("file_probe",new() { ["path"]=path });
            if(!alreadyConfirmed && !JsonNode.DeepEquals(currentRemote["metadataVersion"],pending["landedRemote"]))
            {
                // The upstream changed after landing but before its ACK. Preserve the proven
                // landing as three-way history; a new conditional transaction reconciles from it.
                files!.ConfirmReplicaBaseline(scope,path,new() { ["remoteVersion"]=pending["landedLocal"]?.DeepClone(),["localVersion"]=pending["landedRemote"]?.DeepClone(),["operation"]=S(pending["localAckOperation"]),["baselineVersion"]=S(pending["localBaselineVersion"]),["mutationOperation"]=kind is "download" or "delete_local"?op:"",["stamp"]=stamp?.DeepClone() });
                store.Put("landing:"+id,path,pending);CleanupCapture(pending);store.Put("history:"+id,op,pending);store.Delete("pending:"+id,path);
                throw new LanFileDeferred("landing_changed_before_ack");
            }
            var args=new JsonObject { ["path"]=path,["remoteVersion"]=pending["landedRemote"]?.DeepClone(),["localVersion"]=pending["landedLocal"]?.DeepClone(),["localPrecisionNs"]=100,["localKnown"]=true,["baselineVersion"]=S(pending["baselineVersion"]),["operation"]=S(pending["ackOperation"]),["decision"]=kind,["stamp"]=stamp?.DeepClone() };
            if(kind is "upload" or "delete_remote")args["mutationOperation"]=op;
            baseline=alreadyConfirmed?currentBaseline:await call("file_ack",args);pending["confirmedBaseline"]=baseline.DeepClone();Save("remote_acknowledged");
        }
        files!.ConfirmReplicaBaseline(scope,path,new() { ["remoteVersion"]=pending["landedLocal"]?.DeepClone(),["localVersion"]=pending["landedRemote"]?.DeepClone(),["operation"]=S(pending["localAckOperation"]),["baselineVersion"]=S(pending["localBaselineVersion"]),["mutationOperation"]=kind is "download" or "delete_local"?op:"",["stamp"]=stamp?.DeepClone() });
        var observed=pending["remoteBefore"]!.DeepClone().AsObject();observed["metadataVersion"]=pending["landedRemote"]?.DeepClone();observed["known"]=true;observed["exists"]=pending["landedRemote"] is not null;
        if(pending["landedRemote"] is not null) { var parts=S(pending["landedRemote"]).Split(':');observed["size"]=long.Parse(parts[1],System.Globalization.CultureInfo.InvariantCulture);observed["modifiedNs"]=long.Parse(parts[2],System.Globalization.CultureInfo.InvariantCulture); }
        if(kind is "upload" or "download")observed["dependencies"]=pending["dependencies"]?.DeepClone()??new JsonArray();
        store.Put("remote:"+id,path,new() { ["path"]=path,["remote"]=observed,["baseline"]=pending["confirmedBaseline"]!.DeepClone() });
        CleanupCapture(pending);store.Delete("pending:"+id,path);store.Delete("landing:"+id,path);
    }
    FileStream OpenCapture(JsonObject pending)
    {
        var name=S(pending["capture"]);if(!Guid.TryParseExact(name,"N",out _))throw new LanFileDeferred("capture_missing");
        var input=LanFileMutation.OpenPrivate(Path.Combine(directory,name+".source"),false);
        try { if(LanFileMutation.Identity(input)!=S(pending["captureIdentity"]) || FileMetadata.Stamp(input.SafeFileHandle)!=S(pending["captureStamp"]))throw new LanFileDeferred("capture_changed");return input; }
        catch { input.Dispose();throw; }
    }
    async Task CaptureSource(JsonObject mapping,string root,string path,JsonObject pending,CancellationToken token)
    {
        if(S(pending["captureStamp"]).Length>0) { await using var existing=OpenCapture(pending);return; }
        var expected=FileMetadata.Token(pending["localBefore"]);var size=N(pending["localBefore"]?["size"]);
        if(size<0 || size>8L*1024*1024*1024)throw new LanFileDeferred("file_byte_budget");
        long reserved=0;int count=0;foreach(var file in Directory.EnumerateFiles(directory,"*.source")) { if(++count>4096)throw new LanFileDeferred("capture_count_budget");reserved=checked(reserved+new FileInfo(file).Length); }
        if(reserved+size>16L*1024*1024*1024)throw new LanFileDeferred("capture_byte_budget");
        CleanupCapture(pending);var name=Id();pending["capture"]=name;
        store.Put("pending:"+S(mapping["mappingId"]),path,pending);
        await using var source=LanFiles.OpenRead(root,path);
        if(FileMetadata.Stamp(source.SafeFileHandle)!=expected)throw new LanFileDeferred("source_changed_before_capture");
        await using var output=LanFileMutation.OpenPrivate(Path.Combine(directory,name+".source"));
        if(output.Length!=0)throw new LanFileDeferred("capture_changed");
        pending["captureIdentity"]=LanFileMutation.Identity(output);store.Put("pending:"+S(mapping["mappingId"]),path,pending);
        await source.CopyToAsync(output,262144,token);await output.FlushAsync(token);output.Flush(true);
        if(output.Length!=size || FileMetadata.Stamp(source.SafeFileHandle)!=expected)throw new LanFileDeferred("source_changed_during_capture");
        pending["captureStamp"]=FileMetadata.Stamp(output.SafeFileHandle);store.Put("pending:"+S(mapping["mappingId"]),path,pending);
    }
    void CleanupCapture(JsonObject pending)
    {
        var name=S(pending["capture"]);if(!Guid.TryParseExact(name,"N",out _) || S(pending["captureIdentity"]).Length==0)return;
        try { using var file=LanFileMutation.OpenPrivate(Path.Combine(directory,name+".source"),false);LanFileMutation.DeleteOwned(file,S(pending["captureIdentity"])); }
        catch(IOException) { /* Retained private bytes still count against admission. */ }
        pending.Remove("captureStamp");pending.Remove("captureIdentity");pending.Remove("capture");
    }
}
