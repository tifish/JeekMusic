using System.Text.Json.Nodes;
using Avalonia.Headless;
using JeekMusic.Core.Preferences;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class PreferenceHeadTests
{
    [Fact] public async Task LegacyRegressionRequiresOwnerReviewStaysPrivateAndNeverTrainsOrCrossesProfiles()
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(async()=> {
            var root=Path.Combine(Path.GetTempPath(),"jeek-private-regression-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                using var app=new AppServices(new SettingsStore(Path.Combine(root,"settings.json")),new LibraryDb(":memory:"),source=>new PlayerService(source,o=>new OutputModeSwitchTests.FakeEngine(o),new OutputModeSwitchTests.FakeEndpointVolume()));
                var folder=app.Db.AddFolder("synthetic","\\fixture\\",root+Path.DirectorySeparatorChar,"Synthetic"); var records=new JsonArray();
                for(int i=0;i<11;i++)
                {
                    var file="fixture"+i+".mp3"; var path=Path.Combine(root,file); File.WriteAllText(path,"synthetic content "+i); var stat=new FileInfo(path); var key="synthetic:"+i;
                    using(var tx=app.Db.Begin()) { app.Db.ReplaceFile(tx,folder,"",file,[new Track { FolderId=folder,TrackKey=key,FileName=file,Dir="",Title="Fixture "+i,ArtistDisplay="Synthetic "+i,Artists=["Synthetic "+i],Album="Fixture "+i,DurationMs=180000,Mtime=stat.LastWriteTimeUtc.Ticks,Size=stat.Length }]); tx.Commit(); }
                    records.Add((JsonNode)new JsonObject { ["recording_key_sha256"]=Hash(key),["label"]=i%2==0?"SPORT":"NON_SPORT",["durationMs"]=180000,["reason"]="synthetic import fixture",["verified"]=new JsonObject { ["automatic"]=new JsonObject { ["MOOD"]="NON_SPORT" } } });
                }
                app.Library.RebuildSnapshot(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var pathOfFeedback=Path.Combine(root,"feedback.json"); File.WriteAllText(pathOfFeedback,new JsonObject { ["records"]=records }.ToJsonString());
                var p=app.Preferences.CreateProfile("Synthetic owner");
                await Assert.ThrowsAsync<ArgumentException>(()=>app.Preferences.ImportWorkoutRegression(pathOfFeedback,false));
                Assert.Empty(app.Preferences.Store.Read(p,"answer"));
                var result=await app.Preferences.ImportWorkoutRegression(pathOfFeedback,true); Assert.Equal(0,result["trainingRowsAdded"]!.GetValue<int>());
                Assert.Equal(11,app.Preferences.Store.Current(p,"answer").Count); Assert.All(app.Preferences.Store.Current(p,"answer"),e=>Assert.False(UsableAnswer(e)));
                var other=app.Preferences.CreateProfile("Another synthetic owner"); Assert.Empty(app.Preferences.Store.Read(other,"answer"));
                Assert.DoesNotContain(typeof(JeekMusic.Preferences.PreferenceService).Assembly.GetManifestResourceNames(),n=>n.Contains("PreferenceExposure"));
            }
            finally { Directory.Delete(root,true); }
            return 0;
        },CancellationToken.None);
    }
    [Fact] public async Task CrossPathBindingRequiresExactBytesRatherThanMatchingTitle()
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(async()=> {
            var root=Path.Combine(Path.GetTempPath(),"jeek-binding-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {
                using var app=new AppServices(new SettingsStore(Path.Combine(root,"settings.json")),new LibraryDb(":memory:"),source=>new PlayerService(source,o=>new OutputModeSwitchTests.FakeEngine(o),new OutputModeSwitchTests.FakeEndpointVolume()));
                var folder=app.Db.AddFolder("fixture","\\fixture\\",root+Path.DirectorySeparatorChar,"Synthetic");
                foreach(var name in new[] { "renamed","wrong" }) {
                    var file=name+".mp3"; File.WriteAllText(Path.Combine(root,file),name=="renamed"?"exact synthetic bytes":"different synthetic bytes"); var stat=new FileInfo(Path.Combine(root,file));
                    using var tx=app.Db.Begin(); app.Db.ReplaceFile(tx,folder,"",file,[new Track { FolderId=folder,TrackKey=name,FileName=file,Dir="",Title="Same displayed title",ArtistDisplay="Fixture",Artists=["Fixture"],Album="Fixture",DurationMs=180000,Mtime=stat.LastWriteTimeUtc.Ticks,Size=stat.Length }]); tx.Commit();
                }
                app.Library.RebuildSnapshot(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var s=app.Preferences; var p=s.CreateProfile("Synthetic binding"); var signature=Hash("exact synthetic bytes"); var id=Hash("sha256:"+signature+":whole"); var missing=Hash("sha256:"+Hash("absent bytes")+":whole");
                foreach(var pair in new[] { (id,signature),(missing,Hash("absent bytes")) }) s.Store.Append(p,pair.Item1,"recording",new JsonObject { ["recordingId"]=pair.Item1,["contentSignature"]=pair.Item2,["durationMs"]=180000,["title"]="Same displayed title" });
                s.Store.Append(p,"batch","manifest",new JsonObject { ["dimension"]="MOOD",["items"]=Strings([id,missing]),["role"]="training" });
                Assert.Null(s.FindLocal(id)); var result=await s.MatchLocalBatch(); Assert.Equal(1,result["matched"]!.GetValue<int>());
                Assert.Equal("renamed",s.FindLocal(id)!.TrackKey); Assert.Null(s.FindLocal(missing));
            } finally { Directory.Delete(root,true); }
            return 0;
        },CancellationToken.None);
    }
    [Fact] public async Task OneDimensionWithdrawalKeepsOtherCandidateHeadsAndMissingLabelsEmpty()
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(()=> {
            var settings=new SettingsStore(Path.Combine(Path.GetTempPath(),"jeek-pref-"+Guid.NewGuid(),"settings.json"));
            using var app=new AppServices(settings,new LibraryDb(":memory:"),source=>new PlayerService(source,o=>new OutputModeSwitchTests.FakeEngine(o),new OutputModeSwitchTests.FakeEndpointVolume()));
            var s=app.Preferences; var p=s.CreateProfile("Synthetic independent heads"); var id=new string('a',64);
            s.Store.Append(p,id,"recording",new JsonObject { ["contentSignature"]=new string('b',64),["title"]="Fixture",["artists"]=Strings(["Artist"]) });
            s.Store.Append(p,"batch","manifest",new JsonObject { ["dimension"]="MOOD",["items"]=Strings([id]),["role"]="training",["state"]="pilot" });
            foreach(var d in Dimensions) { s.SelectDimension(d); s.Batch(); s.Store.Append(p,"model:"+d,"model",new JsonObject { ["dimension"]=d,["state"]="development_required",["enabled"]=false }); }
            s.SelectDimension("CALMNESS"); s.Answer(id,"WITHDRAWN","unclear");
            foreach(var e in s.Store.Current(p,"model")) Assert.Equal(Dimension(e["payload"])=="CALMNESS"?"invalidated":"development_required",S(e["payload"]?["state"]));
            Assert.Single(s.Store.Current(p,"answer"));
            s.SelectDimension("VOICE"); Assert.Equal("",S(s.Batch()["items"]![0]?["answer"]));
            Assert.Throws<ArgumentException>(()=>s.Answer(id,"SPORT","whole_recording_familiar"));
            return 0;
        },CancellationToken.None);
    }
    [Fact] public async Task FirstDevelopmentAnswerPreservesCandidateButRevisionInvalidatesOnlyItsHead()
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(()=> {
            using var app=new AppServices(new SettingsStore(Path.Combine(Path.GetTempPath(),"jeek-dev-"+Guid.NewGuid(),"settings.json")),new LibraryDb(":memory:"),source=>new PlayerService(source,o=>new OutputModeSwitchTests.FakeEngine(o),new OutputModeSwitchTests.FakeEndpointVolume()));
            var s=app.Preferences; var p=s.CreateProfile("Synthetic development revision"); var id=new string('a',64);
            s.Store.Append(p,id,"recording",new JsonObject { ["contentSignature"]=new string('b',64),["title"]="Fixture" });
            s.Store.Append(p,"dev","manifest",new JsonObject { ["dimension"]="VOICE",["items"]=Strings([id]),["role"]="development" });
            foreach(var d in Dimensions) s.Store.Append(p,"model:"+d,"model",new JsonObject { ["dimension"]=d,["state"]="development_required" });
            s.SelectDimension("VOICE"); s.Batch(); s.Answer(id,"UNCERTAIN","whole_recording_familiar","Synthetic saved reason");
            Assert.Equal("whole_recording_familiar",S(s.Batch()["items"]![0]?["basis"]));
            Assert.Equal("Synthetic saved reason",S(s.Batch()["items"]![0]?["reason"]));
            Assert.Equal("",S(s.Batch(false)["items"]![0]?["reason"]));
            Assert.All(s.Store.Current(p,"model"),e=>Assert.Equal("development_required",S(e["payload"]?["state"])));
            s.Answer(id,"UNRESOLVED","unclear");
            foreach(var e in s.Store.Current(p,"model")) Assert.Equal(Dimension(e["payload"])=="VOICE"?"invalidated":"development_required",S(e["payload"]?["state"]));
            return 0;
        },CancellationToken.None);
    }
    [Fact] public void SourcePriorityKeepsManualAndClassicalTitleContractAndIndependentFallbacks()
    {
        Assert.Equal("TITLE_RULE",PersonalTagPolicy.Resolve("GENRE","NON_CLASSICAL","NON_CLASSICAL",true,classicalTitle:true).Source);
        Assert.Equal("NON_CLASSICAL",PersonalTagPolicy.Resolve("GENRE","CLASSICAL","CLASSICAL",true,"NON_CLASSICAL",true).Value);
        Assert.Equal("PERSONAL",PersonalTagPolicy.Resolve("VOICE","INSTRUMENTAL","VOCAL",true).Source);
        Assert.Equal("PUBLIC_AUTO",PersonalTagPolicy.Resolve("CALMNESS","NON_CALM",null,true).Source);
        Assert.Null(PersonalTagPolicy.Resolve("CALMNESS",null,null,false).Value);
        Assert.Equal("NON_SPORT",PersonalTagPolicy.Resolve("MOOD","NON_SPORT",null,false).Value);
    }
}
