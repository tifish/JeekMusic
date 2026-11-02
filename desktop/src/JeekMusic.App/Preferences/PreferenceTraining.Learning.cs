using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using JeekMusic.Analysis;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

public sealed partial class PreferenceTraining
{
    bool learning;
    string? remoteOwner;
    public void CancelOwned(string owner) { lock (gate) { if (remoteOwner == owner && cancel is not null) Cancel(); } }
    public string? RemoteOwner { get { lock (gate) return cancel is null ? null : remoteOwner; } }
    int maintenanceStarted;
    internal void Maintain()
    {
        try
        {
            var saved=service.Store.Local("learning-workflow") is { } raw?JsonNode.Parse(raw):null;
            if(Interlocked.Exchange(ref maintenanceStarted,1)==0 && S(saved?["state"]) is "checking_material" or "learning" or "updating_library" or "preparing_components" or "waiting_playback")
            {
                var recoveries=int.TryParse(service.Store.Local("learning-recoveries"),out var n)?n:0;
                if(service.Store.Local("learning-remote-owner") is { Length: > 0 }) { jobGeneration=service.Store.Generation; LearningSet("interrupted","远程任务在重启后停止。请在授权设备或本机重新开始。"); return; }
                if(recoveries<3) { service.Store.SetLocal("learning-recoveries",(recoveries+1).ToString()); StartLearning(automatic:true); }
                else { jobGeneration=service.Store.Generation; LearningSet("interrupted","连续中断已停止自动重试。点击继续学习，可从已完成分析续接。"); }
                return;
            }
            if(!Dimensions.Any(Enabled) || service.Store.Local("learning-user-stopped")=="1") return;
            var tracks=app.Library.Snapshot.Tracks.OrderBy(t=>t.TrackKey,StringComparer.Ordinal).ToArray(); if(tracks.Length==0) return;
            var frame=LibraryFrame(tracks); if(service.Store.Local("library-auto-attempt")==frame) return;
            lock(gate) { if(cancel!=null || quiescing) return; remoteOwner=null; cancel=new(); learning=true; jobProfile=Dataset; jobGeneration=service.Store.Generation; jobDimension=Dimensions.First(Enabled);
                service.Store.SetLocalForGeneration("library-auto-attempt",frame,jobGeneration);
                var token=cancel.Token;
                jobTask=Task.Run(async()=> {
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromHours(6));
                    try { await UpdateLibrary(deadline.Token); }
                    catch(OperationCanceledException) { if(jobGeneration==service.Store.Generation) LearningFail(LearningFailure.Create(token.IsCancellationRequested?"cancelled":"time_budget","update_library",jobDimension),token.IsCancellationRequested?"cancelled":"incomplete"); }
                    catch(Exception e) when(e is IOException or InvalidOperationException or ArgumentException) { if(jobGeneration==service.Store.Generation) LearningFail(LearningFailure.FromException(e,"update_library",jobDimension)); }
                    finally { lock(gate) { learning=false; cancel?.Dispose(); cancel=null; } service.Notify(); }
                });
            }
        }
        catch(Exception e) when(e is IOException or InvalidOperationException or ArgumentException) { }
    }
    public JsonObject LearningStatus()
    {
        var saved=service.Store.Local("learning-workflow");
        var s=saved is null?new JsonObject { ["state"]="idle" }:JsonNode.Parse(saved)!.AsObject();
        lock(gate) { s["running"]=learning && cancel!=null; if(!learning && S(s["state"]) is "checking_material" or "learning" or "updating_library" or "preparing_components" or "waiting_playback") s["state"]="interrupted"; }
        s["failure"]=WorkflowFailure(s); if(s["failure"] is { } reason) s["detail"]=LearningFailure.Summary(reason); s["stage"]=s["failure"]?["stage"]?.DeepClone() ?? s["stage"]?.DeepClone();
        s["remoteOwner"]=RemoteOwner; s["componentsReady"]=File.Exists(Path.Combine(root,"ready.json")); s["deployment"]=DeploymentStatus();
        return s;
    }
    void LearningSet(string status,string detail,int processed=0,int total=0,int uncovered=0,JsonObject? failure=null)
    {
        failure??=FailureForState(status) is { } code?LearningFailure.Create(code,jobStage,jobDimension):null;
        service.Store.SetLocalForGeneration("learning-workflow",new JsonObject { ["state"]=status,["detail"]=detail,["stage"]=jobStage,["failure"]=failure?.DeepClone(),["processed"]=processed,["total"]=total,["uncovered"]=uncovered,["updatedAt"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }.ToJsonString(),jobGeneration);
        service.Notify();
    }
    public JsonObject StartLearning(bool downloadConfirmed=false,bool automatic=false,string? owner=null,bool allowDownload=true)
    {
        var consent=FileHash(Path.Combine(Path.GetDirectoryName(Worker)!,"components.json"));
        lock(gate)
        {
            if(cancel!=null || quiescing) throw new InvalidOperationException("学习正在进行或停止，请等待或取消。");
            remoteOwner=owner;
            service.Store.SetLocal("learning-remote-owner",owner??"");
            jobProfile=Dataset; jobGeneration=service.Store.Generation; jobDimension=service.ActiveDimension; jobStage="check_material";
            learning=true; cancel=new();
            if(!automatic) service.Store.SetLocal("learning-recoveries","0");
            if(!automatic) service.Store.RemoveLocal("learning-user-stopped");
            if(downloadConfirmed) service.Store.SetLocalForGeneration("learning-download-consent",consent,jobGeneration);
            downloadConfirmed=allowDownload && (downloadConfirmed || service.Store.Local("learning-download-consent")==consent);
            LearningSet("checking_material","正在检查四个维度的标注和本机录音。");
            var token=cancel.Token; jobTask=Task.Run(()=>Learn(downloadConfirmed,token));
            return LearningStatus();
        }
    }
    internal JsonObject Material(string d)
    {
        var rows=TrainingRows(service.Store.Current(Dataset,"answer")).Where(e=>Dimension(e["payload"])==d).ToArray();
        var study=service.Store.Current(Dataset,"manifest").Any(e=>S(e["entityId"])=="study-v1");
        if(!study && rows.Any(e=>S(e["payload"]?["source"])=="explicit_daily_tag_correction"))
        {
            rows=rows.Where(e=>S(e["payload"]?["source"])=="explicit_daily_tag_correction").OrderBy(e=>S(e["payload"]?["recordingId"]),StringComparer.Ordinal).ToArray();
            var nodes=HeadContract()["heads"]![d]!["nodes"]!.AsArray().Select(n=>n!.GetValue<int>()).ToArray();
            rows=rows.Take(nodes.Where(n=>n<=rows.Length).DefaultIfEmpty(rows.Length).Max()).ToArray();
        }
        var h=HeadContract()["heads"]![d]!; var positive=rows.Count(e=>S(e["payload"]?["answer"])==Labels(d).Positive); var negative=rows.Length-positive;
        var minimum=study?h["minimum"]!.GetValue<int>():Math.Max(h["minimum"]!.GetValue<int>(),h["nodes"]![0]!.GetValue<int>());
        var records=service.Store.Current(Dataset,"recording").GroupBy(e=>S(e["entityId"])).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single()["payload"]!);
        var graph=Group(records.Select(kv=>new JeekMusic.Core.Preferences.PreferenceProtocol.Identity(kv.Key,S(kv.Value["contentSignature"]),S(kv.Value["workGroup"]),S(kv.Value["albumGroup"]),ReadStrings(kv.Value["artists"]),kv.Value["identityReviewed"]?.GetValue<bool>()==true,kv.Value["exposed"]?.GetValue<bool>()==true,true)).ToArray());
        var ids=rows.Select(e=>S(e["payload"]?["recordingId"])).ToHashSet();
        var components=ids.Where(graph.Components.ContainsKey).Select(id=>graph.Components[id]).Distinct().Count();
        var missingLocal=ids.Count(id=>service.FindLocal(id) is null);
        var artists=ids.Where(records.ContainsKey).SelectMany(id=>ReadStrings(records[id]["artists"])).Distinct();
        var dual=artists.Count(a=>rows.Where(e=>records.TryGetValue(S(e["payload"]?["recordingId"]),out var r) && ReadStrings(r["artists"]).Contains(a)).Select(e=>S(e["payload"]?["answer"])).Distinct().Count()==2);
        var missingComponents=Math.Max(0,h["components"]!.GetValue<int>()-components); var missingDual=d=="MOOD"?Math.Max(0,3-dual):0;
        var enough=rows.Length>=minimum && Math.Min(positive,negative)>=h["per_class"]!.GetValue<int>() && missingComponents==0 && missingDual==0 && missingLocal==0;
        return new JsonObject { ["dimension"]=d,["available"]=rows.Length,["positive"]=positive,["negative"]=negative,["ready"]=enough,
            ["missingLocal"]=missingLocal,["components"]=components,["missingComponents"]=missingComponents,["missingDualArtists"]=missingDual,["missingTotal"]=Math.Max(Math.Max(0,minimum-rows.Length),Math.Max(0,h["per_class"]!.GetValue<int>()-positive)+Math.Max(0,h["per_class"]!.GetValue<int>()-negative)),["missingPositive"]=Math.Max(0,h["per_class"]!.GetValue<int>()-positive),["missingNegative"]=Math.Max(0,h["per_class"]!.GetValue<int>()-negative),
            ["detail"]=enough?"可开始学习；效果检查材料另行核对。":$"总计还需至少 {Math.Max(0,minimum-rows.Length)} 条；其中正向还需 {Math.Max(0,h["per_class"]!.GetValue<int>()-positive)} 条，相反标注还需 {Math.Max(0,h["per_class"]!.GetValue<int>()-negative)} 条。在歌曲列表日常修正即可。" };
    }
    async Task Learn(bool downloadConfirmed,CancellationToken ct)
    {
        try
        {
            using var overall=CancellationTokenSource.CreateLinkedTokenSource(ct); overall.CancelAfter(TimeSpan.FromHours(6)); ct=overall.Token;
            // Durable outbox batches finish automatically; a non-progressing/offline item ends this run.
            for(int i=0;i<200;i++) { ct.ThrowIfCancellationRequested(); var capture=await service.CaptureDailyFeedback(ct); if(capture["completed"]?.GetValue<int>() is not >0) break; }
            await MatchLearningRecordings(ct);
            var eligible=Dimensions.Where(d=>Material(d)["ready"]!.GetValue<bool>()).ToArray();
            foreach(var d in Dimensions.Except(eligible)) { jobDimension=d; Set("insufficient_material",S(Material(d)["detail"])); }
            if(eligible.Length==0 && !Dimensions.Any(Enabled)) { LearningSet("insufficient_material","标注还不够。继续在歌曲列表修正你熟悉的歌曲；四个维度各自累计。无需现在补题。"); return; }
            if(!File.Exists(Path.Combine(root,"ready.json")))
            {
                if(!downloadConfirmed) { LearningSet("download_required","首次学习需下载约615 MB公共声音模型，并安装CPU依赖，预计数GB下载及至少6 GB可用磁盘。仅用于本机学习，不上传歌曲或标注。确认后自动继续。"); return; }
                var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
                if(drive.AvailableFreeSpace<6L*1024*1024*1024) { LearningFail(LearningFailure.Create("disk_space","prepare",jobDimension)); return; }
                LearningSet("preparing_components","正在下载本地学习所需文件。完成后自动继续。");
                await Run("prepare",jobDimension,Dataset,ct,nested:true); ct.ThrowIfCancellationRequested();
                if(S(state["state"])!="components_ready") { LearningFail(state["failure"]?.DeepClone().AsObject()??LearningFailure.Create("worker_exit","prepare",jobDimension)); return; }
            }
            foreach(var d in eligible)
            {
                ct.ThrowIfCancellationRequested(); jobDimension=d;
                if(Enabled(d)) continue;
                LearningSet("learning","正在学习并自动检查。四个维度分别判断，通过后分别生效。");
                try { await LearnHead(d,ct); }
                catch(InvalidOperationException) { service.Store.SetLocalForGeneration("learning-blocker:"+d,"check_material_required",jobGeneration); Set("check_material_required","缺少已隔离且未受自动标签影响的效果检查材料，或录音身份尚待确认。当前继续使用原有识别；无需在设置补题。"); }
            }
            if(Dimensions.Any(Enabled)) await UpdateLibrary(ct);
            else if(eligible.Select(HeadFailure).FirstOrDefault(f=>f!=null && S(f["code"]) is not ("insufficient_material" or "check_material_required" or "validation_failed")) is { } failure) LearningFail(failure);
            else if(eligible.Any(d=>S(Status(d)["state"])=="validation_failed")) LearningFail(LearningFailure.Create("validation_failed","evaluate"),"validation_failed");
            else LearningSet("waiting_checks","本次学习已停止。各维度缺项或检查结果见下方；尚无通过效果检查的维度，继续使用原有识别。");
        }
        catch(OperationCanceledException) { if(jobGeneration==service.Store.Generation) LearningFail(LearningFailure.Create(cancel?.IsCancellationRequested==true?"cancelled":"time_budget","orchestrate",jobDimension),cancel?.IsCancellationRequested==true?"cancelled":"incomplete"); }
        catch(Exception e) when(e is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        { if(jobGeneration==service.Store.Generation) LearningFail(LearningFailure.FromException(e,jobStage,jobDimension)); }
        finally { lock(gate) { learning=false; cancel?.Dispose(); cancel=null; } service.Notify(); }
    }
    async Task YieldToPlayback(CancellationToken ct)
    {
        // Check before every worker batch; a playing client owns the CPU/audio decode budget.
        while(app.Player.IsPlaying) { LearningSet("waiting_playback","播放优先：暂停播放后自动继续学习。"); await Task.Delay(1000,ct); }
    }
    async Task MatchLearningRecordings(CancellationToken ct)
    {
        var wanted=TrainingRows(service.Store.Current(Dataset,"answer")).Select(e=>S(e["payload"]?["recordingId"])).Where(id=>service.FindLocal(id) is null).ToHashSet();
        if(wanted.Count==0) return;
        var records=service.Store.Current(Dataset,"recording").Where(e=>wanted.Contains(S(e["entityId"]))).Select(e=>e["payload"]!).ToArray();
        var candidates=app.Library.Snapshot.Tracks.Where(t=>!t.IsCue && records.Any(r=>Math.Abs((r["durationMs"]?.GetValue<long>()??0)-t.DurationMs)<=1000)).OrderBy(t=>t.TrackKey,StringComparer.Ordinal).ToArray();
        var frame=Hash(string.Join("\n",wanted.Order(StringComparer.Ordinal))+string.Join("\n",candidates.Select(t=>t.TrackKey+AnalysisFeature.Signature(t))));
        var saved=service.Store.Local("learning-match-cursor") is { } raw?JsonNode.Parse(raw):null;
        int offset=S(saved?["frame"])==frame?saved?["offset"]?.GetValue<int>()??0:0;
        if(offset>=candidates.Length) offset=0;
        foreach(var batch in candidates.Skip(offset).Take(10000).Chunk(6))
        {
            await YieldToPlayback(ct);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(60));
            foreach(var track in batch)
            {
                if(await RecordingRow(track,deadline.Token) is { } row && wanted.Remove(S(row["recordingId"])))
                {
                    service.Store.SetLocalForGeneration("binding:"+S(row["recordingId"]),track.TrackKey,jobGeneration);
                    service.Store.SetLocalForGeneration("signature:"+S(row["recordingId"]),AnalysisFeature.Signature(track),jobGeneration);
                }
                offset++; service.Store.SetLocalForGeneration("learning-match-cursor",new JsonObject { ["frame"]=frame,["offset"]=offset }.ToJsonString(),jobGeneration);
                if(wanted.Count==0) return;
            }
        }
    }
    async Task LearnHead(string d,CancellationToken ct)
    {
        var candidates=service.Store.Current(Dataset,"model").Where(e=>S(e["payload"]?["category"])!="job" && Dimension(e["payload"])==d && S(e["payload"]?["state"]) is "development_required" or "regression_required" or "independent_test_required" or "pass_research_gate" or "self_selected_candidate" or "validation_failed" or "inconclusive").ToArray();
        if(candidates.Any(e=>S(e["payload"]?["state"]) is "validation_failed" or "inconclusive")) { Set("validation_failed","效果检查未通过。继续使用原有识别；不反复开封或放宽标准。"); return; }
        if(candidates.Length==0)
        {
            for(int batch=0;batch<24;batch++)
            {
                ct.ThrowIfCancellationRequested(); var answers=TrainingRows(service.Store.Current(Dataset,"answer")).Where(e=>Dimension(e["payload"])==d).ToList();
                var input=Snapshot("extract",answers,d,Dataset,"training"); if(input["rows"]!.AsArray().Count==0) break;
                await YieldToPlayback(ct); await Run("extract",d,Dataset,ct,input,true); ct.ThrowIfCancellationRequested();
                if(S(state["state"])!="extraction_complete") return;
                if(batch==23) { Set("incomplete","本轮分析限额已到，可继续学习。"); return; }
            }
            await YieldToPlayback(ct); await Run("train",d,Dataset,ct,nested:true); ct.ThrowIfCancellationRequested();
            if(service.Synthetic(Dataset)) return;
        }
        for(int stage=0;stage<4;stage++)
        {
            var candidate=service.Store.Current(Dataset,"model").SingleOrDefault(e=>S(e["payload"]?["category"])!="job" && Dimension(e["payload"])==d && S(e["payload"]?["state"]) is "development_required" or "regression_required" or "independent_test_required" or "pass_research_gate" or "self_selected_candidate");
            if(candidate is null) return;
            var status=S(candidate["payload"]?["state"]);
            if(status=="self_selected_candidate") { Set("check_material_required","已学习日常标注，但缺少独立的效果检查材料。继续使用原有识别；不会将训练得分当作识别效果。"); return; }
            if(status=="pass_research_gate") { Activate(d); Set("active","已通过效果检查，正在更新歌曲识别。"); return; }
            var role=status=="development_required"?"development":status=="regression_required"?"calibration_regression":"sealed_test";
            var ids=role=="calibration_regression"?service.Store.Current(Dataset,"answer").Where(e=>S(e["payload"]?["role"])==role).Select(e=>S(e["payload"]?["recordingId"])):ReadStrings(service.Store.Current(Dataset,"manifest").Single(e=>S(e["entityId"])=="study-v1")["payload"]?[role]).Take(d=="MOOD"?int.MaxValue:role=="development"?8:24);
            var checkAnswers=service.Store.Current(Dataset,"answer",true).Where(e=>Dimension(e["payload"])==d && S(e["payload"]?["role"])==role && e["payload"]?["repeat"]?.GetValue<bool>()!=true).ToArray();
            if(ids.Any(id=>checkAnswers.Count(e=>S(e["payload"]?["recordingId"])==id)!=1)) throw new InvalidOperationException("Missing genuine effect-check material");
            foreach(var chunk in ids.Distinct().Chunk(6))
            {
                var rows=new JsonArray();
                foreach(var id in chunk) if(service.FindLocal(id) is { } t && await RecordingRow(t,ct) is { } row && !Cached(S(row["recordingId"]),S(row["contentSignature"]))) rows.Add((JsonNode)row);
                if(rows.Count==0) continue;
                await YieldToPlayback(ct); await Run("extract",d,Dataset,ct,new JsonObject { ["rows"]=rows },true); ct.ThrowIfCancellationRequested(); if(S(state["state"])!="extraction_complete") return;
            }
            await Run("evaluate",d,Dataset,ct,nested:true); ct.ThrowIfCancellationRequested();
            if(S(state["state"]) is not ("development_passed" or "regression_passed" or "pass_research_gate")) return;
        }
    }
    bool Cached(string id,string content)=>service.Store.Local("vector:"+id) is { } raw && JsonNode.Parse(raw) is { } cache && S(cache["contentSignature"])==content && S(cache["extractor"])==Extractor && CompatibleWorker(S(cache["workerHash"]));
    async Task<JsonObject?> RecordingRow(Track t,CancellationToken ct)
    {
        if(t.IsCue || t.DurationMs<150000 || app.Library.PathOf(t) is not { } path) return null;
        var stat=new FileInfo(path); if(!stat.Exists || stat.Length!=t.Size || stat.LastWriteTimeUtc.Ticks!=t.Mtime) return null;
        var centers=PcmMath.Windows(0,t.DurationMs).Select(w=>(w.StartMs+w.EndMs)/2).ToArray();
        if(centers.Length!=3 || centers[1]-centers[0]<30000 || centers[2]-centers[1]<30000) return null;
        var cached=service.Store.Local("daily-content:"+t.TrackKey) is { } raw?JsonNode.Parse(raw):null;
        var content=S(cached?["signature"])==AnalysisFeature.Signature(t)?S(cached?["content"]):"";
        if(content.Length!=64) { await using var stream=File.OpenRead(path); content=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct)); }
        stat.Refresh(); if(stat.Length!=t.Size || stat.LastWriteTimeUtc.Ticks!=t.Mtime) return null;
        var id=Hash("sha256:"+content+":whole");
        service.Store.SetLocalForGeneration("daily-content:"+t.TrackKey,new JsonObject { ["id"]=id,["content"]=content,["signature"]=AnalysisFeature.Signature(t) }.ToJsonString(),jobGeneration);
        return new JsonObject { ["recordingId"]=id,["contentSignature"]=content,["path"]=path,["cue"]=false,["centersMs"]=new JsonArray(centers.Select(c=>(JsonNode?)JsonValue.Create(c)).ToArray()) };
    }
    async Task UpdateLibrary(CancellationToken ct)
    {
        var tracks=app.Library.Snapshot.Tracks.OrderBy(t=>t.TrackKey,StringComparer.Ordinal).ToArray();
        var frame=LibraryFrame(tracks); service.Store.SetLocalForGeneration("library-auto-attempt",frame,jobGeneration);
        var cursor=service.Store.Local("library-update-cursor") is { } raw?JsonNode.Parse(raw):null;
        int processed=S(cursor?["frame"])==frame?cursor?["processed"]?.GetValue<int>()??0:0,uncovered=S(cursor?["frame"])==frame?cursor?["uncovered"]?.GetValue<int>()??0:0;
        if(processed>=tracks.Length) { processed=0; uncovered=0; }
        foreach(var batch in tracks.Skip(processed).Take(10000).Chunk(6))
        {
            ct.ThrowIfCancellationRequested(); await YieldToPlayback(ct); RefreshDeployment();
            if(!Dimensions.Any(Enabled)) throw new InvalidOperationException("All active heads were invalidated");
            var rows=new JsonArray(); var bindings=new List<(Track Track,JsonObject Row)>();
            foreach(var t in batch)
            {
                if(await RecordingRow(t,ct) is not { } row) { uncovered++; continue; }
                bindings.Add((t,row)); if(!Cached(S(row["recordingId"]),S(row["contentSignature"]))) rows.Add((JsonNode)row.DeepClone());
            }
            LearningSet("updating_library","正在更新全库识别。未完成或不支持的歌曲暂用原有识别。",processed,tracks.Length,uncovered);
            if(rows.Count>0) { await Run("extract",jobDimension,Dataset,ct,new JsonObject { ["rows"]=rows },true); ct.ThrowIfCancellationRequested(); if(S(state["state"])!="extraction_complete") { LearningFail(state["failure"]?.DeepClone().AsObject()??LearningFailure.Create("worker_exit","extract",jobDimension),processed:processed,total:tracks.Length,uncovered:uncovered); return; } }
            foreach(var (track,row) in bindings)
            {
                var cache=JsonNode.Parse(service.Store.Local("vector:"+S(row["recordingId"]))!)!; var heads=new JsonObject(); bool inferenceFailed=false;
                lock(deploymentGate) foreach(var (d,active) in activeHeads)
                {
                    try
                    {
                        var timer=Stopwatch.StartNew(); var model=new JeekMusic.Core.Preferences.PersonalHead(active["artifact"]!.AsObject()); var value=model.Decide(cache["vector"]!);
                        if(timer.Elapsed>TimeSpan.FromMilliseconds(100)) throw new InvalidDataException("Personal inference budget exceeded");
                        heads[d]=new JsonObject { ["version"]=S(active["version"]),["value"]=value,["source"]="PERSONAL",["extractor"]=Extractor };
                    }
                    catch(Exception e) when(e is IOException or InvalidOperationException or ArgumentException) { inferenceFailed=true; }
                }
                if(inferenceFailed) uncovered++;
                if(app.Library.ByKey(track.TrackKey) is not { } fresh || AnalysisFeature.Signature(fresh)!=AnalysisFeature.Signature(track)) { uncovered++; continue; }
                service.Store.SetLocalForGeneration("personal-result:"+track.TrackKey,new JsonObject { ["trackKey"]=track.TrackKey,["signature"]=AnalysisFeature.Signature(track),["contentSignature"]=S(row["contentSignature"]),["heads"]=heads }.ToJsonString(),jobGeneration);
            }
            processed+=batch.Length;
            service.Store.SetLocalForGeneration("library-update-cursor",new JsonObject { ["frame"]=frame,["processed"]=processed,["uncovered"]=uncovered }.ToJsonString(),jobGeneration);
            await Task.Run(()=>app.SmartTags.Recompute(),ct);
        }
        var limited=processed<tracks.Length;
        LearningSet(limited?"incomplete":uncovered>0?"updated_with_uncovered":"updated",limited?"本轮已到一万首限额；仍有歌曲等待分析。":uncovered>0?"可分析歌曲已更新，部分录音离线、过短或不支持，继续使用原有识别。":"全库识别已更新。",processed,tracks.Length,uncovered);
    }
    string LibraryFrame(Track[] tracks)=>Hash(string.Join("\n",DeploymentStatus()["heads"]!.AsArray().Where(h=>h!["enabled"]!.GetValue<bool>()).Select(h=>S(h!["dimension"])+":"+S(h["version"])))+string.Join("\n",tracks.Select(t=>t.TrackKey+":"+AnalysisFeature.Signature(t))));
}
