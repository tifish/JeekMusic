using System.Text.Json.Nodes;
using JeekMusic.Core.Preferences;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

public sealed partial class PreferenceService
{
    List<Identity> Identities(string profile)=>Store.Current(profile,"recording").GroupBy(e=>S(e["entityId"])).Where(g=>g.Count()==1).Select(g=>g.Single()).Select(e=> {
        var r=e["payload"]!;
        return new Identity(S(e["entityId"]),S(r["contentSignature"]),S(r["workGroup"]),S(r["albumGroup"]),ReadStrings(r["artists"]),r["identityReviewed"]?.GetValue<bool>()==true,r["exposed"]?.GetValue<bool>()==true,r["supported"]?.GetValue<bool>()==true);
    }).ToList();
    JsonObject? Study()=>Store.Current(ActiveProfile,"manifest").Where(e=>S(e["payload"]?["protocolVersion"]) is "personal-workout-v1" or "personal-tags-v2" && e["payload"]?["training"] is JsonArray).SingleOrDefault()?["payload"]?.AsObject();
    public JsonObject Inventory()
    {
        var p=RequireProfile();
        var records=Store.Current(p,"recording");
        var selected=ReadStrings(Store.Local("inventoryItems:"+p) is { } json ? JsonNode.Parse(json):null);
        return new JsonObject { ["registered"]=records.Count,["reviewed"]=records.Count(e=>e["payload"]?["identityReviewed"]?.GetValue<bool>()==true),
            ["items"]=new JsonArray(records.Where(e=>selected.Contains(S(e["entityId"]))).Select(e=>(JsonNode)new JsonObject { ["recordingId"]=S(e["entityId"]),["metadata"]=e["payload"]!.DeepClone() }).ToArray()),
            ["studyState"]=Study() is { } s ? PreferenceStudy.Contaminated(s,Identities(p)) ? "contaminated" : "frozen" : "not_frozen" };
    }
    public JsonObject FreezeStudy()
    {
        var p=RequireProfile();
        if(Study()!=null) throw new InvalidOperationException("Study already frozen; identity pollution invalidates it rather than silently resampling");
        var attempted=Store.Current(p,"manifest").SelectMany(e=>ReadStrings(e["payload"]?["items"])).ToHashSet();
        JsonObject frozen;
        try { frozen=PreferenceStudy.Freeze(Identities(p),attempted,Store.Local("seed:"+p)??"personal-workout-v1"); }
        catch(InvalidOperationException) when(ActiveDimension!="MOOD") { frozen=PreferenceStudy.Freeze(Identities(p),attempted,Store.Local("seed:"+p)??"personal-workout-v1",includeWorkout:false); }
        frozen["headProtocols"]=HeadContract();
        frozen["recordingRevisions"]=new JsonObject(Store.Current(p,"recording").Select(e=>new KeyValuePair<string,JsonNode?>(S(e["entityId"]),JsonValue.Create(S(e["eventId"])))));
        frozen["hash"]=Hash(frozen.ToJsonString());
        Store.Append(p,"study-v1","manifest",frozen); Notify(); return new JsonObject { ["state"]="frozen",["hash"]=S(frozen["hash"]),["supportedHeads"]=frozen["supportedHeads"]!.DeepClone(),["trainingAttempts"]=ReadStrings(frozen["training"]).Length,["developmentAttempts"]=ReadStrings(frozen["development"]).Length,["sealedTestAttempts"]=ReadStrings(frozen["sealed_test"]).Length };
    }
    JsonObject PrepareFrozenBatch(JsonObject study)
    {
        var p=RequireProfile();
        if(!SupportedHeads(study).Contains(ActiveDimension)) throw new InvalidOperationException("This frozen protocol does not cover the workout independent-study budget; other heads remain usable");
        if(PreferenceStudy.Contaminated(study,Identities(p))) throw new InvalidOperationException("Identity contamination: study stopped; sealed pool loses independence, never silently repaired");
        var models=Store.Current(p,"model").Where(e=>Dimension(e["payload"])==ActiveDimension).ToList();
        string role="training";
        if(models.Any(e=>S(e["payload"]?["state"])=="independent_test_required")) role="sealed_test";
        else if(models.Any(e=>S(e["payload"]?["state"])=="development_required")) role="development";
        foreach(var repeatRole in ActiveDimension=="MOOD" ? new[] { "training","development" } : Array.Empty<string>())
        {
            var count=Store.Current(p,"manifest").Where(e=>Dimension(e["payload"])==ActiveDimension && S(e["payload"]?["role"])==repeatRole).SelectMany(e=>ReadStrings(e["payload"]?["items"])).Distinct().Count();
            foreach(var threshold in repeatRole=="training" ? new[] {12,24,36,48} : new[] {6,12})
            {
                var tag=repeatRole+"-"+threshold;
                if(count<threshold || Store.Current(p,"manifest").Any(e=>S(e["payload"]?["repeatTag"])==tag)) continue;
                var repeat=ReadStrings(study[repeatRole]).Take(threshold).OrderBy(id=>Order(S(study["seed"])+":repeat:"+tag,id),StringComparer.Ordinal).First();
                return SaveBatch(repeatRole,[repeat],study,[repeat],tag);
            }
        }
        var all=ReadStrings(study[role]).Take(ActiveDimension=="MOOD"?int.MaxValue:role=="training"?36:role=="development"?8:24).ToArray();
        var attempted=Store.Current(p,"manifest").Where(e=>Dimension(e["payload"])==ActiveDimension && S(e["payload"]?["role"])==role).SelectMany(e=>ReadStrings(e["payload"]?["items"])).ToHashSet();
        if(role=="training" && (ActiveDimension=="MOOD" ? attempted.Count is 24 or 36 or 48 : attempted.Count is 12 or 24 or 36) && !models.Any(e=>S(e["payload"]?["state"])=="training_node_failed" && e["payload"]?["attempted"]?.GetValue<int>()==attempted.Count))
            throw new InvalidOperationException("At a fixed training node; run training diagnostics before adding another batch. No automatic unlimited expansion");
        var node=role=="training" ? ActiveDimension=="MOOD" ? attempted.Count<24?24:attempted.Count<36?36:48 : attempted.Count<12?12:attempted.Count<24?24:36 : all.Length;
        var next=all.Where(id=>!attempted.Contains(id)).Take(Math.Min(6,node-attempted.Count)).ToArray();
        if(next.Length==0) throw new InvalidOperationException("Frozen stage attempt budget exhausted; no replacements for uncertainty, missing files or withdrawals");
        return SaveBatch(role,next,study,[],"");
    }
    JsonObject SaveBatch(string role,string[] items,JsonObject study,string[] repeats,string repeatTag)
    {
        var p=RequireProfile(); var id=Guid.NewGuid().ToString("N");
        var payload=new JsonObject { ["dimension"]=ActiveDimension,["protocolVersion"]="personal-tags-v2-batch",["studyHash"]=S(study["hash"]),["role"]=role,["items"]=Strings(items),["repeatIds"]=Strings(repeats),["repeatTag"]=repeatTag,["state"]="frozen_"+role,["independent"]=role!="training",["exposure"]="blind_ui_no_predictions" };
        payload["hash"]=Hash(payload.ToJsonString()); Store.Append(p,id,"manifest",payload); Store.SetLocal(BatchKey(p,ActiveDimension),id); Notify(); return Batch();
    }
}
