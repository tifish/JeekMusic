using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

public sealed class WindowsReplicaTests
{
    [Fact] public async Task PreparedRenameWithALaterEditBecomesARetainedConflictInsteadOfBlockingEveryRetry()
    {
        var directory=Path.Combine(Path.GetTempPath(),"jeek-replica-prepared-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var root=Path.Combine(directory,"music");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"a.lrc"),"old");
        try
        {
            using var store=new LanFileSyncStore(Path.Combine(directory,"private"));
            var scope=store.Open("coordinator","owner","root",Guid.NewGuid().ToString("N"),[""])["scope"]!.GetValue<string>();
            var old=FileMetadata.Token(FileMetadata.Observe(root,"a.lrc"));var op=Guid.NewGuid().ToString("N");var stamp=FileMetadata.Version(3,1_790_000_000_000_000_000);
            store.Begin(scope,new() { ["path"]="a.lrc",["operation"]=op,["stamp"]=stamp,["expectedStamp"]=old,["expectedVersion"]=old,["baselineVersion"]="",["resolve"]=true,["size"]=3L,["modifiedNs"]=1_790_000_000_000_000_000L,["deviceId"]="pc" });
            await store.Chunk(scope,new() { ["operation"]=op,["offset"]=0L,["data"]=Convert.ToBase64String("new"u8) },default);
            await Assert.ThrowsAsync<IOException>(()=>store.Finish(scope,root,op,default,a=> { a();throw new IOException("simulate process loss after rename"); }));
            Assert.Equal("prepared",store.UploadStatus(scope,op)["state"]!.GetValue<string>());
            File.WriteAllText(Path.Combine(root,"a.lrc"),"later user edit");
            await Assert.ThrowsAsync<LanFileConflict>(()=>store.Finish(scope,root,op,default,a=>a()));
            Assert.Equal("conflict",store.UploadStatus(scope,op)["state"]!.GetValue<string>());
            Assert.True(store.Abort(scope,op)["aborted"]!.GetValue<bool>());
            Assert.Equal("later user edit",File.ReadAllText(Path.Combine(root,"a.lrc")));
            Assert.Equal("old",File.ReadAllText(Path.Combine(root,".jeek-sync-"+op+".bak")));
            Assert.Single(store.Backups()["backups"]!.AsArray());
        }
        finally { Directory.Delete(directory,true); }
    }
    [Fact] public void ClientCheckpointsSurviveReopenAndPagesNeverCrossMappingKinds()
    {
        var directory=Path.Combine(Path.GetTempPath(),"jeek-replicas-"+Guid.NewGuid().ToString("N"));
        try
        {
            using(var store=new WindowsReplicaStore(directory))
            {
                for(int i=0;i<205;i++)store.Put("pending:a",i.ToString("D4"),new() { ["operation"]=i,["state"]="landed" });
                store.Put("pending:b","0000",new() { ["state"]="created" });
                var first=store.Page("pending:a");Assert.Equal(200,first.Count);Assert.Equal(5,store.Page("pending:a",first[^1].Key).Count);
            }
            using(var store=new WindowsReplicaStore(directory))
            {
                Assert.Equal(205,store.Count("pending:a"));Assert.Equal("landed",store.Read("pending:a","0000")!["state"]!.GetValue<string>());
                store.Clear("pending:a");Assert.Equal(0,store.Count("pending:a"));Assert.Equal(1,store.Count("pending:b"));
            }
        }
        finally { Directory.Delete(directory,true); }
    }
    [Fact] public async Task AConfirmedLandingRetainsItsHistoricalVersionAfterAnotherLocalEdit()
    {
        var directory=Path.Combine(Path.GetTempPath(),"jeek-replica-landing-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var root=Path.Combine(directory,"music");Directory.CreateDirectory(root);
        try
        {
            using var store=new LanFileSyncStore(Path.Combine(directory,"private"));
            var scope=store.Open("coordinator","owner","root",Guid.NewGuid().ToString("N"),[""])["scope"]!.GetValue<string>();
            var op=Guid.NewGuid().ToString("N");var stamp=FileMetadata.Version(3,1_790_000_000_000_000_000);
            store.Begin(scope,new() { ["path"]="a.lrc",["operation"]=op,["stamp"]=stamp,["expectedStamp"]=null,["expectedVersion"]=null,["baselineVersion"]="",["size"]=3L,["modifiedNs"]=1_790_000_000_000_000_000L,["deviceId"]="pc" });
            await store.Chunk(scope,new() { ["operation"]=op,["offset"]=0L,["data"]=Convert.ToBase64String("old"u8) },default);
            var landed=await store.Finish(scope,root,op,default,a=>a());
            File.WriteAllText(Path.Combine(root,"a.lrc"),"new user edit");
            var evidence=new JsonObject { ["operation"]=Guid.NewGuid().ToString("N"),["baselineVersion"]="",["remoteVersion"]=landed["resultVersion"]!.DeepClone(),["localVersion"]=stamp,["stamp"]=stamp,["mutationOperation"]=op };
            var first=store.ConfirmReplicaBaseline(scope,"a.lrc",evidence);
            Assert.Equal(first.ToJsonString(),store.ConfirmReplicaBaseline(scope,"a.lrc",evidence).ToJsonString());
            Assert.Equal("new user edit",File.ReadAllText(Path.Combine(root,"a.lrc")));
            Assert.Equal(stamp,store.BaselineRow(scope,"a.lrc")["remoteVersion"]!.GetValue<string>());
            Assert.Throws<LanFileDeferred>(()=>store.ConfirmReplicaBaseline(scope,"a.lrc",new() { ["operation"]=Guid.NewGuid().ToString("N"),["baselineVersion"]="",["remoteVersion"]=stamp,["localVersion"]=stamp }));
        }
        finally { Directory.Delete(directory,true); }
    }
}
