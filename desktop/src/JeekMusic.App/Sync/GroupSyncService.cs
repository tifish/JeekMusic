using System.Text.Json.Nodes;
using Avalonia.Threading;
using JeekMusic.Core.Sync;

namespace JeekMusic.Sync;

/// <summary>One upstream per group. Business projection and protocol checkpoints share the library commit.</summary>
public sealed class GroupSyncService
{
    readonly AppServices app;
    readonly GroupJournal journal;
    readonly LanPeerStore peers = new();
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim network = new(1,1);
    CancellationTokenSource? runningNetwork;
    SyncFeature? feature;
    DispatcherTimer? timer;
    JsonObject info = new();
    volatile HashSet<string> members = new(StringComparer.Ordinal);
    IReadOnlyDictionary<string,string> memberNames = new Dictionary<string,string>();
    volatile bool active, coordinator, paused;
    string lastError = "";
    long lastSuccess;
    int projectedLibraryVersion=-1;
    public bool Active => active;
    public bool Coordinator => coordinator;
    public event Action? Changed;
    static string S(JsonNode? node) => node?.GetValue<string>() ?? "";
    internal static long Integer(JsonObject value,string key,long minimum=0)
    {
        if(value[key] is not JsonValue node)throw new GroupSyncException("invalid_contract");
        long number;if(node.TryGetValue<long>(out var wide))number=wide;else if(node.TryGetValue<int>(out var narrow))number=narrow;else throw new GroupSyncException("invalid_contract");
        if(number<minimum || number>GroupSyncContract.MaxInteger)throw new GroupSyncException("invalid_contract");
        return number;
    }
    public GroupSyncService(AppServices app)
    {
        this.app=app;
        journal=new(new GroupSqlRows(app.Db),()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Refresh();
        app.OnExit+=Stop;
    }
    internal void Bind(SyncFeature value)
    {
        feature=value;
        feature.ApplyPendingGroupRules();
        timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(30) };
        timer.Tick+=async (_,_)=>await Synchronize();
        timer.Start();
        if(active)Dispatcher.UIThread.Post(async ()=>await Synchronize());
    }
    void Stop() { timer?.Stop();lifetime.Cancel(); }
    void Refresh()
    {
        var names=app.LanSync?.DeviceNames()??new Dictionary<string,string>();
        lock(app.Db.Gate)
        {
            active=journal.Active;
            info=active?journal.Info():new();
            coordinator=active && S(info["role"])=="coordinator";
            paused=app.Db.GetValue("group_paused")=="1";
            var allowed=new HashSet<string>(StringComparer.Ordinal);
            if(coordinator)
            {
                using var command=app.Db.Command("SELECT key FROM sync_group_row WHERE kind=$kind AND json_extract(value,'$.active')=1");
                command.Parameters.AddWithValue("$kind","member:"+S(info["groupId"]));
                using var reader=command.ExecuteReader();while(reader.Read())allowed.Add(reader.GetString(0));
            }
            members=allowed;
            memberNames=names;
        }
    }
    // Called while LAN authorization is locked. Never acquire the database gate here.
    internal void RequireGroupAccess(string actor,string? operation)
    {
        if(!active || operation=="capabilities")return;
        if(operation=="state")throw new GroupSyncException("upgrade_required");
        if(!coordinator)throw new GroupSyncException("not_coordinator");
        if(paused && (operation?.StartsWith("group_",StringComparison.Ordinal)==true && operation!="group_info" || operation=="preference_delta"))throw new GroupSyncException("group_paused");
        if(operation is "group_info" or "group_join" or "group_recover")return;
        if(!members.Contains(actor))throw new GroupSyncException("member_not_active");
    }
    public JsonObject Status()
    {
        lock(app.Db.Gate)
        {
            var result=active?journal.Info():new JsonObject { ["joined"]=false };
            result["joined"]=active;result["paused"]=paused;result["lastError"]=lastError;result["lastSuccess"]=lastSuccess;
            if(coordinator)
            {
                using var cmd=app.Db.Command("SELECT key,value FROM sync_group_row WHERE kind=$kind ORDER BY key");
                cmd.Parameters.AddWithValue("$kind","member:"+S(info["groupId"]));
                using var reader=cmd.ExecuteReader();var array=new JsonArray();
                while(reader.Read()) { var item=JsonNode.Parse(reader.GetString(1))!.AsObject();var actor=reader.GetString(0);item["actorId"]=actor;item["name"]=memberNames.GetValueOrDefault(actor,actor==S(info["actorId"])?Environment.MachineName:actor);array.Add((JsonNode)item); }
                result["memberDetails"]=array;
            }
            return result;
        }
    }
    T Frame<T>(Func<T> operation,Action<Action>? authorization=null)
    {
        Dispatcher.UIThread.VerifyAccess();
        var bridge=feature??throw new GroupSyncException("sync_not_ready");
        bridge.ApplyPendingGroupRules();
        var context=bridge.PrepareGroupContext(Bindings());
        var libraryVersion=app.Library.Snapshot.Version;
        T result=default!;bool changed=false;
        void Commit()=>app.Db.InTransaction(()=>
        {
            var existed=journal.Active;
            if(journal.Active)
            {
                changed=bridge.NormalizeGroupLocal(context);
                journal.Capture(bridge.GroupProjection(context));
            }
            result=operation();
            if(!journal.Active)return;
            if(!existed)journal.Capture(bridge.GroupProjection(context));
            if(projectedLibraryVersion!=libraryVersion)journal.ReapplyCaches();
            while(true)
            {
                var apply=journal.PendingApply();if(apply.Count==0)break;
                bridge.ApplyGroupRegisters(context,apply);
                journal.Projected(bridge.GroupProjection(context),apply);changed=true;
            }
            var current=journal.Info();
            if(S(current["role"])=="coordinator")journal.Acknowledge(S(current["actorId"]),Integer(current,"generation",1),Integer(current,"head"));
        });
        if(authorization is null)Commit();else authorization(Commit);
        projectedLibraryVersion=libraryVersion;
        Refresh();bridge.ApplyPendingGroupRules();
        if(changed)bridge.GroupProjectionChanged();
        Changed?.Invoke();return result;
    }
    internal IReadOnlyDictionary<string,string> Bindings()
    {
        var text=app.Db.GetValue("group_collection_bindings");
        return text is null?new Dictionary<string,string>():JsonNode.Parse(text)!.AsObject().ToDictionary(p=>p.Key,p=>S(p.Value));
    }
    public JsonObject Create()
    {
        return Frame(()=>
        {
            journal.Create(Guid.NewGuid().ToString("N"),feature!.DeviceId,Guid.NewGuid().ToString("N"));
            app.Db.SetValue("legacy_directory_sync","0");
            return journal.Info();
        });
    }
    public void CaptureLocal() { if(active)Frame(()=>true); }
    public JsonObject Pause(bool value) => Frame(()=> { app.Db.SetValue("group_paused",value?"1":"0");if(value)runningNetwork?.Cancel();return new JsonObject { ["paused"]=value }; });
    public JsonObject RemoveMember(string actor) => Frame(()=> { journal.RemoveMember(actor);return journal.Info(); });
    public JsonObject AllowRecovery(string actor)=>Frame(()=> { journal.AllowRecovery(actor);return journal.Info(); });
    public JsonObject Compact()=>Frame(()=>new JsonObject { ["compactedThrough"]=journal.Compact() });
    internal void Revoke(string actor)
    {
        if(!coordinator || !members.Contains(actor))return;
        var copy=new HashSet<string>(members,StringComparer.Ordinal);copy.Remove(actor);members=copy;
        Dispatcher.UIThread.Post(()=> { if(coordinator && !lifetime.IsCancellationRequested)RemoveMember(actor); });
    }
    public JsonArray Peers()=>peers.List();
    public async Task<JsonObject> Pair(string invitation,CancellationToken cancellation=default)
    {
        var parsed=LanPeerStore.Invitation(invitation,feature?.DeviceId??throw new GroupSyncException("sync_not_ready"));
        await using var client=new LanPeerClient(parsed.Connection);
        var response=await client.Call("pair",new() { ["invitation"]=parsed.Invitation,["name"]=Environment.MachineName },cancellation);
        var token=S(response["token"]);if(token.Length!=64 || token.Any(c=>!char.IsAsciiHexDigit(c)))throw new GroupSyncException("invalid_contract");
        parsed.Connection["token"]=token;parsed.Connection["name"]=S(response["name"]);
        var peer=peers.SavePaired(parsed.Connection);
        return new() { ["peerId"]=peer,["name"]=S(response["name"]) };
    }
    public void Forget(string peer)
    {
        if(active && S(info["peerId"])==peer)throw new GroupSyncException("active_group_peer");
        peers.Forget(peer);
    }
    public async Task<JsonObject> Join(string peer,CancellationToken cancellation=default)
    {
        if(active)throw new GroupSyncException("group_already_joined");
        var connection=peers.Connection(peer);
        var incarnation=S(connection["pendingGroupIncarnation"]);
        if(incarnation.Length==0) { incarnation=Guid.NewGuid().ToString("N");connection["pendingGroupIncarnation"]=incarnation;peers.Save(peer,connection); }
        await using var client=new LanPeerClient(connection);
        var server=await client.Call("group_join",new() { ["syncGroupProtocol"]=1,["incarnation"]=incarnation },cancellation);
        return await Dispatcher.UIThread.InvokeAsync(()=>Frame(()=>
        {
            journal.JoinClient(server,feature!.DeviceId,incarnation,peer);
            app.Db.SetValue("legacy_directory_sync","0");
            return journal.Info();
        }));
    }
    public async Task<JsonObject> Recover(CancellationToken cancellation=default)
    {
        if(!active || coordinator)throw new GroupSyncException("not_member_replica");
        await Dispatcher.UIThread.InvokeAsync(CaptureLocal);
        var local=Status();var peer=S(local["peerId"]);var connection=peers.Connection(peer);
        var incarnation=S(connection["pendingRecoveryIncarnation"]);
        if(incarnation.Length==0) { incarnation=Guid.NewGuid().ToString("N");connection["pendingRecoveryIncarnation"]=incarnation;peers.Save(peer,connection); }
        await using var client=new LanPeerClient(connection);
        var remote=await client.Call("group_recover",new() { ["syncGroupProtocol"]=1,["groupId"]=S(local["groupId"]),["incarnation"]=incarnation },cancellation);
        var result=await Dispatcher.UIThread.InvokeAsync(()=>Frame(()=> { journal.RecoverClient(remote,incarnation);return journal.Info(); }));
        connection.Remove("pendingRecoveryIncarnation");peers.Save(peer,connection);return result;
    }
    internal JsonObject Handle(string operation,string actor,JsonObject request,Action<Action> authorization)
    {
        if(Integer(request,"syncGroupProtocol",1)!=1)throw new GroupSyncException("upgrade_required");
        if(!coordinator)throw new GroupSyncException("not_coordinator");
        var bindings=Bindings();var aliases=new JsonObject();
        foreach(var root in app.LanSync.Diagnostics()["roots"]!.AsArray().OfType<JsonObject>())
            if(root["revoked"]?.GetValue<bool>()!=true && root["rootId"] is { } id)aliases[id.ToString()]=bindings.GetValueOrDefault(id.ToString(),id.ToString());
        JsonObject Describe(JsonObject value) { value["rootAliases"]=aliases.DeepClone();return value; }
        return Frame(()=>
        {
            if(operation=="group_join")return Describe(journal.JoinMember(actor,S(request["incarnation"])));
            if(operation=="group_recover")
            {
                if(S(request["groupId"])!=S(journal.Info()["groupId"]))throw new GroupSyncException("group_mismatch");
                return Describe(journal.RecoverMember(actor,S(request["incarnation"])));
            }
            journal.RequireMember(actor,S(request["incarnation"]));
            return operation switch
            {
                "group_info"=>Describe(journal.Info()),
                "group_push"=>journal.Push(actor,S(request["incarnation"]),(request["mutations"] as JsonArray??throw new GroupSyncException("invalid_contract")).Select(n=>GroupSyncContract.ReadMutation(n as JsonObject??throw new GroupSyncException("invalid_contract"))).ToArray()),
                "group_pull"=>journal.Pull(S(request["groupId"]),Integer(request,"generation",1),Integer(request,"cursor"),request.ContainsKey("through")?Integer(request,"through"):null),
                "group_snapshot"=>journal.Snapshot(actor,S(request["snapshotId"])),
                "group_snapshot_rows"=>journal.SnapshotPage(actor,S(request["snapshotId"]),Integer(request,"after")),
                "group_ack"=>Acknowledge(),
                _=>throw new GroupSyncException("unsupported_group_operation")
            };
            JsonObject Acknowledge() { journal.Acknowledge(actor,Integer(request,"generation",1),Integer(request,"cursor"));return journal.Info(); }
        },authorization);
    }
    public async Task<JsonObject> Synchronize(CancellationToken cancellation=default)
    {
        if(!active || paused)return Status();
        if(!await network.WaitAsync(0,cancellation))return Status();
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation);
        runningNetwork=linked;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(CaptureLocal);
            if(coordinator)return Status();
            var local=await Dispatcher.UIThread.InvokeAsync(Status);
            await using var client=new LanPeerClient(peers.Connection(S(local["peerId"])));
            JsonObject Args()=>new() { ["syncGroupProtocol"]=1,["incarnation"]=S(local["incarnation"]),["groupId"]=S(local["groupId"]) };
            async Task<JsonObject> Call(string operation,JsonObject? extra=null)
            {
                var arguments=Args();if(extra is not null)foreach(var pair in extra)arguments[pair.Key]=pair.Value?.DeepClone();
                return await client.Call(operation,arguments,linked.Token);
            }
            var remote=await Call("group_info");
            if(S(remote["groupId"])!=S(local["groupId"]) || S(remote["coordinatorDeviceId"])!=S(local["coordinatorDeviceId"]))throw new GroupSyncException("group_mismatch");
            await Dispatcher.UIThread.InvokeAsync(()=>Frame(()=> { journal.ResetGeneration(Integer(remote,"generation",1));return true; }));
            for(int batch=0;batch<100;batch++)
            {
                var outbox=await Dispatcher.UIThread.InvokeAsync(()=> { lock(app.Db.Gate)return journal.Outbox(); });
                if(outbox.Count==0)break;
                var response=await Call("group_push",new() { ["mutations"]=new JsonArray(outbox.Select(m=>(JsonNode)GroupSyncContract.WriteMutation(m)).ToArray()) });
                await Dispatcher.UIThread.InvokeAsync(()=>Frame(()=> { journal.AcceptReceipts(response["receipts"]!.AsArray());return true; }));
            }
            var progress=await Dispatcher.UIThread.InvokeAsync(()=> { lock(app.Db.Gate)return journal.SnapshotProgress(); });
            if(progress["required"]!.GetValue<bool>())
            {
                // A fresh fixed snapshot also recovers an expired page lease without discarding the outbox.
                await Dispatcher.UIThread.InvokeAsync(()=>Frame(()=> { journal.RestartSnapshot();return true; }));
                var snapshot=Guid.NewGuid().ToString("N");
                var page=await Call("group_snapshot",new() { ["snapshotId"]=snapshot });
                while(true)
                {
                    await Dispatcher.UIThread.InvokeAsync(()=>Frame(()=> { journal.ReceiveSnapshot(page);return true; }));
                    if(!page["hasMore"]!.GetValue<bool>())break;
                    page=await Call("group_snapshot_rows",new() { ["snapshotId"]=snapshot,["after"]=Integer(page,"next") });
                }
            }
            long? through=null;
            for(int batch=0;batch<100;batch++)
            {
                local=await Dispatcher.UIThread.InvokeAsync(Status);
                var args=new JsonObject { ["generation"]=Integer(local,"generation",1),["cursor"]=Integer(local,"cursor") };
                if(through is not null)args["through"]=through.Value;
                JsonObject page;
                try { page=await Call("group_pull",args); }
                catch(LanPeerException e) when(e.Code=="reset_required")
                {
                    await Dispatcher.UIThread.InvokeAsync(()=>Frame(()=> { journal.RestartSnapshot();return true; }));throw;
                }
                through=Integer(page,"head");
                await Dispatcher.UIThread.InvokeAsync(()=>Frame(()=> { journal.Receive(page);return true; }));
                if(!page["hasMore"]!.GetValue<bool>())break;
            }
            local=await Dispatcher.UIThread.InvokeAsync(Status);
            await Call("group_ack",new() { ["generation"]=Integer(local,"generation",1),["cursor"]=Integer(local,"cursor") });
            await SynchronizeLearning(client,S(local["peerId"]),linked.Token);
            lastError="";lastSuccess=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        catch(Exception e) when(e is IOException or ArgumentException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            lastError=e is LanPeerException peer?peer.Code:e is GroupSyncException group?group.Code:e is OperationCanceledException?"cancelled":"connection_failed";
        }
        finally { runningNetwork=null;network.Release(); }
        Changed?.Invoke();return Status();
    }
    async Task SynchronizeLearning(LanPeerClient client,string peerId,CancellationToken cancellation)
    {
        var service=app.Preferences;var store=service.Store;var peer="group:"+peerId;
        var upload=long.TryParse(store.Local("upload:"+peer),out var stored)?stored:0;
        for(int batch=0;batch<100;batch++)
        {
            var page=store.Page(upload);
            var reply=await client.Call("preference_delta",new() { ["protocol"]=3,["sender"]=store.DeviceId,["page"]=page,["after"]=store.Cursor(peer) },cancellation);
            // Reuse reset-boundary cleanup and model invalidation; imported events keep their original identity.
            service.Delta(new() { ["protocol"]=3,["sender"]=peer,["page"]=reply,["after"]=0L });
            upload=Integer(reply,"acceptedCursor");store.SetLocal("upload:"+peer,upload.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if(reply["hasMore"]?.GetValue<bool>()!=true && page["hasMore"]?.GetValue<bool>()!=true)break;
        }
    }
}
