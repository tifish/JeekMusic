using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

[Collection("Metadata IO counters")]
public sealed class LanMetadataTests
{
    [Fact] public async Task ManifestProbeAndAcknowledgementNeverReadContentAndKeepExactLandingPair()
    {
        var dir=Path.Combine(Path.GetTempPath(),"jeek-metadata-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try {
            var root=Path.Combine(dir,"music");Directory.CreateDirectory(root);
            using(var f=File.Create(Path.Combine(root,"large.wav")))f.SetLength(32*1024*1024);
            File.WriteAllText(Path.Combine(root,"sheet.cue"),"No parsing is needed during metadata scanning");
            var before=Interlocked.Read(ref FileMetadata.HashBytes);
            var entries=await LanFiles.Manifest(root,[""],metadataOnly:true);Assert.Equal(2,entries.Count);Assert.All(entries,e=>Assert.Empty(e.Hash));
            using var store=new LanFileSyncStore(Path.Combine(dir,"state"));
            var scope=store.Open("phone","owner","root",Guid.NewGuid().ToString(),[""])["scope"]!.GetValue<string>();
            var observed=await store.Probe(root,"large.wav",default,scope);Assert.False(observed.ContainsKey("hash"));
            var ns=observed["modifiedNs"]!.GetValue<long>();var rounded=ns/1_000_000_000*1_000_000_000;
            var a=new JsonObject { ["path"]="large.wav",["operation"]=Guid.NewGuid().ToString("N"),["baselineVersion"]="",["localKnown"]=true,["remoteVersion"]=FileMetadata.Token(observed),["localVersion"]=FileMetadata.Version(32*1024*1024,rounded),["localPrecisionNs"]=1_000_000_000,["decision"]="download" };
            var ack=await store.Ack(scope,root,a,default,x=>x());
            Assert.Equal(a["localVersion"]!.GetValue<string>(),ack["localVersion"]!.GetValue<string>());
            Assert.Equal(ack["version"]!.GetValue<string>(),(await store.Ack(scope,root,a,default,x=>x()))["version"]!.GetValue<string>());
            var upload=new JsonObject { ["path"]="large.wav",["deviceId"]="phone",["operation"]=Guid.NewGuid().ToString("N"),["size"]=32*1024*1024,["hash"]=new string('a',64),["expectedHash"]=new string('b',64),["expectedVersion"]=FileMetadata.Token(observed),["modifiedNs"]=ns,["baselineVersion"]=ack["version"]!.DeepClone() };
            store.Begin(scope,upload);upload["modifiedNs"]=ns+100;
            Assert.ThrowsAny<IOException>(()=>store.Begin(scope,upload));
            Assert.Equal(before,Interlocked.Read(ref FileMetadata.HashBytes));
            File.SetLastWriteTimeUtc(Path.Combine(root,"large.wav"),new DateTime((ns/100)+DateTime.UnixEpoch.Ticks+1,DateTimeKind.Utc));
            a["operation"]=Guid.NewGuid().ToString("N");a["baselineVersion"]=ack["version"]!.DeepClone();
            await Assert.ThrowsAsync<LanFileConflict>(()=>store.Ack(scope,root,a,default,x=>x()));
            await Assert.ThrowsAnyAsync<IOException>(()=>store.Probe(Path.Combine(dir,"offline"),"large.wav",default,scope));
        }finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task LegacyMigrationPreservesRowsTombstonesAndCannotPropagateUnprovenDeletion()
    {
        var dir=Path.Combine(Path.GetTempPath(),"jeek-migrate-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try {
            string scope;using(var store=new LanFileSyncStore(dir))scope=store.Open("phone","owner","root",Guid.NewGuid().ToString(),[""])["scope"]!.GetValue<string>();
            using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir,"file-sync.db")};Pooling=False")) {
                db.Open();using var q=db.CreateCommand();q.CommandText="INSERT INTO baseline(scope,path,hash,version,operation) VALUES($s,'alive.wav',$h,'v1','legacy'),($s,'deleted.wav',NULL,'v2','legacy')";q.Parameters.AddWithValue("$s",scope);q.Parameters.AddWithValue("$h",new string('a',64));q.ExecuteNonQuery();
            }
            using var migrated=new LanFileSyncStore(dir);
            Assert.Equal("legacy_metadata_unconfirmed",migrated.BaselineRow(scope,"alive.wav")["migration"]!.GetValue<string>());
            Assert.Equal(2,migrated.BaselineRow(scope,"deleted.wav")["comparisonProtocol"]!.GetValue<int>());
            Assert.Equal("v2",migrated.BaselineRow(scope,"deleted.wav")["version"]!.GetValue<string>());
            var deletion=new JsonObject { ["path"]="alive.wav",["operation"]=Guid.NewGuid().ToString("N"),["expectedHash"]=new string('a',64),["expectedVersion"]="m2:64:100",["baselineVersion"]="v1" };
            await Assert.ThrowsAsync<LanFileConflict>(()=>migrated.Delete(scope,dir,deletion,default,x=>x()));
        }finally { Directory.Delete(dir,true); }
    }
}

[CollectionDefinition("Metadata IO counters", DisableParallelization=true)]
public sealed class MetadataIoCollection;
