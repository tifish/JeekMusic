using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using JeekMusic.Core.Preferences;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Preferences;
using JeekMusic.Settings;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.App.Tests;

/// <summary>Isolated contract fixtures only. Receipts here simulate trusted evidence; no accuracy claim.</summary>
[Collection("Avalonia UI")]
public sealed class PreferenceDeploymentTests
{
    static async Task Run(Func<AppServices,Task> test)
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(async()=> {
            using var app=new AppServices(new SettingsStore(Path.Combine(Path.GetTempPath(),"jeek-learning-"+Guid.NewGuid(),"settings.json")),new LibraryDb(":memory:"),source=>new PlayerService(source,o=>new OutputModeSwitchTests.FakeEngine(o),new OutputModeSwitchTests.FakeEndpointVolume()));
            await test(app); return 0;
        },CancellationToken.None);
    }
    static string PrivateHash(PreferenceTraining t,string name,params object[] args)=>(string)t.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(t,args)!;
    static JsonObject Artifact(string dimension)
    {
        var vector=new JsonArray(Enumerable.Range(0,512).Select(i=>(JsonNode?)JsonValue.Create(i==0?1d:0d)).ToArray());
        return new JsonObject { ["state"]="training_gate_passed",["profileId"]=Dataset,["dimension"]=dimension,["extractor"]=Extractor,["margin"]=HeadContract()["heads"]![dimension]!["margin"]!.DeepClone(),["trainingSetHash"]=Hash("isolated simulated training"),["headProtocolHash"]=Hash(HeadContract().ToJsonString()),
            ["workerHash"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Preferences","Runtime","preference_worker.py")))),
            ["scope"]="whole_recording",["analyzerVersion"]=6,["publicRulesVersion"]=9,["model"]=new JsonObject { ["center"]=new JsonArray(Enumerable.Range(0,512).Select(_=>(JsonNode?)JsonValue.Create(0d)).ToArray()),["axes"]=new JsonArray(Enumerable.Range(0,512).Select(i=>(JsonNode)new JsonArray(i==0?1d:0d,i==1?1d:0d)).ToArray()),["scale"]=new JsonArray(1d,1d),["coef"]=new JsonArray(1d,0d),["intercept"]=0d },
            ["deploymentProbes"]=new JsonArray(Enumerable.Range(0,12).Select(_=>(JsonNode)new JsonObject { ["vector"]=vector.DeepClone(),["score"]=1d,["decision"]=Labels(dimension).Positive }).ToArray()) };
    }
    static void InstallFixture(AppServices app,string d,bool receipts=true)
    {
        var t=app.Preferences.Training; var s=app.Preferences.Store; var artifact=Artifact(d); var hash=S(artifact["trainingSetHash"]);
        if(!s.Current(Dataset,"manifest").Any(e=>S(e["entityId"])=="study-v1"))
        {
            for(int i=0;i<44;i++) s.Append(Dataset,Hash("isolated-scope:"+i),"recording",new JsonObject { ["contentSignature"]=Hash("isolated-content:"+i),["workGroup"]="fixture-work:"+i,["albumGroup"]="fixture-release:"+i,["artists"]=Strings(["fixture-artist:"+i]),["identityReviewed"]=true,["supported"]=true });
            app.Preferences.SelectDimension("VOICE"); app.Preferences.FreezeStudy();
        }
        var root=(string)t.GetType().GetField("root",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(t)!;
        Directory.CreateDirectory(Path.Combine(root,"candidates")); File.WriteAllText(Path.Combine(root,"candidates",hash+".json"),artifact.ToJsonString());
        s.Append(Dataset,"candidate:"+d,"model",new JsonObject { ["dimension"]=d,["state"]="pass_research_gate",["trainingSetHash"]=hash });
        if(receipts) foreach(var stage in d=="MOOD"?new[]{"development","calibration_regression","sealed_test"}:new[]{"development","sealed_test"})
            s.SetLocal("passed:"+d+":"+stage,new JsonObject { ["trainingHash"]=hash,["artifactHash"]=Hash(artifact.ToJsonString()),["dependencies"]=PrivateHash(t,"ReceiptDependencies",d,stage),["resultHash"]=Hash("isolated engineering fixture") }.ToJsonString());
    }
    [Fact] public Task MetadataAndTrainingSuccessAloneNeverActivate()=>Run(app=> {
        InstallFixture(app,"VOICE",false);
        Assert.False(app.Preferences.Training.Enabled("VOICE"));
        Assert.Throws<InvalidOperationException>(()=>app.Preferences.Training.Activate("VOICE"));
        app.Preferences.Store.SetLocal("syntheticFixture","1");
        Assert.Throws<InvalidOperationException>(()=>app.Preferences.Training.Activate("VOICE")); return Task.CompletedTask;
    });
    [Fact] public Task TrustedReceiptFixtureActivatesAtomicallyAndSingleDimensionInvalidationPreservesOtherHead()=>Run(app=> {
        var t=app.Preferences.Training; InstallFixture(app,"VOICE"); app.Preferences.Store.Append(Dataset,"job:VOICE","model",new JsonObject { ["dimension"]="VOICE",["category"]="job",["state"]="pass_research_gate" }); t.Activate("VOICE"); InstallFixture(app,"CALMNESS"); t.Activate("CALMNESS");
        Assert.True(t.Enabled("VOICE")); Assert.True(t.Enabled("CALMNESS"));
        var before=app.Db.TagOverrides().Count; t.Invalidate("VOICE"); app.Preferences.Notify();
        Assert.False(t.Enabled("VOICE")); Assert.True(t.Enabled("CALMNESS")); Assert.Equal(before,app.Db.TagOverrides().Count);
        var page=t.ResultPage(); Assert.DoesNotContain("center",page.ToJsonString()); Assert.DoesNotContain("vector",page.ToJsonString());
        app.Preferences.DeleteProfile(); Assert.False(t.Enabled("CALMNESS")); Assert.Empty(app.Preferences.Store.Read(Dataset)); return Task.CompletedTask;
    });
    [Fact] public Task ChangedArtifactAndStaleEvidenceFailClosed()=>Run(app=> {
        InstallFixture(app,"GENRE"); var t=app.Preferences.Training; var s=app.Preferences.Store;
        s.Append(Dataset,"new-identity","recording",new JsonObject { ["contentSignature"]=Hash("new unrelated fixture"),["artists"]=Strings(["fixture"]) });
        Assert.Throws<InvalidOperationException>(()=>t.Activate("GENRE")); Assert.False(t.Enabled("GENRE")); return Task.CompletedTask;
    });
    [Fact] public async Task ExactRecordingResultsUsePersonalSourceManualWinsAndFileChangesFallBack()
    {
        var root=Path.Combine(Path.GetTempPath(),"jeek-personal-results-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await Run(async app=> {
                var path=Path.Combine(root,"fixture.wav"); File.WriteAllText(path,"isolated fixture bytes, no real song"); var stat=new FileInfo(path);
                var folder=app.Db.AddFolder("fixture","\\isolated\\",root+Path.DirectorySeparatorChar,"Fixture");
                using(var tx=app.Db.Begin()) { app.Db.ReplaceFile(tx,folder,"","fixture.wav",[new Track { FolderId=folder,TrackKey="fixture",Dir="",FileName="fixture.wav",Title="Fixture",ArtistDisplay="Fixture",Artists=["Fixture"],DurationMs=180000,Size=stat.Length,Mtime=stat.LastWriteTimeUtc.Ticks }]); tx.Commit(); }
                app.Library.RebuildSnapshot(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                InstallFixture(app,"VOICE"); app.Preferences.Training.Activate("VOICE");
                var track=app.Library.ByKey("fixture")!; var version=app.Preferences.Training.DeploymentStatus()["heads"]!.AsArray().Single(h=>S(h?["dimension"])=="VOICE")!["version"]!.DeepClone();
                app.Preferences.Store.SetLocal("personal-result:fixture",new JsonObject { ["trackKey"]="fixture",["signature"]=JeekMusic.Analysis.AnalysisFeature.Signature(track),["contentSignature"]=Hash("isolated fixture bytes, no real song"),["heads"]=new JsonObject { ["VOICE"]=new JsonObject { ["version"]=version,["value"]="VOCAL" } } }.ToJsonString());
                app.SmartTags.Recompute(); Assert.Equal(("VOCAL","PERSONAL"),app.Db.AllTags()["fixture"]["VOICE"]);
                Assert.Equal("VOCAL",S(app.SmartTags.Explain(track)["automatic"]?["VOICE"]));
                // Check content invalidation before a manual correction starts the
                // asynchronous daily-feedback reader (and invalidates this head).
                File.AppendAllText(path,"changed"); Assert.Empty(app.Preferences.Training.ResultsFor(track));
                await app.SmartTags.Override([track],new Dictionary<JeekMusic.Core.Analysis.TagDimension,JeekMusic.Core.Analysis.Tag?> { [JeekMusic.Core.Analysis.TagDimension.VOICE]=JeekMusic.Core.Analysis.Tag.INSTRUMENTAL });
                Assert.Equal(("INSTRUMENTAL","MANUAL"),app.Db.AllTags()["fixture"]["VOICE"]);
                Assert.Empty(app.Player.Queue.Items);
            });
        }
        // Run disposes the services and joins background readers before cleanup.
        finally { Directory.Delete(root,true); }
    }
    [Fact] public Task LearningWithNoTruthTerminatesWithoutDownloadOrTagWritesAndCanResume()=>Run(async app=> {
        var t=app.Preferences.Training;
        t.WithStoppedJobs(()=>Assert.Throws<InvalidOperationException>(()=>t.StartLearning()));
        t.StartLearning();
        for(int i=0;i<200 && t.LearningStatus()["running"]!.GetValue<bool>();i++) await Task.Delay(10);
        Assert.Equal("insufficient_material",S(t.LearningStatus()["state"])); Assert.Empty(app.Db.TagOverrides()); Assert.Empty(app.Preferences.Store.Current(Dataset,"answer"));
        t.StartLearning(); t.Cancel();
        for(int i=0;i<200 && t.LearningStatus()["running"]!.GetValue<bool>();i++) await Task.Delay(10);
        Assert.False(t.LearningStatus()["running"]!.GetValue<bool>()); Assert.False(Dimensions.Any(t.Enabled));
    });
    [Fact] public Task PersistedCrashRecoveryResumesOnceAndStopsAtTheRegisteredRetryLimit()=>Run(async app=> {
        var t=app.Preferences.Training; var s=app.Preferences.Store;
        s.SetLocal("learning-workflow",new JsonObject { ["state"]="learning" }.ToJsonString()); s.SetLocal("learning-recoveries","2");
        t.Maintain();
        for(int i=0;i<200 && t.LearningStatus()["running"]!.GetValue<bool>();i++) await Task.Delay(10);
        Assert.Equal("3",s.Local("learning-recoveries")); Assert.Equal("insufficient_material",S(t.LearningStatus()["state"]));
        s.SetLocal("learning-workflow",new JsonObject { ["state"]="learning" }.ToJsonString());
        t.GetType().GetField("maintenanceStarted",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(t,0); // simulate the next process
        t.Maintain(); Assert.Equal("interrupted",S(t.LearningStatus()["state"])); Assert.False(t.LearningStatus()["running"]!.GetValue<bool>());
        Assert.Empty(s.Current(Dataset,"answer")); Assert.Empty(app.Db.TagOverrides());
    });
}
