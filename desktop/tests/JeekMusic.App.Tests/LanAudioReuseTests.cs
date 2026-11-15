using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;
using JeekMusic.Sync;
using Microsoft.Data.Sqlite;

namespace JeekMusic.App.Tests;

public sealed class LanAudioReuseTests
{
    sealed class Fixture:IDisposable {
        public string Directory { get; }=Path.Combine(Path.GetTempPath(),"jeek-audio-reuse-"+Guid.NewGuid().ToString("N"));
        public string Root=>Path.Combine(Directory,"music");public string State=>Path.Combine(Directory,"state");
        public Fixture() { System.IO.Directory.CreateDirectory(Root); }
        public void Dispose()=>System.IO.Directory.Delete(Directory,true);
        public LanFileSyncStore Open()=>new(State);
    }
    static string Text(JsonNode? n)=>n?.GetValue<string>()??"";
    static string Id()=>Guid.NewGuid().ToString("N");
    static byte[] Wave(int padding) {
        var b=new byte[52+padding+1024*1024];"RIFF"u8.CopyTo(b);BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4),b.Length-8);"WAVEfmt "u8.CopyTo(b.AsSpan(8));BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16),16);b[20]=1;b[22]=1;BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24),16000);BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28),32000);b[32]=2;b[34]=16;
        "JUNK"u8.CopyTo(b.AsSpan(36));BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40),padding);"data"u8.CopyTo(b.AsSpan(44+padding));BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(48+padding),1024*1024);b.AsSpan(52+padding).Fill(17);return b;
    }
    static async Task<(string Scope,JsonObject Args,AudioEnvelope Layout)> Prepare(Fixture f,LanFileSyncStore store,byte[] source)
    {
        var original=Wave(4);File.WriteAllBytes(Path.Combine(f.Root,"A.wav"),original);File.SetLastWriteTimeUtc(Path.Combine(f.Root,"A.wav"),DateTime.UnixEpoch.AddSeconds(100));
        var expected=FileMetadata.Token(FileMetadata.Observe(f.Root,"A.wav"));var scope=Text(store.Open("p","o","r",Id(),[""])["scope"]);
        var baseline=await store.Ack(scope,f.Root,new() { ["path"]="A.wav",["operation"]=Id(),["baselineVersion"]="",["localKnown"]=true,["localVersion"]=expected,["remoteVersion"]=expected,["stamp"]=expected },default,a=>a());
        var args=new JsonObject { ["path"]="A.wav",["deviceId"]="p",["operation"]=Id(),["size"]=source.Length,["modifiedNs"]=101_000_000_000L,["stamp"]=FileMetadata.Version(source.Length,101_000_000_000L),["expectedVersion"]=expected,["expectedStamp"]=expected,["baselineVersion"]=Text(baseline["version"]) };
        store.Begin(scope,args);using var input=new MemoryStream(source);var layout=AudioEnvelope.Parse(input,"A.wav")!;
        await store.Chunk(scope,new() { ["operation"]=args["operation"]!.DeepClone(),["offset"]=0,["data"]=Convert.ToBase64String(source,0,(int)layout.Start) },default);
        return (scope,args,layout);
    }
    static JsonObject Reuse(JsonObject args,AudioEnvelope source,string? digest=null)=>new() { ["operation"]=args["operation"]!.DeepClone(),["offset"]=source.Start,["sourceOffset"]=56,["length"]=source.Length,["digest"]=digest??Convert.ToHexStringLower(SHA256.HashData(Wave(4).AsSpan(56))) };
    [Fact] public async Task MetadataTransferReusesExactAudioWithoutChangingTheLiveFileUntilCommit()
    {
        using var f=new Fixture();using var store=f.Open();var source=Wave(1024);var (scope,args,layout)=await Prepare(f,store,source);
        var envelope=await store.AudioLayout(scope,f.Root,"A.wav",new() { ["expectedVersion"]=args["expectedVersion"]!.DeepClone(),["mediaBytes"]=layout.Length },default);
        Assert.True(envelope["supported"]!.GetValue<bool>());Assert.Equal(56,envelope["start"]!.GetValue<long>());
        var reply=await store.ReuseAudio(scope,f.Root,Reuse(args,layout),default);
        Assert.True(reply["reused"]!.GetValue<bool>());Assert.Equal(source.Length,reply["offset"]!.GetValue<long>());Assert.Equal(Wave(4),File.ReadAllBytes(Path.Combine(f.Root,"A.wav")));
        await store.Finish(scope,f.Root,Text(args["operation"]),default,a=>a());Assert.Equal(source,File.ReadAllBytes(Path.Combine(f.Root,"A.wav")));
        Assert.Equal(layout.Length,store.AudioDiagnostics()["reusedBytes"]!.GetValue<long>());
    }
    [Fact] public async Task MismatchRollsBackPrivatePrefixAndTheSameUploadCanFallBackToFullChunks()
    {
        using var f=new Fixture();using var store=f.Open();var source=Wave(1024);var (scope,args,layout)=await Prepare(f,store,source);
        var reply=await store.ReuseAudio(scope,f.Root,Reuse(args,layout,new string('0',64)),default);Assert.False(reply["reused"]!.GetValue<bool>());Assert.Equal(layout.Start,reply["offset"]!.GetValue<long>());
        for(var offset=(int)layout.Start;offset<source.Length;offset+=262144)await store.Chunk(scope,new() { ["operation"]=args["operation"]!.DeepClone(),["offset"]=offset,["data"]=Convert.ToBase64String(source,offset,Math.Min(262144,source.Length-offset)) },default);
        await store.Finish(scope,f.Root,Text(args["operation"]),default,a=>a());Assert.Equal(source,File.ReadAllBytes(Path.Combine(f.Root,"A.wav")));
    }
    [Fact] public async Task CancellationKeepsTheConfirmedPrefixAndNeverMutatesTheTarget()
    {
        using var f=new Fixture();using var store=f.Open();var (scope,args,layout)=await Prepare(f,store,Wave(1024));using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.ReuseAudio(scope,f.Root,Reuse(args,layout),cancel.Token));
        Assert.Equal(layout.Start,store.UploadStatus(scope,Text(args["operation"]))["offset"]!.GetValue<long>());Assert.Equal(Wave(4),File.ReadAllBytes(Path.Combine(f.Root,"A.wav")));
    }
    [Theory] [InlineData(false)] [InlineData(true)] public async Task InterruptedCopyRetiresItsOwnedStagingButPreservesSubstitutedObjects(bool substitute)
    {
        using var f=new Fixture();string scope;JsonObject args;
        using(var store=f.Open()) { var prepared=await Prepare(f,store,Wave(1024));scope=prepared.Scope;args=prepared.Args; }
        var part=System.IO.Directory.GetFiles(Path.Combine(f.State,"uploads"),"*.part").Single();
        if(substitute) { File.Move(part,part+".retained");File.WriteAllText(part,"external replacement"); }
        else using(var stream=new FileStream(part,FileMode.Append))stream.WriteByte(99);
        using(var db=new SqliteConnection($"Data Source={Path.Combine(f.State,"file-sync.db")};Pooling=False")) { db.Open();using var update=db.CreateCommand();update.CommandText="UPDATE operations SET json=json_set(json,'$.reuseInProgress',json('true'))";update.ExecuteNonQuery(); }
        using(var store=f.Open()) {
            Assert.Equal("aborted",Text(store.UploadStatus(scope,Text(args["operation"]))["state"]));Assert.Equal(Wave(4),File.ReadAllBytes(Path.Combine(f.Root,"A.wav")));
            if(substitute) { Assert.Equal("external replacement",File.ReadAllText(part));Assert.Throws<LanFileDeferred>(()=>store.Begin(scope,args)); }
            else Assert.Equal(0L,store.Begin(scope,args)["offset"]!.GetValue<long>());
        }
    }
}
