using JeekMusic.Core.Sync;
using System.Text.Json.Nodes;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

public sealed class LanFileSyncStoreTests
{
    sealed class Fixture:IDisposable
    {
        public string Directory=Path.Combine(Path.GetTempPath(),"jeek-file-store-"+Guid.NewGuid().ToString("N"));
        public string Root,State;public Fixture() { Root=Path.Combine(Directory,"music");State=Path.Combine(Directory,"private");System.IO.Directory.CreateDirectory(Root); }
        public LanFileSyncStore Store()=>new(State,new(FileBytes:1024*1024,StagingBytes:2*1024*1024,BackupBytes:4*1024*1024));
        public void Dispose()=>System.IO.Directory.Delete(Directory,true);
    }
    static byte[] Audio(byte marker) { var b=new byte[64];"RIFF"u8.CopyTo(b);"WAVE"u8.CopyTo(b.AsSpan(8));b[^1]=marker;return b; }
    static long Time(byte[] b)=>1_800_000_000_000_000_000L+b[^1]*1_000_000_000L;
    static string Stamp(byte[] b)=>FileMetadata.Version(b.Length,Time(b));
    static void Write(string path,byte[] b) { File.WriteAllBytes(path,b);File.SetLastWriteTimeUtc(path,DateTime.UnixEpoch.AddTicks(Time(b)/100)); }
    static string Id()=>Guid.NewGuid().ToString("N");
    static string Text(JsonNode? node)=>node?.GetValue<string>()??"";
    static JsonObject Ack(string path,string? hash,string version)=>new() { ["path"]=path,["stamp"]=hash,["remoteVersion"]=hash,["localVersion"]=hash,["localKnown"]=true,["baselineVersion"]=version,["operation"]=Id() };
    static JsonObject Upload(string path,byte[] content,string? expected,string version,string operation)=>new() { ["deviceId"]="phone",["path"]=path,["stamp"]=Stamp(content),["expectedStamp"]=expected,["expectedVersion"]=expected,["modifiedNs"]=Time(content),["size"]=content.Length,["baselineVersion"]=version,["operation"]=operation };
    static async Task Feed(LanFileSyncStore store,string scope,string operation,byte[] bytes)
    {
        var first=new JsonObject { ["operation"]=operation,["offset"]=0,["data"]=Convert.ToBase64String(bytes,0,32) };
        await store.Chunk(scope,first,default);await store.Chunk(scope,first,default); // Exact replay is harmless.
        await store.Chunk(scope,new() { ["operation"]=operation,["offset"]=32,["data"]=Convert.ToBase64String(bytes,32,32) },default);
    }
    [Fact] public async Task UploadDeleteAndAcknowledgementHaveDurableSeparateBoundaries()
    {
        using var f=new Fixture();var a=Audio(1);var b=Audio(2);var path=Path.Combine(f.Root,"A.wav");Write(path,a);
        string mapping=Id(),scope,version,upload=Id();
        using(var store=f.Store()) {
            scope=Text(store.Open("phone","credential-owner","root",mapping,[""])["scope"]);
            version=Text((await store.Ack(scope,f.Root,Ack("A.wav",Stamp(a),""),default,action=>action()))["version"]);
            store.Begin(scope,Upload("A.wav",b,Stamp(a),version,upload));await Feed(store,scope,upload,b);
            await Assert.ThrowsAnyAsync<IOException>(()=>store.Finish(scope,f.Root,upload,default,action=> { action();throw new IOException("simulated crash after rename"); }));
            Assert.Equal(b,File.ReadAllBytes(path));
            var page=store.Open("phone","credential-owner","root",mapping,[""]);
            Assert.Equal(Stamp(a),Text(store.Baselines(scope,0,Text(page["revision"]))["rows"]![0]!["stamp"])); // No premature baseline advance.
        }
        using(var store=f.Store()) {
            Assert.True((await store.Finish(scope,f.Root,upload,default,action=>action()))["committed"]!.GetValue<bool>());
            Assert.True((await store.Finish(scope,f.Root,upload,default,action=>action()))["committed"]!.GetValue<bool>());
            Assert.Single(System.IO.Directory.GetFiles(f.Root,".jeek-sync-*.bak"));
            Assert.Equal(a,File.ReadAllBytes(System.IO.Directory.GetFiles(f.Root,".jeek-sync-*.bak").Single()));
            var ack=Ack("A.wav",Stamp(b),version);var next=await store.Ack(scope,f.Root,ack,default,action=>action());
            Assert.Equal(Text(next["version"]),Text((await store.Ack(scope,f.Root,ack,default,action=>action()))["version"]));
            var deletion=new JsonObject { ["path"]="A.wav",["expectedStamp"]=Stamp(b),["expectedVersion"]=Stamp(b),["baselineVersion"]=Text(next["version"]),["operation"]=Id() };
            await store.Delete(scope,f.Root,deletion,default,action=>action());await store.Delete(scope,f.Root,deletion,default,action=>action());Assert.False(File.Exists(path));
            Assert.Equal(2,System.IO.Directory.GetFiles(f.Root,".jeek-sync-*.bak").Length);
            var reply=await store.Ack(scope,f.Root,Ack("A.wav",null,Text(next["version"])),default,action=>action());Assert.Null(reply["stamp"]);
        }
    }
    [Fact] public async Task PreparedReplacementCanResumeAfterOldFileMovedButBeforeNewRename()
    {
        using var f=new Fixture();var a=Audio(1);var b=Audio(2);Write(Path.Combine(f.Root,"A.wav"),a);var op=Id();var mapping=Id();string scope;
        using(var store=f.Store()) {
            scope=Text(store.Open("phone","owner","root",mapping,[""])["scope"]);var version=Text((await store.Ack(scope,f.Root,Ack("A.wav",Stamp(a),""),default,x=>x()))["version"]);
            store.Begin(scope,Upload("A.wav",b,Stamp(a),version,op));await Feed(store,scope,op,b);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>store.Finish(scope,f.Root,op,default,_=>throw new UnauthorizedAccessException()));
            Assert.False(LanFileSyncStore.BlocksScan(f.State,f.Root)); // Denial before any rename leaves the library usable.
        }
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(f.State,"file-sync.db")};Pooling=False")) {
            db.Open();using var query=db.CreateCommand();
            // Recreate the durable prepared boundary before the first native rename. No user files involved.
            query.CommandText="UPDATE operations SET state='prepared',backup_bytes=64,json=json_set(json,'$.state','prepared','$.backup',$backup,'$.backupBytes',64)";
            query.Parameters.AddWithValue("$backup",".jeek-sync-"+op+".bak");query.ExecuteNonQuery();
        }
        File.Move(Path.Combine(f.Root,"A.wav"),Path.Combine(f.Root,".jeek-sync-"+op+".bak")); // Exact isolated crash point from the durable prepared journal.
        Assert.True(LanFileSyncStore.BlocksScan(f.State,f.Root));
        using(var store=f.Store()) {
            Assert.True((await store.Finish(scope,f.Root,op,default,x=>x()))["committed"]!.GetValue<bool>());
            Assert.False(LanFileSyncStore.BlocksScan(f.State,f.Root));
            Assert.Equal(b,File.ReadAllBytes(Path.Combine(f.Root,"A.wav")));Assert.Equal(a,File.ReadAllBytes(Path.Combine(f.Root,".jeek-sync-"+op+".bak")));
        }
    }
    [Fact] public async Task ScopeOwnershipUntrackedDeletesChangedContentAndFalseAcknowledgementsFailClosed()
    {
        using var f=new Fixture();using var store=f.Store();var a=Audio(1);Write(Path.Combine(f.Root,"A.wav"),a);
        var scope=Text(store.Open("phone","owner","root",Id(),["Album"])["scope"]);
        Assert.Throws<UnauthorizedAccessException>(()=>store.Root(scope,"different-credential"));Assert.Throws<UnauthorizedAccessException>(()=>store.PathAllowed(scope,"A.wav"));
        scope=Text(store.Open("phone","owner","root",Id(),[""])["scope"]);
        await Assert.ThrowsAsync<LanFileConflict>(()=>store.Delete(scope,f.Root,new() { ["path"]="A.wav",["operation"]=Id(),["expectedStamp"]=Stamp(a),["expectedVersion"]=Stamp(a),["baselineVersion"]="" },default,x=>x()));
        var fake=Ack("A.wav",Stamp(a),"");fake["localKnown"]=false;await Assert.ThrowsAnyAsync<IOException>(()=>store.Ack(scope,f.Root,fake,default,x=>x()));
        var ack=await store.Ack(scope,f.Root,Ack("A.wav",Stamp(a),""),default,x=>x());Write(Path.Combine(f.Root,"A.wav"),Audio(3));
        await Assert.ThrowsAsync<LanFileConflict>(()=>store.Delete(scope,f.Root,new() { ["path"]="A.wav",["operation"]=Id(),["expectedStamp"]=Stamp(a),["expectedVersion"]=Stamp(a),["baselineVersion"]=Text(ack["version"]) },default,x=>x()));
        Assert.Equal(Audio(3),File.ReadAllBytes(Path.Combine(f.Root,"A.wav")));
        await Assert.ThrowsAnyAsync<IOException>(()=>store.Probe(Path.Combine(f.Directory,"offline"),"A.wav",default));
    }
    [Fact] public async Task CancellationBeforeRenameReleasesOwnedStagingWithoutAdvancingBaselineOrBlockingScan()
    {
        using var f=new Fixture();using var store=f.Store();var a=Audio(1);var b=Audio(2);Write(Path.Combine(f.Root,"A.wav"),a);
        var scope=Text(store.Open("phone","owner","root",Id(),[""])["scope"]);var version=Text((await store.Ack(scope,f.Root,Ack("A.wav",Stamp(a),""),default,x=>x()))["version"]);var operation=Id();
        store.Begin(scope,Upload("A.wav",b,Stamp(a),version,operation));await Feed(store,scope,operation,b);using var cancellation=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.Finish(scope,f.Root,operation,cancellation.Token,_=> { cancellation.Cancel();cancellation.Token.ThrowIfCancellationRequested(); }));
        Assert.Equal(a,File.ReadAllBytes(Path.Combine(f.Root,"A.wav")));Assert.False(LanFileSyncStore.BlocksScan(f.State,f.Root));Assert.Equal("aborted",Text(store.UploadStatus(scope,operation)["state"]));
        Assert.Equal(version,Text(store.BaselineRow(scope,"A.wav")["version"]));Assert.Empty(Directory.GetFiles(f.Root,".jeek-sync-*.bak"));
        operation=Id();store.Begin(scope,Upload("A.wav",b,Stamp(a),version,operation));await Feed(store,scope,operation,b);store.RetireUploads("phone");
        Assert.Equal("aborted",Text(store.UploadStatus(scope,operation)["state"]));Assert.Equal(0,store.UploadStatus(scope,operation)["offset"]!.GetValue<long>());
    }
    [Theory] [InlineData("A.exe")] [InlineData("A.wav")] [InlineData("A.jpg")]
    public async Task ExecutablesAndFalseContainersNeverReachMusicRoot(string name)
    {
        using var f=new Fixture();var source=Path.Combine(f.Directory,"upload.part");Write(source,"MZ synthetic executable envelope"u8.ToArray());
        await Assert.ThrowsAnyAsync<IOException>(()=>LanFileContent.Validate(source,name,default));Assert.Empty(System.IO.Directory.GetFiles(f.Root));
    }
}
