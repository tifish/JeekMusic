using System.Text.Json.Nodes;
using JeekMusic.Preferences;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.App.Tests;

public sealed class PreferenceStoreTests
{
    static JsonObject Answer(string label="SPORT",string role="training")=>new() { ["source"]="explicit_human_blind_batch",["answer"]=label,["basis"]="whole_recording_familiar",["contextId"]=Context,["recordingId"]=new string('a',64),["contentSignature"]=new string('b',64),["manifestId"]="manifest",["role"]=role };
    [Fact] public void OfflineBranchesRetryWithdrawalAndExplicitResolution()
    {
        using var a=new PreferenceStore(":memory:","pc"); using var b=new PreferenceStore(":memory:","phone");
        var initial=a.Append("person","answer","answer",Answer());
        b.AcceptPage("pc",a.Page(0)); b.AcceptPage("pc",a.Page(0));
        Assert.Single(b.Current("person","answer"));
        a.Append("person","answer","answer",Answer("NON_SPORT"));
        b.Append("person","answer","answer",Answer("WITHDRAWN"));
        a.AcceptPage("phone",b.Page(0)); b.AcceptPage("pc",a.Page(0));
        Assert.Equal(2,a.Current("person","answer").Count);
        Assert.Equal(2,b.Current("person","answer").Count);
        Assert.Throws<InvalidOperationException>(()=>a.Append("person","answer","answer",Answer()));
        a.Append("person","answer","answer",Answer("UNCERTAIN"),true);
        b.AcceptPage("pc",a.Page(0));
        Assert.Equal("UNCERTAIN",S(Assert.Single(b.Current("person","answer"))["payload"]?["answer"]));
        Assert.False(UsableAnswer(Assert.Single(b.Current("person","answer"))));
    }
    [Fact] public void SealedAnswersAreAbsentFromTrainingStoreButStillSync()
    {
        using var a=new PreferenceStore(":memory:","pc"); using var b=new PreferenceStore(":memory:","phone");
        a.Append("person","sealed","answer",Answer("SPORT","sealed_test"));
        a.Append("person","dev","answer",Answer("NON_SPORT","development"));
        Assert.Empty(a.Read("person","answer"));
        Assert.Equal(2,a.Read("person","answer",true).Count);
        b.AcceptPage("pc",a.Page(0)); Assert.Empty(b.Read("person","answer")); Assert.Equal(2,b.Read("person","answer",true).Count);
    }
    [Fact] public void MutationAndCrossProfileAncestryRollBackTheWholePage()
    {
        using var a=new PreferenceStore(":memory:","pc");
        var e=a.Append("person","answer","answer",Answer());
        var changed=e.DeepClone().AsObject(); changed["payload"]!["answer"]="NON_SPORT";
        var other=e.DeepClone().AsObject(); other["eventId"]="other";
        Assert.Throws<InvalidDataException>(()=>a.Import([other,changed]));
        Assert.Single(a.Read("person","answer"));
        var bad=e.DeepClone().AsObject(); bad["eventId"]="bad"; bad["profileId"]="another"; bad["parents"]=Strings([S(e["eventId"])]); bad["revision"]=2L;
        Assert.Throws<InvalidDataException>(()=>a.Import([bad]));
        a.Append("person","answer","answer",Answer("WITHDRAWN")); // connection usable after rollback
        Assert.False(UsableAnswer(Assert.Single(a.Current("person","answer"))));
    }
    [Fact] public void PagesPersistAcrossRestartAndCannotSkipAfterFailedImport()
    {
        var path=Path.Combine(Path.GetTempPath(),"jeek-preference-"+Guid.NewGuid().ToString("N")+".db");
        try
        {
            using(var a=new PreferenceStore(path,"pc")) for(int i=0;i<7;i++) a.Append("person","r"+i,"recording",new JsonObject());
            using var source=new PreferenceStore(path,"pc"); using var target=new PreferenceStore(":memory:","phone");
            var page=source.Page(0,3); target.AcceptPage("pc",page); Assert.Equal(3,target.Cursor("pc"));
            var malformed=source.Page(3,3); malformed["events"]![0]!["protocol"]=99;
            Assert.Throws<ArgumentException>(()=>target.AcceptPage("pc",malformed)); Assert.Equal(3,target.Cursor("pc"));
            target.AcceptPage("pc",source.Page(target.Cursor("pc"))); Assert.Equal(7,target.Read("person").Count);
        }
        finally { foreach(var f in new[] {path,path+".evaluation"}) if(File.Exists(f)) File.Delete(f); }
    }
    [Fact] public void IdentityComponentsIncludeWorksAlbumsCollaboratorsAndMissingIdentities()
    {
        Identity[] rows=[new("a","c1","work","album1",["artist1"],true,false,true),new("b","c2","work","album2",["artist2"],true,false,true),new("c","c3","other","album2",["artist3"],true,false,true),new("d","c4","four","album4",["artist3","artist4"],true,false,true),new("missing","c5","","",[],false,false,true)];
        var g=Group(rows);
        Assert.Equal(g.Components["a"],g.Components["d"]); Assert.NotEqual(g.Components["a"],g.Components["missing"]);
        Assert.Equal("missing",Assert.Single(g.Unresolved));
    }
    [Fact] public void DeletionRedactsBodiesPropagatesAndDoesNotResurrectOfflineEdits()
    {
        using var a=new PreferenceStore(":memory:","pc"); using var b=new PreferenceStore(":memory:","phone");
        a.Append("person","person","profile",new JsonObject { ["name"]="Private name" });
        a.Append("person","answer","answer",Answer()); b.AcceptPage("pc",a.Page(0));
        b.Append("person","answer","answer",Answer("NON_SPORT"));
        a.DeleteProfile("person");
        a.AcceptPage("phone",b.Page(0));
        Assert.Empty(a.Read("person","answer",true));
        b.AcceptPage("pc",a.Page(b.Cursor("pc")));
        Assert.Empty(b.Read("person","answer",true));
        Assert.True(Assert.Single(b.Current("person","profile"))["payload"]!["deleted"]!.GetValue<bool>());
        Assert.False(b.Page(b.Cursor("pc"))["hasMore"]!.GetValue<bool>());
    }
    [Fact] public void SealedPurposeCannotBeChangedToTrainingByARevision()
    {
        using var a=new PreferenceStore(":memory:","pc");
        var e=a.Append("person","answer","answer",Answer("SPORT","sealed_test"));
        var changed=e.DeepClone().AsObject(); changed["eventId"]="changed"; changed["revision"]=2L; changed["parents"]=Strings([S(e["eventId"])]); changed["payload"]!["role"]="training";
        Assert.Throws<InvalidDataException>(()=>a.Import([changed])); Assert.Empty(a.Read("person","answer"));
    }
    [Fact] public void FrozenListsKeepWholeComponentsAndRejectLaterCrossPoolLinks()
    {
        var rows=new List<Identity>(); var attempted=new HashSet<string>();
        for(int i=0;i<24;i++) { var id="pilot"+i; rows.Add(new(id,"content"+id,"work"+id,"album"+id,["artist"+(i/3)],true,false,true)); attempted.Add(id); }
        for(int i=0;i<84;i++) { var id="unseen"+i; rows.Add(new(id,"content"+id,"work"+id,"album"+id,["unseenArtist"+i],true,false,true)); }
        var study=JeekMusic.Core.Preferences.PreferenceStudy.Freeze(rows,attempted,"fixed");
        Assert.Equal(48,ReadStrings(study["training"]).Length); Assert.Equal(12,ReadStrings(study["development"]).Length); Assert.Equal(48,ReadStrings(study["sealed_test"]).Length);
        Assert.False(JeekMusic.Core.Preferences.PreferenceStudy.Contaminated(study,rows));
        var a=ReadStrings(study["training"])[0]; var b=ReadStrings(study["sealed_test"])[0];
        var work=rows.Single(r=>r.Id==a).Work;
        rows=rows.Select(r=>r.Id==b ? r with { Work=work } : r).ToList();
        Assert.True(JeekMusic.Core.Preferences.PreferenceStudy.Contaminated(study,rows));
    }
    [Fact] public void OtherHeadsCanFreezeACompactFrameWithoutWorkoutsLargerBudget()
    {
        var rows=Enumerable.Range(0,44).Select(i=>new Identity("r"+i,"c"+i,"w"+i,"a"+i,["artist"+i],true,false,true)).ToArray();
        Assert.Throws<InvalidOperationException>(()=>JeekMusic.Core.Preferences.PreferenceStudy.Freeze(rows,new HashSet<string>(),"fixed"));
        var study=JeekMusic.Core.Preferences.PreferenceStudy.Freeze(rows,new HashSet<string>(),"fixed",includeWorkout:false);
        Assert.Equal(12,ReadStrings(study["training"]).Length); Assert.Equal(8,ReadStrings(study["development"]).Length); Assert.Equal(24,ReadStrings(study["sealed_test"]).Length);
        Assert.Equal(new[] {"VOICE","GENRE","CALMNESS"},ReadStrings(study["supportedHeads"]));
    }
}
