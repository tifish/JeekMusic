using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

public sealed class LanFileDeltaTests
{
    sealed class Fixture:IDisposable {
        public string Directory { get; }=Path.Combine(Path.GetTempPath(),"jeek-delta-"+Guid.NewGuid().ToString("N"));
        public LanFileSyncStore Open()=>new(Directory);
        public void Dispose()=>System.IO.Directory.Delete(Directory,true);
    }
    static string Text(JsonNode? node)=>node?.GetValue<string>()??"";
    static string Id()=>Guid.NewGuid().ToString("N");
    static LanFiles.Entry File(string path,long size=64,long time=100)=>new(path,size,time,"unused");
    static JsonObject Delta(LanFileSyncStore store,string scope,string cursor,IReadOnlyList<LanFiles.Entry> files,Func<LanFiles.Entry,long>? edits=null)=>store.Delta(scope,cursor,files,[""],edits??(_=>0),default);

    [Fact] public void ConfirmedSnapshotPersistsAndOnlyChangedPathsReturnIncludingOldDatedAddsAndEditClock()
    {
        using var fixture=new Fixture();string scope,cursor;
        var files=Enumerable.Range(0,401).Select(i=>File($"{i:D4}.mp3")).ToArray();
        using(var store=fixture.Open()) {
            scope=Text(store.Open("phone","owner","root",Id(),[""])["scope"]);
            var full=Delta(store,scope,"",files);cursor=Text(full["cursor"]);
            Assert.True(full["reset"]!.GetValue<bool>());Assert.Equal(401,full["total"]!.GetValue<int>());
            Assert.Equal(200,full["rows"]!.AsArray().Count);Assert.Equal(200,store.DeltaRows(scope,cursor,200)["rows"]!.AsArray().Count);Assert.Single(store.DeltaRows(scope,cursor,400)["rows"]!.AsArray());
            store.CommitDelta(scope,cursor);Assert.True(store.CommitDelta(scope,cursor)["accepted"]!.GetValue<bool>());
        }
        using(var store=fixture.Open()) {
            var unchanged=Delta(store,scope,cursor,files);Assert.False(unchanged["reset"]!.GetValue<bool>());Assert.Empty(unchanged["rows"]!.AsArray());
            store.CommitDelta(scope,Text(unchanged["cursor"]));cursor=Text(unchanged["cursor"]);
            var changed=files.Skip(1).Select(e=>e.Path=="0001.mp3"?File(e.Path,65):e.Path=="0002.mp3"?File(e.Path,64,99):e).Append(File("old-dated-new.mp3",1,-100)).ToArray();
            var delta=Delta(store,scope,cursor,changed,e=>e.Path=="0003.mp3"?500:0);
            Assert.Equal(5,delta["total"]!.GetValue<int>());
            var rows=delta["rows"]!.AsArray().Select(n=>n!.AsObject()).ToDictionary(r=>Text(r["path"]));
            Assert.Null(rows["0000.mp3"]["remote"]);Assert.Equal(-100,rows["old-dated-new.mp3"]["remote"]!["modifiedNs"]!.GetValue<long>());
            Assert.Equal(500,rows["0003.mp3"]["remote"]!["editedAtMs"]!.GetValue<long>());
        }
    }
    [Fact] public void InterruptedOrReplacedCheckpointCannotAdvanceAndUnknownCursorResets()
    {
        using var fixture=new Fixture();using var store=fixture.Open();var scope=Text(store.Open("p","o","r",Id(),[""])["scope"]);
        var first=Delta(store,scope,"",[File("A.mp3")]);var cursor=Text(first["cursor"]);store.CommitDelta(scope,cursor);
        var interrupted=Delta(store,scope,cursor,[File("A.mp3",65)]);
        var retry=Delta(store,scope,cursor,[File("A.mp3",66)]);
        Assert.Equal(1,retry["total"]!.GetValue<int>());Assert.Equal(66,retry["rows"]![0]!["remote"]!["size"]!.GetValue<long>());
        Assert.Throws<LanFileDeferred>(()=>store.CommitDelta(scope,Text(interrupted["cursor"])));
        Assert.Throws<LanFileDeferred>(()=>store.DeltaRows(scope,Text(interrupted["cursor"]),0));
        var reset=Delta(store,scope,"lost-cache",[File("A.mp3",66)]);Assert.True(reset["reset"]!.GetValue<bool>());
        Assert.Throws<ArgumentException>(()=>store.Delta(scope,cursor,[File("A.mp3")],["other"],_=>0,default));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.Throws<OperationCanceledException>(()=>store.Delta(scope,cursor,[File("A.mp3")],[""],_=>0,cancel.Token));
        Assert.True(store.CommitDelta(scope,Text(reset["cursor"]))["accepted"]!.GetValue<bool>());
    }
    [Fact] public async Task BaselineAndChangesDuringTheRoundAppearOnTheNextCheckpoint()
    {
        using var fixture=new Fixture();using var store=fixture.Open();var scope=Text(store.Open("p","o","r",Id(),[""])["scope"]);
        System.IO.File.WriteAllBytes(Path.Combine(fixture.Directory,"A.mp3"),new byte[64]);
        var observed=FileMetadata.Observe(fixture.Directory,"A.mp3");var version=FileMetadata.Token(observed);
        var file=File("A.mp3",64,observed["modifiedNs"]!.GetValue<long>());
        var first=Delta(store,scope,"",[file]);var cursor=Text(first["cursor"]);
        await store.Ack(scope,fixture.Directory,new() { ["path"]="A.mp3",["operation"]=Id(),["baselineVersion"]="",["remoteVersion"]=version,["localVersion"]=version,["localKnown"]=true,["decision"]="ack" },default,a=>a());
        store.CommitDelta(scope,cursor);
        var next=Delta(store,scope,cursor,[file,File("B.mp3")]);
        Assert.Equal(2,next["total"]!.GetValue<int>());
        Assert.NotNull(next["rows"]![0]!["baseline"]);
    }
}
