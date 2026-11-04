using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

public sealed class LanSharedConflictTests
{
    [Fact] public void ChoicesAreDurableFirstWriterWinsAndBoundToExactVersions() {
        var dir=Path.Combine(Path.GetTempPath(),"jeek-conflicts-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try {
            var root=Path.Combine(dir,"root");Directory.CreateDirectory(root);var file=Path.Combine(root,"a.mp3");File.WriteAllBytes(file,[1,2,3]);
            var state=Path.Combine(dir,"state");var mapping=Guid.NewGuid().ToString("N");string scope,id;
            using(var store=new LanFileSyncStore(state)) {
                scope=store.Open("phone","owner","root",mapping,[""])["scope"]!.GetValue<string>();
                var remote=FileMetadata.Token(FileMetadata.Observe(root,"a.mp3"));var local="m2:4:1234567890000000000";
                id=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",mapping,scope,"a.mp3","",local,remote))));
                var args=new JsonObject { ["id"]=id,["path"]="a.mp3",["baselineVersion"]="",["localVersion"]=local,["remoteVersion"]=remote,["reason"]="initial_different_content" };
                var row=store.ReportConflict(scope,root,args);Assert.Equal(4,row["local"]!["size"]!.GetValue<long>());Assert.Equal(3,row["remote"]!["size"]!.GetValue<long>());
                var rev=store.ConflictRevision(scope);store.ChooseConflict(scope,root,id,"local");Assert.NotEqual(rev,store.ConflictRevision(scope));
                var chosenRevision=store.ConflictRevision(scope);
                Assert.Equal("local",store.ConflictStatus(scope,new JsonArray(id))["rows"]![0]!["choice"]!.GetValue<string>());
                Assert.Equal(chosenRevision,store.ConflictRevision(scope)); // reads cannot trigger change loops
                Assert.Empty(store.ConflictStatus("another-scope",new JsonArray(id))["rows"]!.AsArray());
                Assert.Throws<ArgumentException>(()=>store.ConflictStatus(scope,new JsonArray("invalid")));
                Assert.Throws<ArgumentException>(()=>store.ConflictStatus(scope,new JsonArray(Enumerable.Repeat(id,101).Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray())));
                Assert.Equal("local",store.ReportConflict(scope,root,args)["choice"]!.GetValue<string>());
                Assert.Throws<LanFileConflict>(()=>store.ChooseConflict(scope,root,id,"remote"));
                Assert.Throws<UnauthorizedAccessException>(()=>store.ChooseConflict("another-scope",root,id,"local"));
                Assert.Equal(new byte[] {1,2,3},File.ReadAllBytes(file));
            }
            using(var store=new LanFileSyncStore(state)) {
                Assert.Equal(1,store.PendingConflictCount);Assert.Equal("local",store.ConflictPage()["rows"]![0]!["choice"]!.GetValue<string>());
                File.WriteAllBytes(file,[9,8,7,6,5]);Assert.Throws<LanFileConflict>(()=>store.ChooseConflict(scope,root,id,"local"));
                var current=FileMetadata.Token(FileMetadata.Observe(root,"a.mp3"));
                store.ClearConvergedConflict(scope,root,new() { ["path"]="a.mp3",["localVersion"]=current,["remoteVersion"]=current });Assert.Equal(0,store.PendingConflictCount);
                Assert.Empty(store.ConflictStatus(scope,new JsonArray(id))["rows"]!.AsArray());
                Assert.Equal(id,store.ConflictStatus(scope,new JsonArray(id))["missing"]![0]!.GetValue<string>());
                foreach(var local in new[] { current,null }) {
                    var comparisonId=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",mapping,scope,"a.mp3","",local??"",current))));
                    var comparison=store.ReportConflict(scope,root,new() { ["id"]=comparisonId,["path"]="a.mp3",["baselineVersion"]="",["localVersion"]=local,["remoteVersion"]=current,["reason"]="initial_different_content" });
                    foreach(var side in new[] { "local","remote" }) {
                        Assert.Equal(local is null?"unavailable":"equal",comparison[side]!["sizeComparison"]!.GetValue<string>());
                        Assert.Equal(local is null?"unavailable":"equal",comparison[side]!["modifiedComparison"]!.GetValue<string>());
                    }
                    store.ClearConvergedConflict(scope,root,new() { ["path"]="a.mp3",["localVersion"]=current,["remoteVersion"]=current });
                }
            }
        } finally { Directory.Delete(dir,true); }
    }
}
