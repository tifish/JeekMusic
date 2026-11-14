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
}
