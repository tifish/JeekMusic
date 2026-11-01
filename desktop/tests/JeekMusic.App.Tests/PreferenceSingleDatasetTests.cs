using System.Text.Json.Nodes;
using JeekMusic.Preferences;
using Microsoft.Data.Sqlite;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.App.Tests;

public sealed class PreferenceSingleDatasetTests
{
    static JsonObject Answer(string value="SPORT")=>new() { ["dimension"]="MOOD",["source"]="explicit_human_blind_batch",["answer"]=value,["basis"]="whole_recording_familiar",["contextId"]=Context,["recordingId"]=new string('a',64),["contentSignature"]=new string('b',64),["manifestId"]="batch",["role"]="training" };
    [Fact] public void IndependentlyInitializedDevicesShareOneDatasetAndPreserveConcurrentHeads()
    {
        using var a=new PreferenceStore(":memory:","pc"); using var b=new PreferenceStore(":memory:","phone");
        Assert.Equal(a.Generation,b.Generation); Assert.Equal(new[]{Dataset},a.Profiles());
        a.Append(Dataset,"answer","answer",Answer()); b.Append(Dataset,"answer","answer",Answer("NON_SPORT"));
        a.AcceptPage("phone",b.Page(0)); b.AcceptPage("pc",a.Page(0));
        Assert.Equal(2,a.Current(Dataset,"answer").Count); Assert.Equal(2,b.Current(Dataset,"answer").Count);
        a.Append(Dataset,"answer","answer",Answer("WITHDRAWN"),true); b.AcceptPage("pc",a.Page(b.Cursor("pc")));
        Assert.Equal("WITHDRAWN",S(Assert.Single(b.Current(Dataset,"answer"))["payload"]?["answer"]));
    }
    [Fact] public void ClearDominatesOfflineEventsAndReplayAndPermitsImmediateReuse()
    {
        using var a=new PreferenceStore(":memory:","pc"); using var b=new PreferenceStore(":memory:","phone");
        a.Append(Dataset,"answer","answer",Answer()); b.AcceptPage("pc",a.Page(0));
        b.Append(Dataset,"answer","answer",Answer("NON_SPORT")); var offline=b.Page(0);
        var oldGeneration=a.Generation; var oldCursor=b.Cursor("pc"); a.ClearLearning();
        Assert.Throws<InvalidOperationException>(()=>a.Append(Dataset,"late","answer",Answer(),expectedGeneration:oldGeneration)); var cleared=a.Page(oldCursor);
        Assert.Equal(oldCursor,cleared["nextCursor"]!.GetValue<long>()); // sequence cannot rewind
        a.AcceptPage("phone",offline); Assert.Empty(a.Read(Dataset));
        b.AcceptPage("pc",cleared); Assert.Empty(b.Read(Dataset));
        b.AcceptPage("pc",cleared); a.AcceptPage("phone",offline); Assert.Empty(a.Read(Dataset));
        a.Append(Dataset,"answer","answer",Answer()); b.AcceptPage("pc",a.Page(oldCursor));
        Assert.Single(b.Current(Dataset,"answer"));
        b.AcceptPage("stale-peer",offline); Assert.Single(b.Current(Dataset,"answer"));
        Assert.DoesNotContain("reason",a.Page(0)["resetBoundary"]!.ToJsonString());
    }
    [Fact] public void ConcurrentClearsConvergeAndDiscardWritesThatDidNotObserveBothBoundaries()
    {
        using var a=new PreferenceStore(":memory:","pc"); using var b=new PreferenceStore(":memory:","phone");
        a.ClearLearning(); b.ClearLearning(); a.Append(Dataset,"a","answer",Answer()); b.Append(Dataset,"b","answer",Answer());
        var pa=a.Page(0); var pb=b.Page(0); a.AcceptPage("phone",pb); b.AcceptPage("pc",pa);
        Assert.Equal(a.Generation,b.Generation); Assert.Empty(a.Read(Dataset)); Assert.Empty(b.Read(Dataset));
        b.Append(Dataset,"fresh","answer",Answer()); a.AcceptPage("phone",b.Page(a.Cursor("phone")));
        Assert.Single(a.Current(Dataset,"answer"));
    }
    [Fact] public void OldProtocolAndOtherDatasetFailWithoutAdvancingCursorOrClearingData()
    {
        using var a=new PreferenceStore(":memory:","pc"); var e=a.Append(Dataset,"answer","answer",Answer());
        var page=a.Page(0); page["protocol"]=2;
        Assert.Throws<ArgumentException>(()=>a.AcceptPage("old",page)); Assert.Equal(0,a.Cursor("old"));
        var wrong=e.DeepClone().AsObject(); wrong["profileId"]="old-multiple-datasets";
        Assert.Throws<ArgumentException>(()=>a.Import([wrong]));
        page=a.Page(0); page["resetBoundary"]=Strings([new string('a',32)]); page["generation"]=Hash("personal-label-learning-v3\n"+new string('a',32));
        Assert.Throws<ArgumentException>(()=>a.AcceptPage("malformed",page)); Assert.Single(a.Read(Dataset));
    }
    [Theory] [InlineData(1)] [InlineData(2)]
    public void UpgradeResetsOnlyLearningOnceAndNewDataSurvivesRestart(int version)
    {
        var root=Path.Combine(Path.GetTempPath(),"jeek-single-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path=Path.Combine(root,"preferences.db"); var ordinary=Path.Combine(root,"library.db"); var settings=Path.Combine(root,"settings.json");
        try
        {
            File.WriteAllText(settings,"ordinary settings, pairing and progress sentinel");
            using(var library=new SqliteConnection("Pooling=False;Data Source="+ordinary)) { library.Open(); using var c=library.CreateCommand(); c.CommandText="CREATE TABLE ordinary(value TEXT); INSERT INTO ordinary VALUES('library playlists manual tags reviews analysis pairing mapping sentinel');"; c.ExecuteNonQuery(); }
            using(var old=new SqliteConnection("Pooling=False;Data Source="+path)) {
                old.Open(); using var c=old.CreateCommand();
                c.CommandText="ATTACH DATABASE '"+path.Replace("'","''")+".evaluation' AS evaluation; CREATE TABLE events(id TEXT PRIMARY KEY,profile TEXT,entity TEXT,kind TEXT,json TEXT"+(version==2?",dimension TEXT":"")+"); CREATE TABLE evaluation.events(id TEXT PRIMARY KEY,profile TEXT,entity TEXT,kind TEXT,json TEXT"+(version==2?",dimension TEXT":"")+"); CREATE TABLE journal(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE,sealed INTEGER); CREATE TABLE local(key TEXT PRIMARY KEY,value TEXT); CREATE TABLE peers(id TEXT PRIMARY KEY,cursor INTEGER); INSERT INTO events(id,profile,entity,kind,json) VALUES('old','old-user','old-answer','answer','private old answer body'); INSERT INTO evaluation.events(id,profile,entity,kind,json) VALUES('sealed','old-user','sealed','answer','private sealed body'); INSERT INTO journal(id,sealed) VALUES('old',0); INSERT INTO local VALUES('old-state','private derived old state'); INSERT INTO peers VALUES('phone',40); PRAGMA user_version="+version+";";
                c.ExecuteNonQuery();
            }
            var before=File.ReadAllBytes(ordinary); var beforeSettings=File.ReadAllText(settings); string generation;
            using(var current=new PreferenceStore(path,"pc")) {
                Assert.Empty(current.Read(Dataset,includeEvaluation:true)); Assert.Null(current.Local("old-state")); Assert.Equal(0,current.Cursor("phone"));
                Assert.Equal(1,current.Page(0)["nextCursor"]!.GetValue<long>()); generation=current.Generation;
                current.Append(Dataset,"fresh","answer",Answer());
            }
            using(var restarted=new PreferenceStore(path,"pc")) { Assert.Equal(generation,restarted.Generation); Assert.Single(restarted.Current(Dataset,"answer")); }
            Assert.Equal(before,File.ReadAllBytes(ordinary)); Assert.Equal(beforeSettings,File.ReadAllText(settings));
            Assert.DoesNotContain("private old answer body",System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            Assert.DoesNotContain("private sealed body",System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path+".evaluation")));
        }
        finally { Directory.Delete(root,true); }
    }
}
