using System.Text.Json.Nodes;
using Avalonia.Headless;
using JeekMusic.Analysis;
using JeekMusic.Automation;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Settings;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class SingingLanguageTests
{
    [Fact]
    public async Task PairedMetadataExchangesBothSourcesAndRestoreAutoWithoutResurrection()
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(()=> {
            using var h=new PlayingVisibilityTests.Harness("Language exchange fixture");
            var track=h.Track(5); var db=h.App.Db;
            var refs=new Dictionary<string,string> { [track.TrackKey]="fixture/song.wav" };
            db.SaveLanguage(track,new("known",["en"],"AUTO",.95),"AUTO");
            db.SaveLanguage(track,SingingLanguage.Manual("CHINESE"),"MANUAL");
            var prior=db.ExportLanguages(refs);
            Assert.Equal(2,prior["items"]!["fixture/song.wav"]!.AsArray().Count);
            db.SaveLanguage(track,null,"MANUAL");
            Assert.False(db.ImportLanguages(prior,_=>track.TrackKey));
            Assert.True(db.Languages([track])[track.TrackKey].Matches("ENGLISH"));
            var remote=db.ExportLanguages(refs);
            var manual=remote["items"]!["fixture/song.wav"]!.AsArray().OfType<JsonObject>().Single(r=>r["source"]!.GetValue<string>()=="MANUAL");
            manual["state"]="known"; manual["languages"]=new JsonArray("zh","en"); manual["updated_at"]=manual["updated_at"]!.GetValue<long>()+10;
            Assert.True(db.ImportLanguages(remote,_=>track.TrackKey));
            Assert.True(db.Languages([track])[track.TrackKey].Matches("CHINESE"));
            Assert.True(db.Languages([track])[track.TrackKey].Matches("ENGLISH"));
            manual["size"]=track.Size+1; manual["updated_at"]=manual["updated_at"]!.GetValue<long>()+10;
            Assert.False(db.ImportLanguages(remote,_=>track.TrackKey));
            manual["size"]=track.Size; manual["languages"]=new JsonArray("not-a-language");
            Assert.Throws<ArgumentException>(()=>db.ImportLanguages(remote,_=>track.TrackKey));
            Assert.True(db.Languages([track])[track.TrackKey].Matches("CHINESE"));
            var conflict=db.ExportLanguages(refs);
            var equal=conflict["items"]!["fixture/song.wav"]!.AsArray().OfType<JsonObject>().Single(r=>r["source"]!.GetValue<string>()=="MANUAL");
            equal["state"]="unknown"; equal["languages"]=new JsonArray();
            Assert.True(db.ImportLanguages(conflict,_=>track.TrackKey));
            Assert.Equal("unknown",db.Languages([track])[track.TrackKey].State);
            manual["languages"]=new JsonArray("zh","en"); manual["updated_at"]=equal["updated_at"]!.GetValue<long>();
            Assert.False(db.ImportLanguages(remote,_=>track.TrackKey));
        },default);
    }
    [Theory]
    [InlineData("CHINESE",true,false,false,true)]
    [InlineData("ENGLISH",false,true,true,false)]
    [InlineData("CHINESE_ENGLISH",true,false,true,false)]
    [InlineData("OTHER",false,true,false,true)]
    [InlineData("INSTRUMENTAL",false,false,false,false)]
    [InlineData("UNKNOWN",false,false,false,false)]
    public void FiltersUseKnownVocalLanguagesAndNeverAssignUnknownToNegatives(string value,bool chinese,bool nonChinese,bool english,bool nonEnglish)
    {
        var result=SingingLanguage.Manual(value);
        Assert.Equal(chinese,result.Matches("CHINESE")); Assert.Equal(nonChinese,result.Matches("NON_CHINESE"));
        Assert.Equal(english,result.Matches("ENGLISH")); Assert.Equal(nonEnglish,result.Matches("NON_ENGLISH"));
        Assert.True(new SingingLanguage("known",["yue"],"AUTO").Matches("CHINESE"));
    }
    [Fact]
    public void PipelineUsesAudioResultsDespiteOppositeTitleAndMissingAnalysis()
    {
        Track Song(long id,string title)=>new() { Id=id,FolderId=1,TrackKey=id.ToString(),Title=title,FileName=$"{id}.flac",Dir="",ArtistDisplay="中文 English" };
        Track[] songs=[Song(1,"English title"),Song(2,"中文歌名"),Song(3,"123"),Song(4,"中文 English 未识别")];
        var folder=new LibraryFolder { Id=1,VolumeId="fixture",PathInVolume="\\",Root="Z:\\",DisplayName="Fixture" };
        var languages=new Dictionary<string,SingingLanguage> { ["1"]=SingingLanguage.Manual("CHINESE"),["2"]=SingingLanguage.Manual("ENGLISH"),["3"]=SingingLanguage.Manual("CHINESE_ENGLISH") };
        var snapshot=new LibrarySnapshot([folder],songs,new Dictionary<string,SongTags> { ["3"]=new("SPORT","VOCAL",null,false,true,false) },languages);
        var source=new QueueSource.Folder(1,"",true);
        Assert.Equal(new long[] {1,3},ListBuilder.Tracks(snapshot,source,new() { TagFilter=["CHINESE"] },null).Select(t=>t.Id));
        Assert.Equal(new long[] {2},ListBuilder.Tracks(snapshot,source,new() { TagFilter=["NON_CHINESE"] },null).Select(t=>t.Id));
        Assert.Equal(new long[] {1},ListBuilder.Tracks(snapshot,source,new() { TagFilter=["CHINESE","NON_ENGLISH","UNANNOTATED"] },null).Select(t=>t.Id));
        Assert.Equal(new long[] {3},ListBuilder.Tracks(snapshot,source,new() { TagFilter=["CHINESE","ENGLISH","VOCAL","MANUAL"] },null).Select(t=>t.Id));
        Assert.Equal(4,ListBuilder.Tracks(snapshot,source,new(),null).Count);
        Assert.Null(ListBuilder.FilterOf(snapshot,[]));
    }
    static JsonObject Sample(string? language,double probability,bool speech=true,double vocal=0)=>new() {
        ["language"]=language,["probability"]=probability,["speechConfirmed"]=speech,["vocalScore"]=vocal };
    [Fact]
    public void AudioEvidenceRequiresSpeechAndRepeatedOrStrongLanguageSupport()
    {
        var uncertain=SingingLanguageService.Aggregate(new JsonArray((JsonNode)Sample("zh",.75)));
        Assert.Equal("unknown",uncertain.State); Assert.False(uncertain.Matches("NON_ENGLISH"));
        var repeated=SingingLanguageService.Aggregate(new JsonArray((JsonNode)Sample("zh",.75),(JsonNode)Sample("zh",.7)));
        Assert.True(repeated.Matches("CHINESE"));
        var mixed=SingingLanguageService.Aggregate(new JsonArray((JsonNode)Sample("zh",.95),(JsonNode)Sample("en",.97)));
        Assert.True(mixed.Matches("CHINESE")); Assert.True(mixed.Matches("ENGLISH"));
        Assert.False(mixed.Matches("NON_CHINESE")); Assert.False(mixed.Matches("NON_ENGLISH"));
        var noSpeech=SingingLanguageService.Aggregate(new JsonArray((JsonNode)Sample("zh",.99,false),(JsonNode)Sample("en",.99,false)));
        Assert.Equal("unknown",noSpeech.State);
        var instrumental=SingingLanguageService.Aggregate(new JsonArray((JsonNode)Sample(null,0,false,-4),(JsonNode)Sample(null,0,false,-4)));
        Assert.Equal("instrumental",instrumental.State); Assert.False(instrumental.Matches("NON_CHINESE"));
    }
    [Theory] [InlineData(1000)] [InlineData(5000)] [InlineData(25000)] [InlineData(50000)] [InlineData(180000)]
    public void SamplingIsBoundedNonoverlappingAndRespectsLogicalCueTrack(long duration)
    {
        var track=new Track { TrackKey="cue",Title="",FileName="track.flac",ArtistDisplay="",Dir="",StartMs=90000,EndMs=90000+duration,DurationMs=duration };
        var windows=SingingLanguageService.Windows(track);
        Assert.InRange(windows.Count,0,3); Assert.True(windows.Sum(w=>w.EndMs-w.StartMs)<=60000);
        for(var i=0;i<windows.Count;i++) {
            Assert.InRange(windows[i].StartMs,90000,90000+duration);
            Assert.InRange(windows[i].EndMs,windows[i].StartMs+1,90000+duration);
            if(i>0) Assert.True(windows[i].StartMs>=windows[i-1].EndMs);
        }
    }
    [Fact]
    public async Task UiServiceMcpOverridesRefreshRestoreAutoAndIgnoreTitles()
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(()=> {
            using var h=new PlayingVisibilityTests.Harness("中文歌名");
            var t=h.Track(5);
            h.Vm.ToggleTag("CHINESE"); h.Wait(()=>!h.Vm.ComputingRows); Assert.Empty(h.Rows);
            h.App.Db.SaveLanguage(t,new("known",["en"],"AUTO",.95),"AUTO");
            h.App.Library.RebuildSnapshot(); h.Pump();
            Assert.Empty(h.Rows);
            h.Tools.Call("singing_language",new() { ["action"]="set",["trackIds"]=new JsonArray(t.Id),["value"]="CHINESE_ENGLISH" });
            h.Wait(()=>!h.Vm.ComputingRows && h.Rows.Count==1);
            h.Vm.ToggleTag("ENGLISH"); h.Wait(()=>!h.Vm.ComputingRows); Assert.Single(h.Rows);
            h.Vm.ToggleTag("NON_CHINESE"); h.Wait(()=>!h.Vm.ComputingRows); Assert.Empty(h.Rows);
            h.Tools.Call("singing_language",new() { ["action"]="set",["trackIds"]=new JsonArray(t.Id),["value"]="AUTO" });
            h.Wait(()=>!h.Vm.ComputingRows && h.Rows.Count==1);
            Assert.Equal("AUTO",h.App.SingingLanguages.Result(t)!.Source);
            Assert.True(h.App.SingingLanguages.Result(t)!.Matches("ENGLISH"));
            var status=h.Tools.Call("singing_language",new() { ["action"]="status",["trackIds"]=new JsonArray(t.Id) });
            Assert.Equal("en",status["tracks"]!["items"]![0]!["language"]!["languages"]![0]!.GetValue<string>());
            var filters=h.Vm.TagFilter.ToArray();
            Assert.Equal(1,h.Tools.Call("search_tracks",new() { ["tags"]=new JsonArray("NON_CHINESE") })["total"]!.GetValue<int>());
            Assert.Equal(filters,h.Vm.TagFilter);
            Assert.Throws<ArgumentException>(()=>h.Tools.Call("song_filter",new() { ["action"]="set",["tags"]=new JsonArray("CHINESE","NON_CHINESE") }));
            Assert.Throws<ArgumentException>(()=>h.Tools.Call("singing_language",new() { ["action"]="set",["trackIds"]=new JsonArray(t.Id,99999L),["value"]="CHINESE" }));
            Assert.Equal("AUTO",h.App.SingingLanguages.Result(t)!.Source);
            Assert.Throws<ArgumentException>(()=>h.Tools.Call("singing_language",new() { ["action"]="status",["force"]=true }));
            Assert.False(h.Player.IsPlaying); Assert.Empty(h.Player.Queue.Items);
            h.Vm.ClearFilterCommand.Execute(null); h.Wait(()=>!h.Vm.ComputingRows); Assert.Equal(260,h.Rows.Count);
            return 0;
        },CancellationToken.None);
    }
    [Fact]
    public void PersistedResultsRestoreAndSignatureChangesInvalidateAutomaticAndManual()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
        try {
            Track track;
            using(var db=new LibraryDb(path)) {
                var f=db.AddFolder("test","\\","Z:\\","fixture");
                using(var tx=db.Begin()) {
                    db.ReplaceFile(tx,f,"","audio.wav",[new() { FolderId=f,TrackKey="fixture",Title="misleading title",FileName="audio.wav",Dir="",ArtistDisplay="",Mtime=42,Size=100,DurationMs=50000 }]); tx.Commit();
                }
                track=db.AllTracks().Single();
                db.SaveLanguage(track,new("known",["en"],"AUTO",.95),"AUTO");
                db.SaveLanguage(track,SingingLanguage.Manual("CHINESE"),"MANUAL");
            }
            using(var db=new LibraryDb(path)) {
                Assert.True(db.Languages([track])[track.TrackKey].Matches("CHINESE"));
                db.SaveLanguage(track,null,"MANUAL"); Assert.True(db.Languages([track])[track.TrackKey].Matches("ENGLISH"));
                db.Exec("UPDATE track SET mtime=43"); Assert.Empty(db.Languages(db.AllTracks()));
                db.Exec("DELETE FROM track"); Assert.Equal(0L,db.Scalar("SELECT count(*) FROM track_language"));
            }
        } finally { File.Delete(path); }
    }
}
