using JeekMusic.Core.Sync;
using JeekMusic.Library;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

public sealed class LanManifestCacheTests
{
    [Fact] public void WatchRevisionRootIdentityAndFallbackAllBoundReuse()
    {
        var cache=new LanManifestCache();LanFiles.Entry[] files=[new("Album/A.wav",123,456,"")];
        cache.Store(1,"root","path",[""],"epoch:1",true,0,files);
        Assert.True(cache.TryGet("root","path",[""],"epoch:1",true,1,out var reused));Assert.Same(files,reused);
        Assert.False(cache.TryGet("root","other-path",[""],"epoch:1",true,2,out _));
        Assert.False(cache.TryGet("root","path",[""],"epoch:2",true,2,out _));
        Assert.False(cache.TryGet("root","path",[""],"epoch:1",false,2,out _));
        Assert.False(cache.TryGet("root","path",[""],"epoch:1",true,LanManifestCache.LifetimeMs,out _));
    }
    [Fact] public void FullRootCacheKeepsOnlySelectedPathsAndTheirCueDependencies()
    {
        var cache=new LanManifestCache();LanFiles.Entry[] files=[new("Selected/A.cue",1,2,"",["Other/A.wav"]),new("Other/A.wav",3,4,""),new("Private/Skip.wav",5,6,"")];
        cache.Store(1,"root","path",[""],"epoch:1",true,0,files);
        Assert.True(cache.TryGet("root","path",["Selected"],"epoch:1",true,1,out var selected));
        Assert.Equal(["Selected/A.cue","Other/A.wav"],selected.Select(e=>e.Path));
        cache.Invalidate(1);Assert.False(cache.TryGet("root","path",["Selected"],"epoch:1",true,2,out _));
    }
    [Fact] public async Task RealWatcherInvalidatesBeforeItsDebounceIncludingIgnoredLibraryWrites()
    {
        var path=Path.Combine(Path.GetTempPath(),"jeek-cache-watch-"+Guid.NewGuid());Directory.CreateDirectory(path);
        try {
            using var watcher=new FolderWatcher();
            var changed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Watch(new LibraryFolder { Id=1,Root=path+Path.DirectorySeparatorChar,VolumeId="test",PathInVolume="\\",DisplayName="Synthetic watcher" });
            Assert.True(watcher.IsWatching(1));
            watcher.MetadataChanged+=_=>changed.TrySetResult();
            var file=Path.Combine(path,"A.wav");watcher.Ignore(file);File.WriteAllBytes(file,[1]);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            watcher.Unwatch(1);Assert.False(watcher.IsWatching(1));
        } finally { Directory.Delete(path,true); }
    }
    sealed class Fixture:IDisposable {
        public string Home=Path.Combine(Path.GetTempPath(),"jeek-pc-index-"+Guid.NewGuid().ToString("N"));
        public string Root=>Path.Combine(Home,"music");
        public Fixture()=>Directory.CreateDirectory(Root);
        public void Put(string path,int bytes) { var full=Path.Combine(Root,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);File.WriteAllBytes(full,new byte[bytes]); }
        public void Dispose()=>Directory.Delete(Home,true);
    }
    [Fact] public async Task OnlyDirtyLeavesChangeAndAddRenameDeleteMatchesAFreshManifest() {
        using var f=new Fixture();f.Put("A.wav",10);f.Put("B.wav",20);
        var cache=new LanManifestCache();var first=await LanFiles.Manifest(f.Root,[""]);
        cache.Store(1,"root",f.Root,[""],"v1",true,0,first);
        f.Put("A.wav",30);f.Put("C.wav",40);File.Move(Path.Combine(f.Root,"B.wav"),Path.Combine(f.Root,"Renamed.wav"));
        foreach(var path in new[] { "A.wav","C.wav","B.wav","Renamed.wav" })cache.Changed(1,path,false);
        Assert.True(cache.TryRefresh(1,"root",f.Root,[""],"v2",true,1,default,out var updated,out var refreshed,out var shared));
        Assert.Equal(4,refreshed);Assert.Equal(0,shared);
        Assert.Equal(await LanFiles.Manifest(f.Root,[""]),updated);
        Assert.True(cache.TryGet("root",f.Root,[""],"v2",true,2,out _));
        File.Delete(Path.Combine(f.Root,"C.wav"));cache.Changed(1,"C.wav",false);
        Assert.True(cache.TryRefresh(1,"root",f.Root,[""],"v3",true,3,default,out updated,out _,out _));Assert.Equal(await LanFiles.Manifest(f.Root,[""]),updated);
    }
    [Fact] public async Task ActualLibraryScannerSharesExactFileInfoWithoutASecondMetadataRead() {
        using var f=new Fixture();f.Put("A.wav",64);f.Put("cover.jpg",12);
        using var db=new LibraryDb(Path.Combine(f.Home,"library.sqlite"));db.AddFolder("synthetic","\\",f.Root+Path.DirectorySeparatorChar,"Synthetic");
        var folder=db.Folders().Single();var cache=new LanManifestCache();
        cache.Store(folder.Id,"root",f.Root,[""],"v1",true,0,await LanFiles.Manifest(f.Root,[""]));
        f.Put("A.wav",65);cache.Changed(folder.Id,"A.wav",false);
        var scanner=new Scanner(db,cache);scanner.Scan(folder,new(new(JeekMusic.Core.Library.ArtistSplitConfig.Default,false,JeekMusic.Core.Library.FileNameFormat.ARTIST_TITLE,null)),null,false,default);
        Assert.True(cache.TryRefresh(folder.Id,"root",f.Root,[""],"v2",true,1,default,out var updated,out var refreshed,out var shared));
        Assert.Equal(1,refreshed);Assert.Equal(1,shared);Assert.Equal(65,updated.Single(e=>e.Path=="A.wav").Size);
    }
    [Fact] public async Task CueDependencyDirectoriesRequireFullReconciliationButUnrelatedSongsDoNot() {
        using var f=new Fixture();f.Put("Other/A.wav",10);f.Put("Cue/disc.wav",20);
        var cache=new LanManifestCache();
        LanFiles.Entry[] files=[new("Cue/album.cue",1,2,"",["Cue/disc.wav"]),new("Cue/disc.wav",20,2,""),new("Other/A.wav",10,2,"")];
        cache.Store(1,"root",f.Root,[""],"v1",true,0,files);
        f.Put("Other/A.wav",30);cache.Changed(1,"Other/A.wav",false);
        Assert.True(cache.TryRefresh(1,"root",f.Root,[""],"v2",true,1,default,out _,out _,out _));
        cache.Changed(1,"Cue/cover.jpg",false);
        Assert.False(cache.TryRefresh(1,"root",f.Root,[""],"v3",true,2,default,out _,out _,out _));
        cache.Invalidate(1);Assert.False(cache.TryGet("root",f.Root,[""],"v1",true,2,out _));
        var version=cache.Version(1);cache.Invalidate(1);
        cache.Store(1,"root",f.Root,[""],"v4",true,3,files,version);
        Assert.Equal(0,cache.Diagnostics()["entries"]!.GetValue<int>());
        await Task.CompletedTask;
    }
    [Fact] public async Task RealPathWatcherRetainsInventoryForLeafChangesAndDropsItForDirectoryMoves() {
        using var f=new Fixture();f.Put("A.wav",10);
        using var watcher=new FolderWatcher();var cache=new LanManifestCache();
        watcher.PathMetadataChanged+=cache.Changed;watcher.MetadataInvalidated+=cache.Invalidate;
        watcher.Watch(new LibraryFolder { Id=1,Root=f.Root+Path.DirectorySeparatorChar,VolumeId="test",PathInVolume="\\",DisplayName="Synthetic" });
        cache.Store(1,"root",f.Root,[""],"v1",true,0,await LanFiles.Manifest(f.Root,[""]));
        var change=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.PathMetadataChanged+=(_,_,_,_)=>change.TrySetResult();
        f.Put("A.wav",20);await change.Task.WaitAsync(TimeSpan.FromSeconds(5));await Task.Delay(150);
        Assert.True(cache.TryRefresh(1,"root",f.Root,[""],"v2",watcher.IsWatching(1),1,default,out var updated,out var count,out _));
        Assert.Equal(1,count);Assert.Equal(20,Assert.Single(updated).Size);
        Directory.CreateDirectory(Path.Combine(f.Root,"New"));
        await Task.Delay(150);Assert.Equal(0,cache.Diagnostics()["entries"]!.GetValue<int>());
    }
    [Fact] public async Task FullRootCannotTurnDifferentSelectionCasingIntoAnEmptyDirectory() {
        using var f=new Fixture();f.Put("Chosen/A.wav",10);var cache=new LanManifestCache();
        cache.Store(1,"root",f.Root,[""],"v1",true,0,await LanFiles.Manifest(f.Root,[""]));
        Assert.False(cache.TryGet("root",f.Root,["chosen"],"v1",true,1,out _));
        Assert.False(cache.TryRefresh(1,"root",f.Root,["chosen"],"v1",true,1,default,out _,out _,out _));
        Assert.Single(await LanFiles.Manifest(f.Root,["chosen"]));
    }
    [Fact] public void LibraryObservationsCannotIntroduceLeavesAbsentFromTheSafeInventory() {
        var cache=new LanManifestCache();LanFiles.Entry[] files=[new("A.wav",10,100,"")];
        cache.Store(1,"root","path",[""],"v1",true,0,files);
        var ticket=cache.BeginLibraryScan(1,"path");
        cache.ObserveLibraryFile(ticket,"Junction/Outside.wav",DateTime.UnixEpoch.Ticks,20);
        Assert.Equal(0,cache.Diagnostics()["sharedEstimatedBytes"]!.GetValue<long>());
        Assert.True(cache.TryGet("root","path",[""],"v1",true,1,out var original));Assert.Same(files,original);
        cache.Invalidate(1);cache.ObserveLibraryFile(ticket,"A.wav",DateTime.UnixEpoch.Ticks,10);
        Assert.Equal(0,cache.Diagnostics()["sharedEstimatedBytes"]!.GetValue<long>());
    }
    [Fact] public void UnchangedCueObservationsDoNotInvalidateTheWholeInventoryAgain() {
        var cache=new LanManifestCache();LanFiles.Entry[] files=[new("A.cue",10,100,"",["A.wav"]),new("A.wav",20,100,"")];
        cache.Store(1,"root","path",[""],"v1",true,0,files);var ticket=cache.BeginLibraryScan(1,"path");
        cache.ObserveLibraryFile(ticket,"A.cue",DateTime.UnixEpoch.Ticks+1,10);
        cache.ObserveLibraryFile(ticket,"A.wav",DateTime.UnixEpoch.Ticks+1,20);
        Assert.True(cache.TryGet("root","path",[""],"v1",true,1,out _));
        Assert.Equal(0,cache.Diagnostics()["sharedEstimatedBytes"]!.GetValue<long>());
    }
}
