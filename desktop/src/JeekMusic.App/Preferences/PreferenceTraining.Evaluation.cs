using System.Text.Json.Nodes;
using JeekMusic.Core.Preferences;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

public sealed partial class PreferenceTraining
{
    JsonObject EvaluationSnapshot(string dimension,string profile)
    {
        var study=service.Store.Current(profile,"manifest").SingleOrDefault(e=>S(e["entityId"])=="study-v1")?["payload"]?.AsObject() ?? throw new InvalidOperationException("Freeze the complete identity study first");
        var identities=service.Store.Current(profile,"recording").Select(e=> { var r=e["payload"]!; return new Identity(S(e["entityId"]),S(r["contentSignature"]),S(r["workGroup"]),S(r["albumGroup"]),ReadStrings(r["artists"]),r["identityReviewed"]?.GetValue<bool>()==true,r["exposed"]?.GetValue<bool>()==true,true); }).ToArray();
        if(PreferenceStudy.Contaminated(study,identities)) throw new InvalidOperationException("Identity contamination: evaluation permanently stopped");
        var candidates=service.Store.Current(profile,"model").Where(e=>Dimension(e["payload"])==dimension && S(e["payload"]?["state"]) is "development_required" or "regression_required" or "independent_test_required").ToArray();
        if(candidates.Length!=1) throw new InvalidOperationException("One frozen candidate with a passed training gate required");
        var candidate=candidates[0]; var candidateData=candidate["payload"]!;
        var trainingHash=S(candidateData["trainingSetHash"]);
        var trainingAnswers=service.Store.Current(profile,"answer").GroupBy(e=>S(e["entityId"])).Where(g=>g.Count()==1).Select(g=>g.Single()).Where(e=>Dimension(e["payload"])==dimension && UsableAnswer(e)).ToList();
        if(Hash(Snapshot("train",trainingAnswers,dimension,profile).ToJsonString())!=trainingHash) throw new InvalidOperationException("Training data/content/extractor changed; frozen candidate invalid");
        var path=Path.Combine(root,"candidates",trainingHash+".json");
        var artifact=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if(S(artifact["trainingSetHash"])!=trainingHash || S(artifact["extractor"])!=Extractor) throw new InvalidOperationException("Candidate artifact mismatch");
        string role=S(candidateData["state"]) switch { "development_required"=>"development", "regression_required"=>"calibration_regression", _=>"sealed_test" };
        if(role=="calibration_regression")
        {
            var regression=RegressionSnapshot(false,profile);
            if(regression["rows"]!.AsArray().Count!=11 || regression["rows"]!.AsArray().Any(n=>n?["vector"] is null)) throw new InvalidOperationException("Old-feedback regression requires all 11 exact recordings and bounded explicit extraction; no inferred or missing negative answers");
            regression["stage"]=role; regression["candidateId"]=S(candidate["entityId"]); regression["model"]=artifact["model"]!.DeepClone(); regression["trainingSetHash"]=trainingHash;
            return regression;
        }
        var expected=ReadStrings(study[role]).Take(dimension=="MOOD"?int.MaxValue:role=="development"?8:24).ToArray();
        var attempted=service.Store.Current(profile,"manifest").Where(e=>Dimension(e["payload"])==dimension && S(e["payload"]?["role"])==role).SelectMany(e=>ReadStrings(e["payload"]?["items"])).Distinct().ToArray();
        if(attempted.Length!=expected.Length || !attempted.ToHashSet().SetEquals(expected)) throw new InvalidOperationException("Finish the entire frozen stage before opening any evaluation result");
        var answers=service.Store.Current(profile,"answer",true).Where(e=>Dimension(e["payload"])==dimension && S(e["payload"]?["role"])==role && e["payload"]?["repeat"]?.GetValue<bool>()!=true).GroupBy(e=>S(e["payload"]?["recordingId"])).ToDictionary(g=>g.Key,g=>g.ToArray());
        if(expected.Any(id=>!answers.TryGetValue(id,out var a) || a.Length!=1)) throw new InvalidOperationException("Every fixed attempt needs an explicit answer/skip; resolve concurrent answers first");
        if(role=="sealed_test")
        {
            var openings=service.Store.Current(profile,"manifest").Where(e=>S(e["entityId"])=="global-seal-exposure").ToArray();
            if(openings.Length>1) throw new InvalidOperationException("Concurrent shared sealed-pool openings; independence invalidated across heads");
            if(openings.Length==0)
            {
                var allowed=new JsonObject();
                foreach(var e in service.Store.Current(profile,"model").Where(e=>S(e["payload"]?["state"])=="independent_test_required")) allowed[Dimension(e["payload"])]=S(e["payload"]?["trainingSetHash"]);
                service.Store.Append(profile,"global-seal-exposure","manifest",new JsonObject { ["state"]="opened",["allowedFrozenHeads"]=allowed,["studyHash"]=S(study["hash"]) });
            }
            var opening=service.Store.Current(profile,"manifest").Single(e=>S(e["entityId"])=="global-seal-exposure")["payload"]!;
            if(S(opening["allowedFrozenHeads"]?[dimension])!=trainingHash) throw new InvalidOperationException("This head was not frozen before the shared sealed pool opened; existing pool is no longer independent for this head");
        }
        var rows=new JsonArray();
        foreach(var id in expected)
        {
            var a=answers[id][0]["payload"]!;
            string label=S(a["answer"]);
            if(!Binary(label,dimension) || S(a["basis"]) is not ("whole_recording_familiar" or "listened_full")) label="";
            var cached=service.Store.Local("vector:"+id) is { } value ? JsonNode.Parse(value):null;
            var valid=S(cached?["extractor"])==Extractor && S(cached?["workerHash"])==FileHash(Worker) && S(cached?["contentSignature"])==S(a["contentSignature"]);
            var track=service.FindLocal(id);
            rows.Add((JsonNode)new JsonObject { ["recordingId"]=id,["role"]=role,["label"]=label,["vector"]=valid?cached?["vector"]?.DeepClone():null,["perturbations"]=valid?cached?["perturbations"]?.DeepClone():null,
                ["rules"]=track is null ? "ABSTAIN" : S(app.SmartTags.Explain(track)["automatic"]?[dimension]) });
        }
        var result=new JsonObject { ["dimension"]=dimension,["profileId"]=profile,["stage"]=role,["studyHash"]=S(study["hash"]),["candidateId"]=S(candidate["entityId"]),["model"]=artifact["model"]!.DeepClone(),["trainingSetHash"]=trainingHash,["annotationStabilityPassed"]=AnnotationStabilityPassed(profile),["regressionPassed"]=candidateData["regressionPassed"]?.GetValue<bool>()==true,["rows"]=rows };
        var audit="evaluation:"+profile+":"+dimension+":"+role;
        if(service.Store.Local(audit)!=null) throw new InvalidOperationException("This frozen stage was already opened; no retuning or repeated independent evaluation");
        service.Store.SetLocal(audit,Hash(result.ToJsonString()));
        return result;
    }
    bool AnnotationStabilityPassed(string profile)
    {
        var all=service.Store.Read(profile,"answer",true); int evaluable=0,conflicts=0;
        var repeats=service.Store.Current(profile,"answer",true).Where(e=>Dimension(e["payload"])=="MOOD" && e["payload"]?["repeat"]?.GetValue<bool>()==true);
        foreach(var repeat in repeats)
        {
            var r=repeat["payload"]!;
            var original=all.Where(e=>Dimension(e["payload"])=="MOOD" && e["payload"]?["repeat"]?.GetValue<bool>()!=true && S(e["payload"]?["recordingId"])==S(r["recordingId"]) && e["at"]!.GetValue<long>()<repeat["at"]!.GetValue<long>()).OrderBy(e=>e["at"]!.GetValue<long>()).FirstOrDefault()?["payload"];
            if(S(original?["answer"]) is not ("SPORT" or "NON_SPORT") || S(r["answer"]) is not ("SPORT" or "NON_SPORT")) continue;
            evaluable++; if(S(original?["answer"])!=S(r["answer"])) conflicts++;
        }
        return evaluable>=4 && conflicts<=1;
    }
    JsonObject RegressionSnapshot(bool extract,string? profile=null)
    {
        var owner=profile??service.ActiveProfile;
        var feedback=service.Store.Current(owner,"answer").Where(e=>Dimension(e["payload"])=="MOOD" && S(e["payload"]?["role"])=="calibration_regression").GroupBy(e=>S(e["payload"]?["recordingId"])).Where(g=>g.Count()==1).Select(g=>g.Single()["payload"]!).ToArray();
        if(feedback.Length!=11) throw new InvalidOperationException("Import and confirm this profile owner's eleven explicit legacy workout regression answers first; no global built-in user truth is used");
        var rows=new JsonArray();
        foreach(var item in feedback)
        {
            var id=S(item["recordingId"]);
            var track=service.FindLocal(id);
            if(track is null || track.IsCue || app.Library.PathOf(track) is not { } path) continue;
            var info=new FileInfo(path);
            if(!info.Exists || info.Length!=track.Size || info.LastWriteTimeUtc.Ticks!=track.Mtime) continue;
            var centers=JeekMusic.Core.Analysis.PcmMath.Windows(0,track.DurationMs).Select(w=>(w.StartMs+w.EndMs)/2).ToArray();
            if(centers.Length!=3 || centers[1]-centers[0]<30000 || centers[2]-centers[1]<30000) continue;
            var signature=FileHash(path); if(signature!=S(item["contentSignature"])) continue;
            var cached=service.Store.Local("vector:"+id) is { } value ? JsonNode.Parse(value):null;
            var valid=S(cached?["extractor"])==Extractor && S(cached?["workerHash"])==FileHash(Worker) && S(cached?["contentSignature"])==signature;
            if(extract && valid) continue;
            var row=new JsonObject { ["recordingId"]=id,["contentSignature"]=signature,["role"]="calibration_regression",["label"]=S(item?["answer"]),["rules"]=S(item?["baselineAtFeedback"]),["cue"]=false,["centersMs"]=new JsonArray(centers.Select(c=>(JsonNode?)JsonValue.Create(c)).ToArray()) };
            if(extract) row["path"]=path;
            else if(valid) { row["vector"]=cached!["vector"]!.DeepClone(); row["perturbations"]=cached["perturbations"]?.DeepClone(); }
            rows.Add((JsonNode)row); if(extract && rows.Count==6) break;
        }
        return new JsonObject { ["profileId"]=profile??service.ActiveProfile,["rows"]=rows,["extractor"]=Extractor };
    }
    void CommitEvaluation(JsonObject snapshot,JsonObject result)
    {
        var profile=S(snapshot["profileId"]); var id=S(snapshot["candidateId"]);
        var current=service.Store.Current(profile,"model").Single(e=>S(e["entityId"])==id)["payload"]!.DeepClone().AsObject();
        if(S(current["state"])=="invalidated" || service.Deleted(profile)) throw new InvalidOperationException("Candidate invalidated while evaluation ran");
        current["state"]=S(result["state"]) switch { "development_passed"=>Dimension(snapshot)=="MOOD"?"regression_required":"independent_test_required", "regression_passed"=>"independent_test_required", "pass_research_gate"=>"pass_research_gate", "inconclusive" or "incomplete"=>"inconclusive", _=>"validation_failed" };
        current["enabled"]=false;
        current["evaluationHash"]=Hash(result.ToJsonString());
        if(S(result["state"])=="regression_passed") current["regressionPassed"]=true;
        service.Store.Append(profile,id,"model",current);
    }
}
