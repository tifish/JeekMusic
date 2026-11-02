using System.Reflection;
using System.Text.Json.Nodes;
using JeekMusic.Automation;
using JeekMusic.Preferences;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.App.Tests;

public sealed partial class PreferenceDeploymentTests
{
    [Fact] public void SharedFailureContractRejectsPrivateFieldsAndCoversVocabulary()
    {
        var contract=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"testcases","learning-failure-v1.json")))!;
        Assert.Equal(contract["codes"]!.AsArray().Select(n=>n!.GetValue<string>()).Order(),LearningFailure.Codes.Order());
        var safe=LearningFailure.Sanitize(contract["safe"]!); Assert.Equal(480000,safe["expectedSamples"]!.GetValue<long>());
        var hostile=LearningFailure.Sanitize(contract["hostile"]!); Assert.DoesNotContain("SECRET",hostile.ToJsonString());Assert.DoesNotContain("private",hostile.ToJsonString());Assert.Null(hostile["exitCode"]);
    }
    [Theory]
    [InlineData("TimeoutError","time_budget")]
    [InlineData("MemoryError","memory_budget")]
    [InlineData("ModuleNotFoundError","dependency_missing")]
    [InlineData("CalledProcessError","decode_unsupported")]
    [InlineData("UnknownPrivateType","worker_exit")]
    public void WorkerErrorsAreStableSanitizedAndKeepStage(string type,string expected)
    {
        var failure=LearningFailure.FromWorker(new JsonObject { ["errorType"]=type,["error"]="SECRET_AUDIO_LABEL_TOKEN_PATH" },"extract","VOICE",17);
        Assert.Equal(expected,S(failure["code"])); Assert.Equal("extract",S(failure["stage"])); Assert.Equal(17,failure["exitCode"]!.GetValue<int>());
        Assert.DoesNotContain("SECRET",failure.ToJsonString()); Assert.DoesNotContain("UnknownPrivateType",failure.ToJsonString());
    }
    [Fact] public void HistoricalExactDecodeFailureIsRecoverableButExitCodeRemainsUnknown()
    {
        var failure=LearningFailure.FromWorker(new JsonObject { ["errorType"]="ValueError",["error"]="Incomplete 10-second audio" },"extract","VOICE",null,true);
        Assert.Equal("decode_length",S(failure["code"])); Assert.Equal("decode",S(failure["workerPhase"])); Assert.Null(failure["exitCode"]); Assert.True(failure["historical"]!.GetValue<bool>());
    }
    [Fact] public void UntrustedFailureFieldsCannotReachUiOrCopyableDiagnostics()
    {
        var raw=new JsonObject { ["code"]="decode_length",["stage"]="SECRET",["dimension"]="SECRET",["message"]="SECRET",["nextStep"]="SECRET",["workerErrorType"]="SECRET",["workerPhase"]="SECRET",["expectedSamples"]=-1,["actualSamples"]=100000001 };
        var safe=LearningFailure.Sanitize(raw); Assert.DoesNotContain("SECRET",safe.ToJsonString()); Assert.DoesNotContain("SECRET",LearningFailure.Diagnostics(safe)); Assert.Null(safe["expectedSamples"]); Assert.Null(safe["actualSamples"]);
    }
    [Fact] public Task ChildFailureSurvivesWorkflowPersistenceMcpAndSyncWithoutRetraining()=>Run(async app=> {
        var t=app.Preferences.Training;var store=app.Preferences.Store;
        foreach(var (key,value) in new[]{("jobProfile",Dataset),("jobDimension","VOICE"),("jobGeneration",store.Generation),("jobStage","extract")}) typeof(PreferenceTraining).GetField(key,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(t,value);
        var failure=LearningFailure.Create("decode_length","extract","VOICE",2,"ValueError",480000,479000);
        typeof(PreferenceTraining).GetMethod("Fail",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(t,[failure,"failed"]);
        typeof(PreferenceTraining).GetMethod("LearningFail",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(t,[failure,"failed",0,0,0]);
        var status=t.Status("VOICE"); Assert.Equal("decode_length",S(status["failure"]?["code"])); Assert.Equal("decode_length",S(status["learning"]?["failure"]?["code"])); Assert.False(status["learning"]!["running"]!.GetValue<bool>());
        var tools=new McpTools(app); var reply=await tools.CallAsync("preference_training",new() { ["action"]="status",["dimension"]="VOICE" }); Assert.Equal("decode_length",S(reply["failure"]?["code"]));
        using var phone=new PreferenceStore(":memory:","failure-phone"); phone.AcceptPage("pc",store.Page(0));
        Assert.Equal("decode_length",S(phone.Current(Dataset,"model").Single(e=>S(e["entityId"])=="job:VOICE")["payload"]?["failure"]?["code"])); Assert.Empty(app.Db.TagOverrides()); Assert.False(Dimensions.Any(t.Enabled));
    });
    [Fact] public Task HistoricalFailureWithoutEvidenceExplicitlyReportsUnknown()=>Run(app=> {
        app.Preferences.Store.Append(Dataset,"job:VOICE","model",new JsonObject { ["dimension"]="VOICE",["category"]="job",["state"]="failed" });
        Assert.Equal("history_unavailable",S(app.Preferences.Training.Status("VOICE")["failure"]?["code"])); return Task.CompletedTask;
    });
    [Fact] public Task DecoderOnlyUpgradeKeepsPriorSuccessfulVectorProvenance()=>Run(app=> {
        var t=app.Preferences.Training;
        var method=typeof(PreferenceTraining).GetMethod("CompatibleWorker",BindingFlags.NonPublic|BindingFlags.Instance)!;
        Assert.True((bool)method.Invoke(t,["cc9ea8a8e6daffa64cc62619c94d5a62cc2454afdc0c1743934e0e5feed1f237"])!);
        Assert.False((bool)method.Invoke(t,[new string('a',64)])!);return Task.CompletedTask;
    });
}
