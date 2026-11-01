using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Threading;
using JeekMusic.Analysis;
using JeekMusic.Automation;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class DailyFeedbackTests
{
    sealed class Fixture:IDisposable
    {
        public readonly string Root=Path.Combine(Path.GetTempPath(),"jeek-daily-"+Guid.NewGuid().ToString("N"));
        public AppServices App { get; }
        public Track[] Tracks { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            App=new(new SettingsStore(Path.Combine(Root,"settings.json")),new LibraryDb(":memory:"),source=>new PlayerService(source,o=>new OutputModeSwitchTests.FakeEngine(o),new OutputModeSwitchTests.FakeEndpointVolume()));
            var folder=App.Db.AddFolder("fixture","\\daily\\",Root+Path.DirectorySeparatorChar,"Daily fixture");
            for(int i=0;i<2;i++) {
                var name="song"+i+".mp3"; File.WriteAllText(Path.Combine(Root,name),"synthetic bytes "+i); var file=new FileInfo(Path.Combine(Root,name));
                using var tx=App.Db.Begin(); App.Db.ReplaceFile(tx,folder,"",name,[new Track { FolderId=folder,TrackKey="daily:"+i,Dir="",FileName=name,Title="Fixture "+i,ArtistDisplay="Artist "+i,Artists=["Artist "+i],Album="Album "+i,AlbumArtist="Artist "+i,DurationMs=180000,Mtime=file.LastWriteTimeUtc.Ticks,Size=file.Length }]); tx.Commit();
            }
            App.Library.RebuildSnapshot(); Dispatcher.UIThread.RunJobs(); Tracks=App.Db.AllTracks().ToArray();
        }
        public void Dispose() { App.Dispose(); Directory.Delete(Root,true); }
    }
    static async Task Flush(Fixture f,Func<bool> done)
    {
        for(int i=0;i<100;i++) { await f.App.Preferences.CaptureDailyFeedback(); Dispatcher.UIThread.RunJobs(); if(done()) return; await Task.Delay(20); }
        Assert.True(done(),f.App.Preferences.Status().ToJsonString());
    }
    static async Task Run(Func<Task> test) { using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication)); await session.Dispatch(async()=> { await test(); return 0; },CancellationToken.None); }
    [Fact] public Task MainBusinessBatchCorrectionsReviseWithdrawSyncAndFilterWithoutInventedListeningBasis()=>Run(async()=> {
        using var f=new Fixture(); var tools=new McpTools(f.App); var s=f.App.Preferences;
        Assert.Empty(s.Store.Current(Dataset,"answer"));
        await tools.CallAsync("set_smart_tags",new() { ["trackIds"]=new JsonArray(f.Tracks.Select(t=>(JsonNode?)JsonValue.Create(t.Id)).ToArray()),["voice"]="VOCAL",["genre"]="CLASSICAL" });
        await Flush(f,()=>s.Store.Current(Dataset,"answer").Count==4);
        Assert.All(s.Store.Current(Dataset,"answer"),e=> { Assert.Equal("explicit_daily_tag_correction",S(e["payload"]?["source"])); Assert.Equal("track_tag_feedback",S(e["payload"]?["basis"])); Assert.True(UsableAnswer(e)); });
        Assert.DoesNotContain(s.Store.Current(Dataset,"answer"),e=>Dimension(e["payload"]) is "MOOD" or "CALMNESS");
        var revisions=s.Store.Read(Dataset,"answer").Count; await s.CaptureDailyFeedback(); Assert.Equal(revisions,s.Store.Read(Dataset,"answer").Count);
        Assert.Equal(2,tools.Call("search_tracks",new() { ["onlyManual"]=true,["limit"]=1 })["total"]!.GetValue<int>());
        await tools.CallAsync("set_smart_tags",new() { ["trackIds"]=new JsonArray(f.Tracks[0].Id),["voice"]="INSTRUMENTAL" });
        await Flush(f,()=>s.Store.Current(Dataset,"answer").Any(e=>S(e["payload"]?["answer"])=="INSTRUMENTAL"));
        await tools.CallAsync("set_smart_tags",new() { ["trackIds"]=new JsonArray(f.Tracks.Select(t=>(JsonNode?)JsonValue.Create(t.Id)).ToArray()),["voice"]="AUTO" });
        await Flush(f,()=>s.Store.Current(Dataset,"answer").Count(e=>Dimension(e["payload"])=="VOICE" && S(e["payload"]?["answer"])=="WITHDRAWN")==2);
        Assert.Equal(2,TrainingRows(s.Store.Current(Dataset,"answer")).Count());
        using var peer=new JeekMusic.Preferences.PreferenceStore(":memory:","phone"); peer.AcceptPage("pc",s.Store.Page(0)); peer.AcceptPage("pc",s.Store.Page(0));
        Assert.Equal(4,peer.Current(Dataset,"answer").Count); Assert.Equal(2,TrainingRows(peer.Current(Dataset,"answer")).Count());
        await tools.CallAsync("set_smart_tags",new() { ["trackIds"]=new JsonArray(f.Tracks.Select(t=>(JsonNode?)JsonValue.Create(t.Id)).ToArray()),["genre"]="AUTO" });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0,tools.Call("search_tracks",new() { ["onlyManual"]=true })["total"]!.GetValue<int>());
    });
    [Fact] public Task AutoWithdrawsOnlyDailySourceAndClearNeverImportsManualHistoryAgain()=>Run(async()=> {
        using var f=new Fixture(); var s=f.App.Preferences;
        await f.App.SmartTags.Override([f.Tracks[0]],new Dictionary<JeekMusic.Core.Analysis.TagDimension,JeekMusic.Core.Analysis.Tag?> { [JeekMusic.Core.Analysis.TagDimension.MOOD]=JeekMusic.Core.Analysis.Tag.SPORT });
        await Flush(f,()=>s.Store.Current(Dataset,"answer").Count==1);
        var daily=s.Store.Current(Dataset,"answer").Single(); var payload=daily["payload"]!.DeepClone().AsObject(); payload["source"]="explicit_human_blind_batch"; payload["basis"]="whole_recording_familiar";
        s.Store.Append(Dataset,"separate-blind-answer","answer",payload);
        await f.App.SmartTags.Override([f.Tracks[0]],new Dictionary<JeekMusic.Core.Analysis.TagDimension,JeekMusic.Core.Analysis.Tag?> { [JeekMusic.Core.Analysis.TagDimension.MOOD]=null });
        await Flush(f,()=>s.Store.Current(Dataset,"answer").Any(e=>S(e["payload"]?["answer"])=="WITHDRAWN"));
        Assert.Single(TrainingRows(s.Store.Current(Dataset,"answer"))); Assert.Equal("SPORT",S(s.Store.Current(Dataset,"answer").Single(e=>S(e["entityId"])=="separate-blind-answer")["payload"]?["answer"]));
        s.DeleteProfile(); await s.CaptureDailyFeedback(); Assert.Empty(s.Store.Current(Dataset,"answer"));
        await f.App.SmartTags.Override([f.Tracks[1]],new Dictionary<JeekMusic.Core.Analysis.TagDimension,JeekMusic.Core.Analysis.Tag?> { [JeekMusic.Core.Analysis.TagDimension.CALMNESS]=JeekMusic.Core.Analysis.Tag.CALM });
        await Flush(f,()=>s.Store.Current(Dataset,"answer").Count==1); Assert.Equal("CALMNESS",Dimension(s.Store.Current(Dataset,"answer").Single()["payload"]));
    });
    [Fact] public Task OfflineFeedbackDoesNotBlockManualSaveAndRetriesExactRecording()=>Run(async()=> {
        using var f=new Fixture(); var t=f.Tracks[0]; var path=Path.Combine(f.Root,t.FileName); var bytes=File.ReadAllBytes(path); File.Delete(path);
        await f.App.SmartTags.Override([t],new Dictionary<JeekMusic.Core.Analysis.TagDimension,JeekMusic.Core.Analysis.Tag?> { [JeekMusic.Core.Analysis.TagDimension.VOICE]=JeekMusic.Core.Analysis.Tag.VOCAL });
        await Flush(f,()=>f.App.Preferences.Status()["dailyFeedback"]!["pending"]!.GetValue<int>()==1);
        Assert.True(f.App.SmartTags.Read()[t.TrackKey].VoiceManual); Assert.Empty(f.App.Preferences.Store.Current(Dataset,"answer"));
        File.WriteAllBytes(path,bytes); File.SetLastWriteTimeUtc(path,new DateTime(t.Mtime,DateTimeKind.Utc));
        await Flush(f,()=>f.App.Preferences.Store.Current(Dataset,"answer").Count==1);
    });
}
