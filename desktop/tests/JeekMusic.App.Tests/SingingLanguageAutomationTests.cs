using Avalonia.Headless;
using Avalonia.Threading;
using JeekMusic.Analysis;
using JeekMusic.Library;
using JeekMusic.Settings;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class SingingLanguageAutomationTests
{
    static async Task Run(Action test)
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(test,default);
    }

    [Fact]
    public Task AutomaticStartupFillsOnlyMissingResultsAndPicksUpNewRecordings()=>Run(()=> {
        using var h=new Fixture();
        h.Db.SaveLanguage(h.Tracks[0],SingingLanguage.Manual("CHINESE"),"MANUAL");
        h.Db.SaveLanguage(h.Tracks[1],new("unknown",[],"AUTO"),"AUTO");
        h.Db.SaveLanguage(h.Tracks[2],new("instrumental",[],"AUTO"),"AUTO");
        var calls=0;
        using var service=h.Service((_,_)=> { Interlocked.Increment(ref calls);return Task.FromResult(new SingingLanguage("known",["en"],"AUTO")); });
        Assert.Null(service.ReconcileAutomatic(AnalysisMode.Manual,true,false));
        Assert.Null(service.ReconcileAutomatic(AnalysisMode.PluggedIn,false,false));
        Assert.Null(service.ReconcileAutomatic(AnalysisMode.Always,true,true));
        Assert.Equal(0,calls);
        Assert.False(service.Busy);
        service.ReconcileAutomatic(AnalysisMode.PluggedIn,true,false);
        h.Wait(()=>!service.Busy);
        Assert.Equal(1,calls);
        Assert.Equal("MANUAL",service.Result(h.Tracks[0])!.Source);
        Assert.Equal("unknown",service.Result(h.Tracks[1])!.State);
        Assert.Equal("instrumental",service.Result(h.Tracks[2])!.State);
        Assert.Equal(0,service.Read()["libraryCoverage"]!["pendingTracks"]!.GetValue<int>());
        Assert.Null(service.ReconcileAutomatic(AnalysisMode.Always,false,false));
        Assert.Equal("complete",service.Read()["automation"]!["reason"]!.GetValue<string>());
        h.AddTrack();h.Library.RebuildSnapshot();h.Pump();
        service.ReconcileAutomatic(AnalysisMode.Always,false,false);
        h.Wait(()=>!service.Busy);
        Assert.Equal(2,calls);
        Assert.Equal(0,service.Read()["libraryCoverage"]!["pendingTracks"]!.GetValue<int>());
    });

    [Fact]
    public Task PolicySuspensionResumesButExplicitCancelHoldsUntilManualStartOrRestart()=>Run(()=> {
        using var h=new Fixture();
        var entered=0;
        var block=true;
        using var service=h.Service(async(_,ct)=> {
            Interlocked.Increment(ref entered);
            if(block) await Task.Delay(Timeout.Infinite,ct);
            return new("known",["en"],"AUTO");
        });
        service.ReconcileAutomatic(AnalysisMode.Always,true,false);h.Wait(()=>entered>0);
        service.ReconcileAutomatic(AnalysisMode.Always,true,true);h.Wait(()=>!service.Busy);
        Assert.Equal("paused",service.Read()["state"]!.GetValue<string>());
        Assert.False(service.Read()["automation"]!["suppressedUntilRestart"]!.GetValue<bool>());
        service.ReconcileAutomatic(AnalysisMode.Always,true,false);h.Wait(()=>entered>1);
        service.Cancel();h.Wait(()=>!service.Busy);
        block=false;
        service.ReconcileAutomatic(AnalysisMode.Always,true,false);h.Pump();
        Assert.False(service.Busy);Assert.Equal(2,entered);
        Assert.Equal("cancelled_by_user",service.Read()["automation"]!["reason"]!.GetValue<string>());
        service.Start(h.Tracks,downloadConfirmed:true);h.Wait(()=>!service.Busy);
        Assert.Equal(6,entered);
        Assert.False(service.Read()["automation"]!["suppressedUntilRestart"]!.GetValue<bool>());
    });

    [Fact]
    public Task RestartReconstructsQueueFromDurableResultsAndRetainsManualCorrections()=>Run(()=> {
        using var h=new Fixture();
        using(var first=h.Service(async(t,ct)=> {
            if(t.Id!=h.Tracks[0].Id) await Task.Delay(Timeout.Infinite,ct);
            return new("known",["en"],"AUTO");
        })) {
            first.ReconcileAutomatic(AnalysisMode.Always,true,false);
            h.Wait(()=>first.Result(h.Tracks[0]) is not null && first.Read()["currentTrackId"]?.GetValue<long>()==h.Tracks[1].Id);
            first.Cancel();h.Wait(()=>!first.Busy);
        }
        h.Db.SaveLanguage(h.Tracks[2],SingingLanguage.Manual("CHINESE"),"MANUAL");
        var ids=new List<long>();
        using var second=h.Service((t,_)=> { ids.Add(t.Id);return Task.FromResult(new SingingLanguage("unknown",[],"AUTO")); });
        second.ReconcileAutomatic(AnalysisMode.Always,true,false);h.Wait(()=>!second.Busy);
        Assert.Equal(new[] { h.Tracks[1].Id,h.Tracks[3].Id },ids);
        Assert.True(second.Read()["resumesAfterRestart"]!.GetValue<bool>());
        Assert.Equal("MANUAL",second.Result(h.Tracks[2])!.Source);
        Assert.Equal(0,second.Read()["libraryCoverage"]!["pendingTracks"]!.GetValue<int>());
    });

    [Fact]
    public Task FailedRecordingBacksOffWithoutBlockingOtherTracksAndChangedVersionCanRetry()=>Run(()=> {
        using var h=new Fixture();var failed=h.Tracks[0];var count=0;
        using var service=h.Service((t,_)=> {
            Interlocked.Increment(ref count);
            if(t.Id==failed.Id) throw new IOException("fixture failure");
            return Task.FromResult(new SingingLanguage("unknown",[],"AUTO"));
        });
        service.ReconcileAutomatic(AnalysisMode.Always,true,false);h.Wait(()=>!service.Busy);
        Assert.Equal(4,count);
        Assert.Equal(1,service.Read()["libraryCoverage"]!["pendingTracks"]!.GetValue<int>());
        Assert.InRange(service.ReconcileAutomatic(AnalysisMode.Always,true,false)!.Value.TotalSeconds,290,300);
        Assert.Equal(4,count);
        h.Db.Exec("UPDATE track SET duration_ms=duration_ms+1 WHERE id=$id",null,("$id",failed.Id));h.Library.RebuildSnapshot();h.Pump();
        service.ReconcileAutomatic(AnalysisMode.Always,true,false);h.Wait(()=>!service.Busy);
        Assert.Equal(5,count);
        Assert.Equal("completed_with_errors",service.Read()["state"]!.GetValue<string>());
    });

    [Fact]
    public Task ModelPreparationFailureBacksOffAndPairedResultDuringAnalysisWins()=>Run(()=> {
        using var h=new Fixture();var prepares=0;
        using(var broken=new SingingLanguageService(h.Library,h.Root,_=> {
            prepares++;throw new IOException("fixture model unavailable");
        },(_,_)=>throw new Exception("Must not analyze without model"))) {
            broken.ReconcileAutomatic(AnalysisMode.Always,true,false);h.Wait(()=>!broken.Busy);
            Assert.InRange(broken.ReconcileAutomatic(AnalysisMode.Always,true,false)!.Value.TotalSeconds,290,300);
            Assert.Equal(1,prepares);
        }
        using var service=h.Service((t,_)=> {
            h.Db.SaveLanguage(t,SingingLanguage.Manual("CHINESE"),"MANUAL");
            return Task.FromResult(new SingingLanguage("known",["en"],"AUTO"));
        });
        service.ReconcileAutomatic(AnalysisMode.Always,true,false);h.Wait(()=>!service.Busy);
        Assert.All(h.Tracks,t=>Assert.Equal("MANUAL",service.Result(t)!.Source));
        Assert.Equal(0L,h.Db.Scalar("SELECT count(*) FROM track_language WHERE source='AUTO'"));
    });

    sealed class Fixture : IDisposable
    {
        public string Root { get; }=Path.Combine(Path.GetTempPath(),"jeek-language-auto-"+Guid.NewGuid());
        public LibraryDb Db { get; }
        public LibraryService Library { get; }
        public Track[] Tracks=>Db.AllTracks().OrderBy(t=>t.Id).ToArray();
        readonly long folder;
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Db=new LibraryDb(Path.Combine(Root,"library.db"));
            folder=Db.AddFolder("fixture","\\",Root+"\\","fixture");
            Library=new LibraryService(Db,new SettingsStore(Path.Combine(Root,"settings.json")));
            for(var i=0;i<4;i++) AddTrack();
            Library.RebuildSnapshot();Pump();
        }
        public void AddTrack()
        {
            var key=Guid.NewGuid().ToString("N");var name=key+".wav";var path=Path.Combine(Root,name);
            File.WriteAllBytes(path,[]);var info=new FileInfo(path);
            using var tx=Db.Begin();Db.ReplaceFile(tx,folder,"",name,[new() {
                FolderId=folder,TrackKey=key,FileName=name,Title=key,ArtistDisplay="",Dir="",DurationMs=100,
                Mtime=info.LastWriteTimeUtc.Ticks,Size=info.Length
            }]);tx.Commit();
        }
        public SingingLanguageService Service(Func<Track,CancellationToken,Task<SingingLanguage>> analyze)=>new(Library,Root,_=>Task.CompletedTask,analyze);
        public void Pump()=>Dispatcher.UIThread.RunJobs();
        public void Wait(Func<bool> condition)
        {
            var until=DateTime.UtcNow.AddSeconds(10);
            while(DateTime.UtcNow<until) { Pump();if(condition()) { Pump();return; }Thread.Yield(); }
            Assert.Fail("Timed out waiting for language task");
        }
        public void Dispose() { Library.Dispose();Db.Dispose();Directory.Delete(Root,true); }
    }
}
