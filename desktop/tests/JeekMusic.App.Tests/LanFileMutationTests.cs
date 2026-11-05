using JeekMusic.Core.Sync;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

public sealed class LanFileMutationTests
{
    sealed class Fixture:IDisposable
    {
        public string Root=Path.Combine(Path.GetTempPath(),"jeek-file-mutation-"+Guid.NewGuid().ToString("N"));
        public Fixture()=>Directory.CreateDirectory(Root);
        public string File(string name,string text) { var path=Path.Combine(Root,name);System.IO.File.WriteAllText(path,text);System.IO.File.SetLastWriteTimeUtc(path,DateTime.UnixEpoch.AddTicks(1_800_000_000_000_000_000L/100));return path; }
        public void Dispose()=>Directory.Delete(Root,true);
    }
    static string Stamp(string text)=>FileMetadata.Version(System.Text.Encoding.UTF8.GetByteCount(text),1_800_000_000_000_000_000L);
    [Fact] public async Task ConditionalCreateReplaceAndRecoverableDeleteUseVerifiedHandles()
    {
        using var f=new Fixture();var source=f.File("upload.part","new");var operation=Guid.NewGuid().ToString("N");
        using(var mutation=new LanFileMutation(f.Root,"Music/A.wav",true)) {
            Assert.Null(await mutation.Observe(1024,default,true));await mutation.Stage(source,Stamp("new"),3,operation,default);mutation.Commit(operation,false,a=>a());
        }
        Assert.Equal("new",File.ReadAllText(Path.Combine(f.Root,"Music/A.wav")));
        source=f.File("upload.part","next");operation=Guid.NewGuid().ToString("N");string? backup;
        using(var mutation=new LanFileMutation(f.Root,"Music/A.wav",true)) {
            Assert.Equal(Stamp("new"),await mutation.Observe(1024,default,true));
            Assert.Throws<IOException>(()=>File.WriteAllText(mutation.FullPath,"concurrent"));
            await mutation.Stage(source,Stamp("next"),4,operation,default);mutation.Commit(operation,false,a=>a());backup=mutation.BackupRelative;
        }
        Assert.Equal("new",File.ReadAllText(Path.Combine(f.Root,backup!)));Assert.Equal("next",File.ReadAllText(Path.Combine(f.Root,"Music/A.wav")));
        operation=Guid.NewGuid().ToString("N");
        using(var mutation=new LanFileMutation(f.Root,"Music/A.wav",false)) {
            Assert.Equal(Stamp("next"),await mutation.Observe(1024,default,true));mutation.Commit(operation,true,a=>a());backup=mutation.BackupRelative;
        }
        Assert.False(File.Exists(Path.Combine(f.Root,"Music/A.wav")));Assert.Equal("next",File.ReadAllText(Path.Combine(f.Root,backup!)));
    }
    [Fact] public async Task RevocationAndNewLocalTargetNeverOverwriteOrLeakStaging()
    {
        using var f=new Fixture();var source=f.File("upload.part","new");var operation=Guid.NewGuid().ToString("N");
        using(var mutation=new LanFileMutation(f.Root,"A.wav",true)) {
            Assert.Null(await mutation.Observe(1024,default,true));await mutation.Stage(source,Stamp("new"),3,operation,default);
            Assert.Throws<UnauthorizedAccessException>(()=>mutation.Commit(operation,false,_=>throw new UnauthorizedAccessException()));
        }
        Assert.Empty(Directory.EnumerateFiles(f.Root,".jeek-sync-*"));Assert.False(File.Exists(Path.Combine(f.Root,"A.wav")));
        operation=Guid.NewGuid().ToString("N");
        using(var mutation=new LanFileMutation(f.Root,"A.wav",true)) {
            Assert.Null(await mutation.Observe(1024,default,true));await mutation.Stage(source,Stamp("new"),3,operation,default);
            Assert.Throws<IOException>(()=>mutation.Commit(operation,false,action=> { f.File("A.wav","local race");action(); }));
        }
        Assert.Equal("local race",File.ReadAllText(Path.Combine(f.Root,"A.wav")));Assert.Empty(Directory.EnumerateFiles(f.Root,".jeek-sync-*"));
    }
    [Fact] public async Task StageResumeRequiresRecordedIdentityAndMetadataAndRetainsUnknownFiles()
    {
        using var f=new Fixture();var source=f.File("upload.part","complete");var op=Guid.NewGuid().ToString("N");
        var partial=f.File(".jeek-sync-"+op+".new","com");string id,stamp;
        using(var owned=LanFileMutation.OpenPrivate(partial,false)) { id=LanFileMutation.Identity(owned);stamp=FileMetadata.Stamp(owned.SafeFileHandle); }
        using(var mutation=new LanFileMutation(f.Root,"A.wav",true))
            await Assert.ThrowsAsync<IOException>(()=>mutation.Stage(source,Stamp("complete"),8,op,default));
        Assert.Equal("com",File.ReadAllText(partial));
        File.AppendAllText(partial,"changed");
        using(var mutation=new LanFileMutation(f.Root,"A.wav",true))
            await Assert.ThrowsAsync<IOException>(()=>mutation.Stage(source,Stamp("complete"),8,op,default,id,stamp));
        Assert.Equal("comchanged",File.ReadAllText(partial));Assert.False(File.Exists(Path.Combine(f.Root,"A.wav")));
        f.File(".jeek-sync-"+op+".new","com");
        using(var owned=LanFileMutation.OpenPrivate(partial,false)) { id=LanFileMutation.Identity(owned);stamp=FileMetadata.Stamp(owned.SafeFileHandle); }
        using(var mutation=new LanFileMutation(f.Root,"A.wav",true)) {
            await mutation.Stage(source,Stamp("complete"),8,op,default,id,stamp);mutation.Commit(op,false,a=>a());
        }
        Assert.Equal("complete",File.ReadAllText(Path.Combine(f.Root,"A.wav")));
    }
    [Fact] public async Task BusyFileMissingParentCancellationAndBadSizeAreRecoverable()
    {
        using var f=new Fixture();var target=f.File("A.wav","original");
        using(var busy=new FileStream(target,FileMode.Open,FileAccess.ReadWrite,FileShare.Read))
        using(var mutation=new LanFileMutation(f.Root,"A.wav",false)) await Assert.ThrowsAnyAsync<IOException>(()=>mutation.Observe(1024,default,true));
        using(var mutation=new LanFileMutation(f.Root,"Absent/A.wav",false)) Assert.Null(await mutation.Observe(1024,default));
        using(var mutation=new LanFileMutation(f.Root,"A.wav",true)) {
            Assert.Equal(Stamp("original"),await mutation.Observe(1024,default,true));
            var source=f.File("upload.part","bad");await Assert.ThrowsAnyAsync<IOException>(()=>mutation.Stage(source,Stamp("good"),3,Guid.NewGuid().ToString("N"),default));
        }
        Assert.Equal("original",File.ReadAllText(target));Assert.Empty(Directory.EnumerateFiles(f.Root,".jeek-sync-*"));
        Assert.ThrowsAny<IOException>(()=>new LanFileMutation(f.Root,"../escape.wav",true));
    }
}
