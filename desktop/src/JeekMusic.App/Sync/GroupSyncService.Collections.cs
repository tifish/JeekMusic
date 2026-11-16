using System.Text.Json.Nodes;
using Avalonia.Threading;
using JeekMusic.Core.Sync;

namespace JeekMusic.Sync;

public sealed partial class GroupSyncService
{
    internal Microsoft.Win32.SafeHandles.SafeFileHandle? PinCollectionRoot(string rootId,string path)
    {
        if(!active)return null;
        if(!coordinator)throw new GroupSyncException("not_coordinator");
        JsonObject definition;lock(app.Db.Gate)definition=CollectionRows().Select(p=>p.Value).OfType<JsonObject>().SingleOrDefault(row=>S(row["rootId"])==rootId)?.DeepClone().AsObject()??throw new GroupSyncException("collection_unavailable");
        return LanFileMutation.PinRoot(path,S(definition["nativeIdentity"])).Handle;
    }
    JsonObject CollectionRows(bool remote=false)
        =>JsonNode.Parse(app.Db.GetValue(remote?"group_remote_collections":"group_collections")??"{}")!.AsObject();
    internal JsonArray CollectionDefinitions()
    {
        lock(app.Db.Gate)return new(CollectionRows().Select(p=>(JsonNode)new JsonObject {
            ["collectionId"]=p.Key,["rootId"]=p.Value!["rootId"]!.DeepClone(),["name"]=p.Value["name"]!.DeepClone()
        }).ToArray());
    }
    internal string? CollectionForRoot(string rootId)
    {
        if(!active || !coordinator)return null;
        lock(app.Db.Gate)return CollectionRows().FirstOrDefault(p=>S(p.Value?["rootId"])==rootId).Key;
    }
    internal string? CollectionNameForRoot(string rootId)
    {
        if(!active || !coordinator)return null;
        lock(app.Db.Gate)return CollectionRows().Select(p=>p.Value).OfType<JsonObject>().SingleOrDefault(r=>S(r["rootId"])==rootId)?["name"]?.GetValue<string>();
    }
    public JsonObject Collections()
    {
        lock(app.Db.Gate)return new() { ["groupId"]=info["groupId"]?.DeepClone(),["coordinator"]=coordinator,
            ["collections"]=coordinator?CollectionDefinitions():new JsonArray(CollectionRows(true).Select(p=>p.Value!.DeepClone()).ToArray()),
            ["bindings"]=JsonNode.Parse(app.Db.GetValue("group_collection_bindings")??"{}") };
    }
    internal void ReceiveCollections(JsonObject remote)
    {
        if(remote["collections"] is not JsonArray definitions)return; // P2 peer has no file collections.
        if(definitions.Count>128)throw new GroupSyncException("collection_limit");
        var rows=new JsonObject();
        foreach(var definition in definitions.OfType<JsonObject>())
        {
            var id=S(definition["collectionId"]);var root=S(definition["rootId"]);var name=S(definition["name"]);
            if(!Guid.TryParseExact(id,"N",out _) || !Guid.TryParseExact(root,"N",out _) || name.Length is <1 or >200 || rows.ContainsKey(id))throw new GroupSyncException("invalid_collection");
            rows[id]=new JsonObject { ["collectionId"]=id,["rootId"]=root,["name"]=name };
        }
        if(rows.Count!=definitions.Count)throw new GroupSyncException("invalid_collection");
        app.Db.SetValue("group_remote_collections",rows.ToJsonString());
    }
    public JsonObject CreateCollection(long folderId,string name)
    {
        Dispatcher.UIThread.VerifyAccess();
        if(!coordinator)throw new GroupSyncException("not_coordinator");
        name=name.Trim();if(name.Length is <1 or >200)throw new ArgumentException("Invalid collection name");
        var folder=app.Db.Folders().Single(f=>f.Id==folderId);
        if(!folder.Available)throw new LanFileDeferred("local_directory_unavailable");
        var pin=LanFileMutation.PinRoot(folder.Root);using var handle=pin.Handle;
        var oldContext=feature!.PrepareGroupContext(Bindings());CaptureLocal();
        var root=app.LanSync.EnsureRootIdentity(folderId);
        var prospective=Bindings().ToDictionary(p=>p.Key,p=>p.Value);prospective[root]=root;
        var afterContext=feature.PrepareGroupContext(prospective);
        return Frame(()=>
        {
            var rows=CollectionRows();
            if(rows.ContainsKey(root))throw new GroupSyncException("collection_already_exists");
            if(rows.Count>=128)throw new GroupSyncException("collection_limit");
            rows[root]=new JsonObject { ["rootId"]=root,["name"]=name,["folderId"]=folderId,["folderIdentity"]=folder.Identity,["nativeIdentity"]=pin.Identity };
            app.Db.SetValue("group_collections",rows.ToJsonString());
            BindCollectionIdentity(root,root,oldContext,afterContext);
            return new JsonObject { ["collectionId"]=root,["rootId"]=root,["name"]=name };
        },initialContext:oldContext,resultingContext:afterContext);
    }
    void BindCollectionIdentity(string root,string collection,SyncFeature.GroupContext before,SyncFeature.GroupContext after)
    {
        var bindings=JsonNode.Parse(app.Db.GetValue("group_collection_bindings")??"{}")!.AsObject();
        if(bindings[root] is not null && S(bindings[root])!=collection)throw new GroupSyncException("collection_already_bound");
        if(bindings.Any(p=>p.Key!=root && S(p.Value)==collection))throw new GroupSyncException("collection_already_bound");
        bindings[root]=collection;app.Db.SetValue("group_collection_bindings",bindings.ToJsonString());
        var references=before.References.Where(p=>after.References.TryGetValue(p.Key,out var next) && next!=p.Value).ToDictionary(p=>p.Value,p=>after.References[p.Key],StringComparer.Ordinal);
        journal.RebindReferences(reference=>references.GetValueOrDefault(reference,reference),feature!.GroupProjection(after));
        // Playlist references are a deliberate identity edit. Per-track history keeps its HLC.
        journal.Capture(feature.GroupProjection(after));
        journal.ReapplyCaches();
        projectedLibraryVersion=-1;
    }
    internal JsonObject RequireCollection(string collection)
    {
        lock(app.Db.Gate)
        {
            if(!active || coordinator)throw new GroupSyncException("not_member_replica");
            return CollectionRows(true)[collection]?.DeepClone().AsObject()??throw new GroupSyncException("collection_unavailable");
        }
    }
    internal JsonObject Upstream()
    {
        lock(app.Db.Gate)
        {
            if(!active || coordinator)throw new GroupSyncException("not_member_replica");
            return peers.Connection(S(info["peerId"]));
        }
    }
    internal JsonObject BindReplicaIdentity(long folderId,string collection)
    {
        Dispatcher.UIThread.VerifyAccess();RequireCollection(collection);
        var before=feature!.PrepareGroupContext(Bindings());CaptureLocal();
        var root=app.LanSync.EnsureRootIdentity(folderId);
        var prospective=Bindings().ToDictionary(p=>p.Key,p=>p.Value);prospective[root]=collection;
        var after=feature.PrepareGroupContext(prospective);
        return Frame(()=> { BindCollectionIdentity(root,collection,before,after);return new JsonObject { ["localRootId"]=root }; },initialContext:before,resultingContext:after);
    }
}
