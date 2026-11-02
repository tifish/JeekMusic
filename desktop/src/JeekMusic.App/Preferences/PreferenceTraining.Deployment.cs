using System.Text.Json.Nodes;
using JeekMusic.Analysis;
using JeekMusic.Core.Preferences;
using JeekMusic.Library;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

public sealed partial class PreferenceTraining
{
    readonly Lock deploymentGate=new();
    Dictionary<string,JsonObject> activeHeads=new();
    internal string AnswerRevision(string d)=>Hash(string.Join("\n",service.Store.Current(Dataset,"answer",true).Where(e=>Dimension(e["payload"])==d).Select(e=>S(e["eventId"])).Order(StringComparer.Ordinal)));
    string ReceiptDependencies(string d,string stage)
    {
        var events=service.Store.Current(Dataset,"recording").Concat(service.Store.Current(Dataset,"manifest").Where(e=>S(e["entityId"])=="study-v1"))
            .Concat(service.Store.Current(Dataset,"answer",true).Where(e=>Dimension(e["payload"])==d && S(e["payload"]?["role"]) is "training" || Dimension(e["payload"])==d && S(e["payload"]?["role"])==stage));
        return Hash(service.Store.Generation+":"+string.Join("\n",events.Select(e=>S(e["eventId"])).Order(StringComparer.Ordinal)));
    }
    string Dependencies(string d)
    {
        var store=service.Store;
        var manifests=store.Current(Dataset,"manifest").Where(e=>S(e["entityId"]) is "study-v1" or "global-seal-exposure").ToArray();
        var ids=manifests.SelectMany(e=>new[]{"training","development","sealed_test"}.SelectMany(k=>ReadStrings(e["payload"]?[k])))
            .Concat(store.Current(Dataset,"answer",true).Where(e=>Dimension(e["payload"])==d).Select(e=>S(e["payload"]?["recordingId"]))).ToHashSet();
        return Hash(store.Generation+":"+AnswerRevision(d)+":"+string.Join("\n",manifests.Concat(store.Current(Dataset,"recording").Where(e=>ids.Contains(S(e["entityId"])))) .Select(e=>S(e["eventId"])).Order(StringComparer.Ordinal)));
    }
    bool IndependentScopeValid(string d)
    {
        var study=service.Store.Current(Dataset,"manifest").SingleOrDefault(e=>S(e["entityId"])=="study-v1")?["payload"]?.AsObject();
        if(study is null || !SupportedHeads(study).Contains(d) || study["independent"]?.GetValue<bool>()!=true) return false;
        var records=service.Store.Current(Dataset,"recording");
        if(records.GroupBy(e=>S(e["entityId"])).Any(g=>g.Count()>1)) return false;
        var identities=records.Select(e=> { var r=e["payload"]!; return new Identity(S(e["entityId"]),S(r["contentSignature"]),S(r["workGroup"]),S(r["albumGroup"]),ReadStrings(r["artists"]),r["identityReviewed"]?.GetValue<bool>()==true,r["exposed"]?.GetValue<bool>()==true,r["supported"]?.GetValue<bool>()==true); }).ToArray();
        var groups=Group(identities);
        var used=new[]{"training","development","sealed_test"}.SelectMany(k=>ReadStrings(study[k])).ToHashSet();
        var relevant=identities.Where(r=>used.Contains(r.Id)).Select(r=>groups.Components[r.Id]).ToHashSet();
        return !PreferenceStudy.Contaminated(study,identities.Where(r=>relevant.Contains(groups.Components[r.Id])).ToArray());
    }
    internal void InitializeDeployment()
    {
        app.SmartTags.PersonalResults=ResultsFor;
        RefreshDeployment();
    }
    internal void RefreshDeployment()
    {
        var next=new Dictionary<string,JsonObject>();
        foreach(var d in Dimensions)
        {
            try
            {
                if(service.Store.Local("active-head:"+d) is not { } json || service.Synthetic(Dataset)) continue;
                var entry=JsonNode.Parse(json)!.AsObject();
                var artifact=entry["artifact"]!.AsObject();
                var metadata=service.Store.Current(Dataset,"model").Where(e=>S(e["entityId"])==S(entry["candidateId"])).ToArray();
                if(!VersionScopeValid(artifact, active:true) || !IndependentScopeValid(d) || S(entry["dependencies"])!=Dependencies(d) || S(entry["workerHash"])!=FileHash(Worker) || S(entry["contractHash"])!=Hash(HeadContract().ToJsonString()) ||
                    S(entry["version"])!=Hash(artifact.ToJsonString()) || metadata.Length!=1 || S(metadata[0]["payload"]?["state"])!="active" || S(metadata[0]["payload"]?["version"])!=S(entry["version"])) continue;
                new PersonalHead(artifact).VerifyEquivalence(artifact["deploymentProbes"]!.AsArray());
                next[d]=entry;
            }
            catch(Exception e) when(e is IOException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { /* fail closed; public rules remain available */ }
        }
        bool changed;
        lock(deploymentGate) { changed=string.Join(";",activeHeads.Select(kv=>kv.Key+S(kv.Value["version"])))!=string.Join(";",next.Select(kv=>kv.Key+S(kv.Value["version"]))); activeHeads=next; }
        if(changed) _=Task.Run(()=>app.SmartTags.Recompute());
    }
    public bool Enabled(string d) { lock(deploymentGate) return activeHeads.ContainsKey(d); }
    static bool VersionScopeValid(JsonObject artifact,bool active=false)=>S(artifact["scope"])=="whole_recording" && artifact["analyzerVersion"]?.GetValue<int>()==JeekMusic.Core.Analysis.AnalysisVersions.AnalyzerVersion && (artifact["publicRulesVersion"]?.GetValue<int>()==JeekMusic.Core.Analysis.AnalysisVersions.RulesVersion || active && artifact["publicRulesVersion"]?.GetValue<int>()==9);
    public JsonObject DeploymentStatus()
    {
        lock(deploymentGate) return new JsonObject { ["heads"]=new JsonArray(Dimensions.Select(d=>(JsonNode)new JsonObject { ["dimension"]=d,["enabled"]=activeHeads.ContainsKey(d),["version"]=activeHeads.TryGetValue(d,out var a)?S(a["version"]):null,["blocker"]=activeHeads.ContainsKey(d)?null:service.Store.Local("learning-blocker:"+d) }).ToArray()),["scope"]="exact_recording_whole_windows_cpu_results",["results"]=service.Store.LocalRows("personal-result:",100000).Count };
    }
    internal void Activate(string dimension)
    {
        var generation=service.Store.Generation;
        if(service.Synthetic(Dataset)) throw new InvalidOperationException("Synthetic data can never activate a user model");
        if(!IndependentScopeValid(dimension)) throw new InvalidOperationException("Independent identity scope missing or contaminated");
        var candidates=service.Store.Current(Dataset,"model").Where(e=>S(e["payload"]?["category"])!="job" && Dimension(e["payload"])==dimension && S(e["payload"]?["state"])=="pass_research_gate").ToArray();
        if(candidates.Length!=1) throw new InvalidOperationException("Exactly one locally verified head required");
        var candidate=candidates[0]; var payload=candidate["payload"]!.DeepClone().AsObject(); var trainingHash=S(payload["trainingSetHash"]);
        var artifact=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"candidates",trainingHash+".json")))!.AsObject();
        var dependency=Dependencies(dimension);
        if(!VersionScopeValid(artifact) || S(artifact["profileId"])!=Dataset || S(artifact["state"])!="training_gate_passed" || artifact["selfSelectedPilot"]?.GetValue<bool>()==true || S(artifact["dimension"])!=dimension || S(artifact["trainingSetHash"])!=trainingHash || S(artifact["headProtocolHash"])!=Hash(HeadContract().ToJsonString()) || S(artifact["workerHash"])!=FileHash(Worker)) throw new InvalidDataException("Artifact provenance mismatch");
        foreach(var stage in dimension=="MOOD"?new[]{"development","calibration_regression","sealed_test"}:new[]{"development","sealed_test"})
        {
            var receipt=service.Store.Local("passed:"+dimension+":"+stage) is { } raw?JsonNode.Parse(raw):null;
            if(S(receipt?["trainingHash"])!=trainingHash || S(receipt?["artifactHash"])!=Hash(artifact.ToJsonString()) || S(receipt?["dependencies"])!=ReceiptDependencies(dimension,stage)) throw new InvalidOperationException("Local independent-check receipts missing or stale");
        }
        var model=new PersonalHead(artifact); model.VerifyEquivalence(artifact["deploymentProbes"]!.AsArray());
        var timer=System.Diagnostics.Stopwatch.StartNew();
        foreach(var probe in artifact["deploymentProbes"]!.AsArray()) model.Decide(probe!["vector"]!);
        if(timer.Elapsed>TimeSpan.FromSeconds(2)) throw new InvalidOperationException("Production head CPU budget exceeded");
        var version=Hash(artifact.ToJsonString());
        var entry=new JsonObject { ["version"]=version,["candidateId"]=S(candidate["entityId"]),["dependencies"]=dependency,["workerHash"]=FileHash(Worker),["contractHash"]=Hash(HeadContract().ToJsonString()),["artifact"]=artifact.DeepClone() };
        // Metadata alone never activates. Evidence, metadata and the local pointer commit together.
        payload["state"]="active"; payload["enabled"]=true; payload["version"]=version; payload["scope"]="windows_exact_recording_results";
        service.Store.AppendAndSetLocal(Dataset,S(candidate["entityId"]),"model",payload,new Dictionary<string,string> { ["active-head:"+dimension]=entry.ToJsonString() },generation,()=>dependency==Dependencies(dimension) && IndependentScopeValid(dimension));
        RefreshDeployment();
    }
    public JsonObject ResultsFor(Track track)
    {
        var result=new JsonObject();
        if(track.IsCue || service.Store.Local("personal-result:"+track.TrackKey) is not { } raw) return result;
        try
        {
            var saved=JsonNode.Parse(raw)!;
            if(S(saved["signature"])!=AnalysisFeature.Signature(track) || app.Library.PathOf(track) is not { } path) return result;
            var file=new FileInfo(path); if(!file.Exists || file.Length!=track.Size || file.LastWriteTimeUtc.Ticks!=track.Mtime) return result;
            lock(deploymentGate) foreach(var d in Dimensions)
                if(activeHeads.TryGetValue(d,out var active) && S(saved["heads"]?[d]?["version"])==S(active["version"]) && Binary(S(saved["heads"]?[d]?["value"]),d)) result[d]=saved["heads"]![d]!.DeepClone();
        }
        catch(Exception e) when(e is IOException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { }
        return result;
    }
    // Authenticated paired transport carries data-only results, never weights, vectors or code.
    public JsonObject ResultPage(int offset=0,string? expectedVersion=null,CancellationToken cancel=default)
    {
        cancel.ThrowIfCancellationRequested();
        if(offset<0) throw new ArgumentException("Invalid result cursor");
        var deployment=DeploymentStatus(); var heads=deployment["heads"]!.AsArray();
        foreach(var head in heads) head!["answerRevision"]=AnswerRevision(S(head["dimension"]));
        var all=service.Store.LocalRows("personal-result:",100000).OrderBy(kv=>kv.Key,StringComparer.Ordinal).ToArray();
        var version=Hash(service.Store.Generation+heads.ToJsonString()+string.Join("\n",all.Select(kv=> { cancel.ThrowIfCancellationRequested(); return kv.Key+":"+Hash(kv.Value); })));
        if(offset>0 && expectedVersion!=version) throw new InvalidOperationException("Active versions/results changed; restart result sync");
        var rows=new JsonArray();
        foreach(var (_,raw) in all.Skip(offset).Take(100))
        {
            cancel.ThrowIfCancellationRequested();
            var saved=JsonNode.Parse(raw)!; var track=app.Library.ByKey(S(saved["trackKey"]));
            if(track is null) continue;
            rows.Add((JsonNode)new JsonObject { ["contentSignature"]=S(saved["contentSignature"]),["durationMs"]=track.DurationMs,["heads"]=ResultsFor(track) });
        }
        return new JsonObject { ["protocol"]=3,["resultProtocol"]=1,["scope"]="sha256_whole_recording",["analyzerVersion"]=JeekMusic.Core.Analysis.AnalysisVersions.AnalyzerVersion,["rulesVersion"]=JeekMusic.Core.Analysis.AnalysisVersions.RulesVersion,["generation"]=service.Store.Generation,["version"]=version,["heads"]=heads.DeepClone(),["rows"]=rows,["next"]=Math.Min(offset+100,all.Length),["hasMore"]=offset+100<all.Length };
    }
}
