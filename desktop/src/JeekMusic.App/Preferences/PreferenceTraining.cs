using System.Diagnostics;
using System.Text.Json.Nodes;
using JeekMusic.Infrastructure;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

/// <summary>Optional CPU worker; no Python/torch is loaded into the player or NativeAOT process.</summary>
public sealed partial class PreferenceTraining : IDisposable
{
    readonly PreferenceService service;
    readonly AppServices app;
    Task? jobTask;
    string jobDimension="MOOD", jobProfile="";
    readonly Dictionary<string,JsonObject> headStates=new();
    readonly Lock gate=new();
    readonly string root=Path.Combine(AppPaths.DataDir,"preference-components");
    CancellationTokenSource? cancel;
    JsonObject state=new() { ["state"]="idle" };
    string Worker=>Path.Combine(AppPaths.AppDir,"Preferences","Runtime","preference_worker.py");
    string Python=>Path.Combine(root,"venv","Scripts","python.exe");
    public PreferenceTraining(PreferenceService service,AppServices app,bool isolated=false)
    {
        this.service=service; this.app=app; if(isolated) root=Path.Combine(Path.GetTempPath(),"jeek-preference-test-"+Guid.NewGuid().ToString("N"));
        foreach(var profile in service.Store.Profiles().Where(p=>!service.Deleted(p)))
            foreach(var e in service.Store.Current(profile,"model").Where(e=>S(e["payload"]?["category"])=="job" && S(e["payload"]?["state"]) is "running" or "preparing_components" or "checking_material"))
            {
                var interrupted=e["payload"]!.DeepClone().AsObject(); interrupted["state"]="interrupted"; interrupted["detail"]="Previous worker stopped; completed per-recording cache can resume by explicit request";
                service.Store.Append(profile,S(e["entityId"]),"model",interrupted,true);
            }
    }
    public JsonObject Status(string? dimension=null) { lock(gate) { var d=dimension??service.ActiveDimension; var s=(JsonObject)(headStates.GetValueOrDefault(service.ActiveProfile+":"+d) ?? service.Store.Current(service.ActiveProfile,"model").SingleOrDefault(e=>S(e["entityId"])=="job:"+d)?["payload"]?.AsObject() ?? new JsonObject { ["state"]="idle" }).DeepClone(); s["dimension"]=d; s["runningDimension"]=cancel is null ? null : jobDimension; s["componentsReady"]=File.Exists(Path.Combine(root,"ready.json")); s["computePlatform"]="windows_cpu_optional_worker"; s["maxRecordings"]=6; s["maxThreads"]=2; s["budgetSeconds"]=240; s["maxWorkerBytes"]=2L*1024*1024*1024; s["modelEnabled"]=false; s["activationBlocker"]="independent_and_deployment_equivalence_gates_required"; return s; } }
    void Set(string status,string? detail=null)
    {
        lock(gate) { state=new JsonObject { ["dimension"]=jobDimension,["state"]=status,["detail"]=detail??"",["updatedAt"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }; headStates[jobProfile+":"+jobDimension]=state; }
        if(jobProfile.Length>0 && service.Store.Profiles().Contains(jobProfile) && !service.Deleted(jobProfile))
            service.Store.Append(jobProfile,"job:"+jobDimension,"model",new JsonObject { ["dimension"]=jobDimension,["category"]="job",["state"]=status,["detail"]=detail??"",["platform"]="windows_cpu",["enabled"]=false },true);
        service.Notify();
    }
    public void Invalidate(string? dimension=null,string? profileId=null)
    {
        var profile=profileId??service.ActiveProfile;
        service.Store.SetLocal("validatedModel","");
        foreach(var e in service.Store.Current(profile,"model").Where(e=>(dimension==null || Dimension(e["payload"])==dimension) &&S(e["payload"]?["state"]) is "development_required" or "regression_required" or "independent_test_required" or "pass_research_gate"))
        {
            var p=e["payload"]!.DeepClone().AsObject(); p["state"]="invalidated"; p["enabled"]=false;
            service.Store.Append(profile,S(e["entityId"]),"model",p,true);
        }
        lock(gate) { if(profile==jobProfile && (dimension==null || dimension==jobDimension) && S(state["state"]) is "training_gate_passed" or "completed") state["state"]="invalidated"; }
    }
    public void Cancel(string? dimension=null) { lock(gate) { if(dimension!=null && cancel!=null && dimension!=jobDimension) throw new InvalidOperationException("Another independent head owns the running worker"); cancel?.Cancel(); } }
    public JsonObject Start(string action,string? dimension=null)
    {
        if(action is not ("prepare" or "train" or "extract" or "evaluate" or "regression_extract")) throw new ArgumentException("Invalid training action");
        var selectedDimension=dimension??service.ActiveDimension; if(!Dimensions.Contains(selectedDimension)) throw new ArgumentException("Unknown dimension");
        var selectedProfile=service.ActiveProfile;
        lock(gate)
        {
            if(cancel!=null) throw new InvalidOperationException("Training/component job already running");
            cancel=new();
        }
        jobDimension=selectedDimension; jobProfile=selectedProfile;
        Set(action=="prepare" ? "preparing_components" : "checking_material");
        var token=cancel.Token; jobTask=Task.Run(()=>Run(action,jobDimension,selectedProfile,token)); return Status(jobDimension);
    }
    async Task Run(string action,string dimension,string profile,CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(root);
            var snapshot=new JsonObject();
            if(action!="prepare")
            {
                if(action=="regression_extract" && dimension!="MOOD") { Set("not_applicable","旧11反馈仅有运动维标签，其他维度不复用其真值。"); return; }
                if(string.IsNullOrEmpty(profile) || service.Deleted(profile)) { Set("insufficient_material","请先创建档案并明确标注。旧反馈、自动标签和未标注项不会作为训练答案。"); return; }
                var answers=service.Store.Current(profile,"answer").GroupBy(e=>S(e["entityId"])).Where(g=>g.Count()==1).Select(g=>g.Single()).Where(e=>Dimension(e["payload"])==dimension && UsableAnswer(e)).ToList();
                if(action=="train" && (answers.Count<(dimension=="MOOD"?16:12) || answers.Count(e=>S(e["payload"]?["answer"])==Labels(dimension).Positive)<(dimension=="MOOD"?8:6) || answers.Count(e=>S(e["payload"]?["answer"])==Labels(dimension).Negative)<(dimension=="MOOD"?8:6)))
                { Set("insufficient_material",dimension=="MOOD"?"运动：至少16条整曲答案、每类8条、8组件、3个双类艺人；24/36/48尝试节点。":"当前维度：至少12条整曲答案、每类6条、6组件；12/24/36尝试节点。其他维未标不会补值。"); return; }
                if(!File.Exists(Path.Combine(root,"ready.json"))) { Set("components_required","按需准备官方 CLAP（权重约615 MB）及独立 CPU Python 运行时；不会下载或上传歌曲。"); return; }
                if(action is "train" or "extract" && !service.Store.Current(profile,"manifest").Any(e=>S(e["entityId"])=="study-v1")) { Set("identity_incomplete",dimension=="MOOD"?"首次新录音表示查询前需完整核验并冻结训练48/开发12/封存48清单；六首试行不运行模型。":"Freeze this head’s complete shared identity frame before extraction: training12/24/36, development8, sealed24; workout’s larger frame is not required."); return; }
                var study=service.Store.Current(profile,"manifest").SingleOrDefault(e=>S(e["entityId"])=="study-v1")?["payload"];
                if(study!=null && !SupportedHeads(study).Contains(dimension)) { Set("protocol_incomplete_for_head","This compact shared study covers the other three independent heads; MOOD requires its own larger prospective budget. No other head is blocked."); return; }
                snapshot=action=="evaluate" ? EvaluationSnapshot(dimension,profile) : action=="regression_extract" ? RegressionSnapshot(true,profile) : Snapshot(action,answers,dimension,profile);
                if(snapshot["rows"]!.AsArray().Count==0) { Set("insufficient_material","没有已核验身份、支持的本地录音或完整训练向量。训练仅接收 training 侧明确整曲答案。"); return; }
            }
            var job=Path.Combine(root,"job-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
            var input=Path.Combine(job,"input.json"); var output=Path.Combine(job,"result.json");
            await File.WriteAllTextAsync(input,snapshot.ToJsonString(),ct);
            var before=Hash(snapshot.ToJsonString());
            var start=new ProcessStartInfo(action=="prepare" ? "python" : Python) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true };
            foreach(var arg in new[] { Worker,action=="regression_extract"?"extract":action,"--root",root,"--input",input,"--output",output }) start.ArgumentList.Add(arg);
            using var process=Process.Start(start) ?? throw new IOException("Unable to start optional training worker");
            using var budget=action=="prepare" ? null : new WorkerBudget(process);
            // Drain without retaining private paths/audio/answers in diagnostics.
            var error=process.StandardError.ReadToEndAsync(ct); var stdout=process.StandardOutput.ReadToEndAsync(ct);
            Set(action=="prepare" ? "preparing_components" : "running");
            var timer=Stopwatch.StartNew(); string? stopped=null;
            try
            {
                while(!process.HasExited)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        process.Refresh();
                        if(action!="prepare" && !process.HasExited && process.PeakWorkingSet64>2L*1024*1024*1024) { stopped="memory_budget_exhausted"; break; }
                    }
                    catch(InvalidOperationException) when(process.HasExited) { break; }
                    if(timer.Elapsed>TimeSpan.FromSeconds(action=="prepare"?3600:240)) { stopped="time_budget_exhausted"; break; }
                    await Task.Delay(100,ct);
                }
            }
            finally { if(!process.HasExited) { process.Kill(entireProcessTree:true); await process.WaitForExitAsync(CancellationToken.None); } }
            await Task.WhenAll(error,stdout);
            await File.WriteAllTextAsync(Path.Combine(job,"worker.log"),await error,CancellationToken.None);
            if(stopped!=null) { Set("incomplete",stopped); return; }
            if(File.Exists(output) && process.ExitCode!=0)
            {
                var failure=JsonNode.Parse(await File.ReadAllTextAsync(output,ct));
                if(S(failure?["errorType"]) is "TimeoutError" or "MemoryError" || S(failure?["error"]).Contains("PCA rank")) { Set("incomplete","Worker resource budget or training rank requirement unmet; no candidate committed"); return; }
            }
            if(!File.Exists(output) || process.ExitCode!=0) { Set("failed","组件准备或有界工作进程失败；检查本机私有任务日志/依赖及格式，不替换旧候选。"); return; }
            var result=JsonNode.Parse(await File.ReadAllTextAsync(output,ct))!.AsObject();
            if(action=="evaluate") CommitEvaluation(snapshot,result);
            if(action is "extract" or "regression_extract")
                foreach(var row in result["rows"]!.AsArray())
                    service.Store.SetLocal("vector:"+S(row?["recordingId"]),new JsonObject { ["vector"]=row!["vector"]!.DeepClone(),["perturbations"]=row["perturbations"]!.DeepClone(),["extractor"]=Extractor,["workerHash"]=FileHash(Worker),["contentSignature"]=S(row["contentSignature"]) }.ToJsonString());
            if(action=="train")
            {
                // Recompute snapshot after the worker; withdrawal/sync/conflict while running invalidates this artifact.
                var fresh=service.Store.Current(profile,"answer").GroupBy(e=>S(e["entityId"])).Where(g=>g.Count()==1).Select(g=>g.Single()).Where(e=>Dimension(e["payload"])==dimension && UsableAnswer(e)).ToList();
                if(service.ActiveProfile!=profile || service.Deleted(profile) || before!=Hash(Snapshot("train",fresh,dimension!,profile).ToJsonString())) { Set("invalidated","标注、身份或录音在训练期间发生变化。"); return; }
                result["dimension"]=dimension; result["trainingSetHash"]=before; result["extractor"]=Extractor; result["profileId"]=profile;
                if(S(result["state"])!="training_gate_passed")
                    service.Store.Append(profile,"training-node-"+dimension+"-"+snapshot["attempted"],"model",new JsonObject { ["dimension"]=dimension,["state"]="training_node_failed",["attempted"]=snapshot["attempted"]!.DeepClone(),["trainingSetHash"]=before,["enabled"]=false },true);
                if(S(result["state"])=="training_gate_passed" && !service.Synthetic(profile))
                {
                    var directory=Path.Combine(root,"candidates"); Directory.CreateDirectory(directory);
                    var candidatePath=Path.Combine(directory,before+".json");
                    await File.WriteAllTextAsync(candidatePath+".tmp",result.ToJsonString(),ct);
                    ct.ThrowIfCancellationRequested(); File.Move(candidatePath+".tmp",candidatePath,true);
                    service.Store.Append(profile,Guid.NewGuid().ToString("N"),"model",new JsonObject { ["dimension"]=dimension,["state"]="development_required",["trainingSetHash"]=before,["extractor"]=Extractor,["platform"]="windows_cpu_candidate",["enabled"]=false });
                }
            }
            var temp=Path.Combine(root,"last-result.json.tmp"); await File.WriteAllTextAsync(temp,result.ToJsonString(),ct); File.Move(temp,Path.Combine(root,"last-result.json"),true);
            if(action=="train" && service.Synthetic(profile)) { result["syntheticFixture"]=true; Set("synthetic_validation_complete",S(result["state"])+"; synthetic labels are not user ground truth and can never enable a head"); } else Set(S(result["state"]));
        }
        catch(OperationCanceledException) { Set("cancelled","已完成的单录音缓存保留；部分结果不提交。"); }
        catch(Exception e) when(e is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException) { Set("failed",e is System.ComponentModel.Win32Exception ? "需要本机 Python 3.13+ 和 FFmpeg；按需准备会创建私有运行时。" : e is IOException ? "local_io_failure; previous candidate retained; inspect private job log" : e.Message); }
        finally { lock(gate) { cancel?.Dispose(); cancel=null; } service.Notify(); }
    }
    JsonObject Snapshot(string action,List<JsonObject> answers,string dimension,string profile)
    {
        var recordings=service.Store.Current(profile,"recording").GroupBy(e=>S(e["entityId"])).Where(g=>g.Count()==1).Select(g=>g.Single()).ToDictionary(e=>S(e["entityId"]),e=>e["payload"]!.AsObject());
        var identities=recordings.Select(kv=>new JeekMusic.Core.Preferences.PreferenceProtocol.Identity(kv.Key,S(kv.Value["contentSignature"]),S(kv.Value["workGroup"]),S(kv.Value["albumGroup"]),ReadStrings(kv.Value["artists"]),kv.Value["identityReviewed"]?.GetValue<bool>()==true,kv.Value["exposed"]?.GetValue<bool>()==true,true)).ToArray();
        var grouping=Group(identities);
        var role=action=="extract" ? S(service.Store.Current(profile,"manifest").FirstOrDefault(e=>S(e["entityId"])==S(service.Batch(dimension:dimension)["manifestId"]))?["payload"]?["role"]) : "training";
        var ids=action=="extract" ? service.Store.Current(profile,"manifest").Where(e=>Dimension(e["payload"])==dimension && S(e["payload"]?["role"])==role).OrderBy(e=>e["at"]!.GetValue<long>()).SelectMany(e=>ReadStrings(e["payload"]?["items"])).Distinct().ToArray() : answers.Select(e=>S(e["payload"]?["recordingId"])).ToArray();
        var rows=new JsonArray();
        foreach(var id in ids.Order(StringComparer.Ordinal))
        {
            if(!recordings.TryGetValue(id,out var r) || service.FindLocal(id) is not { } t) continue;
            if(action=="extract" && service.Store.Local("vector:"+id) is { } saved)
            {
                var cached=JsonNode.Parse(saved);
                if(S(cached?["extractor"])==Extractor && S(cached?["workerHash"])==FileHash(Worker) && S(cached?["contentSignature"])==S(r["contentSignature"])) continue;
            }
            if(action=="train" && (r["identityReviewed"]?.GetValue<bool>()!=true || r["exposed"]?.GetValue<bool>()==true)) continue;
            // Frozen production model segment centers; no new model scoring or automatic answer inference.
            var centers=new List<long>();
            foreach(var segment in JeekMusic.Core.Analysis.PcmMath.Windows(0,t.DurationMs))
            {
                var center=(segment.StartMs+segment.EndMs)/2;
                if(center<15000 || center>t.DurationMs-15000 || centers.Any(c=>Math.Abs(c-center)<30000)) continue;
                centers.Add(center);
            }
            if(centers.Count!=3) continue;
            var answer=answers.SingleOrDefault(e=>S(e["payload"]?["recordingId"])==id);
            var row=new JsonObject { ["recordingId"]=id,["contentSignature"]=S(r["contentSignature"]),["path"]=app.Library.PathOf(t),["cue"]=false,["centersMs"]=new JsonArray(centers.Select(c=>(JsonNode?)JsonValue.Create(c)).ToArray()),
                ["dimension"]=dimension,["componentId"]=grouping.Components[id],["artists"]=r["artists"]!.DeepClone(),["identityReviewed"]=r["identityReviewed"]?.GetValue<bool>()==true,["exposed"]=r["exposed"]?.GetValue<bool>()==true,["role"]=role,
                ["labelSource"]=S(answer?["payload"]?["source"]),["label"]=S(answer?["payload"]?["answer"]),["answerRevision"]=S(answer?["eventId"]),["rules"]=S(app.SmartTags.Explain(t)["automatic"]?[dimension]) };
            if(action=="train")
            {
                var cached=service.Store.Local("vector:"+id);
                var vector=cached==null?null:JsonNode.Parse(cached);
                if(S(vector?["workerHash"])!=FileHash(Worker) || S(vector?["extractor"])!=Extractor || S(vector?["contentSignature"])!=S(r["contentSignature"])) continue;
                row["vector"]=vector!["vector"]!.DeepClone(); row.Remove("path");
            }
            rows.Add((JsonNode)row); if(action=="extract" && rows.Count==6) break;
        }
        return new JsonObject { ["headProtocolHash"]=Hash(HeadContract().ToJsonString()),["syntheticFixture"]=service.Synthetic(profile),["dimension"]=dimension,["workerHash"]=FileHash(Worker),["profileId"]=profile,["rows"]=rows,["attempted"]=service.Store.Current(profile,"manifest").Where(e=>Dimension(e["payload"])==dimension && S(e["payload"]?["role"])=="training").SelectMany(e=>ReadStrings(e["payload"]?["items"])).Distinct().Count(),["extractor"]=Extractor };
    }
    public void PurgeProfile(string profile)
    {
        if(jobProfile==profile) { Cancel(); try { jobTask?.Wait(TimeSpan.FromSeconds(5)); } catch(AggregateException) { } }
        if(!Directory.Exists(root)) return;
        foreach(var dir in Directory.GetDirectories(root,"job-*"))
        {
            if(new DirectoryInfo(dir).LinkTarget!=null) continue;
            var input=Path.Combine(dir,"input.json");
            try { if(File.Exists(input) && S(JsonNode.Parse(File.ReadAllText(input))?["profileId"])==profile) Directory.Delete(dir,true); } catch(IOException) { }
        }
        var candidates=Path.Combine(root,"candidates");
        foreach(var file in (Directory.Exists(candidates)?Directory.GetFiles(candidates,"*.json"):[]).Concat(new[] {Path.Combine(root,"last-result.json")}))
            try { if(File.Exists(file) && S(JsonNode.Parse(File.ReadAllText(file))?["profileId"])==profile) File.Delete(file); } catch(IOException) { }
    }
    static string FileHash(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    public void Dispose() { Cancel(); try { jobTask?.Wait(TimeSpan.FromSeconds(5)); } catch(AggregateException) { } }
}
