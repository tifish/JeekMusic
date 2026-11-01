using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using JeekMusic.Analysis;
using JeekMusic.Infrastructure;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

public sealed partial class PreferenceService
{
    readonly SemaphoreSlim dailyGate=new(1,1);
    readonly CancellationTokenSource dailyStop=new();
    Timer? dailyTimer;
    Task? dailyTask;
    void ResetDailyCursor() => Store.SetLocal("dailyCursor",app.Db.LatestTagReviewId().ToString(CultureInfo.InvariantCulture));
    void InitializeDailyFeedback()
    {
        if(Store.Local("dailyCursor") is null) ResetDailyCursor();
        app.SmartTags.LearningGeneration=()=>Store.Generation;
        app.SmartTags.LearningDevice=()=>Store.DeviceId;
        app.SmartTags.ResultsChanged+=ScheduleDailyFeedback;
        app.Library.SnapshotChanged+=ScheduleDailyFeedback;
        dailyTimer=new(_=>ScheduleDailyFeedback(),null,1000,30000);
    }
    void ScheduleDailyFeedback()
    {
        if(dailyStop.IsCancellationRequested || dailyTask is { IsCompleted:false }) return;
        dailyTask=Task.Run(async()=> { try { await CaptureDailyFeedback(); } catch(OperationCanceledException) { } catch(Exception e) { Log.Warn("preferences","日常标注待处理，日常标签已经保存",e); } });
    }
    JsonObject DailyFeedbackStatus()
    {
        var pending=Store.LocalRows("daily-pending:",10000);
        var records=Store.Current(Dataset,"recording").Where(e=>e["payload"]?["selfSelected"]?.GetValue<bool>()==true).ToArray();
        return new JsonObject { ["pending"]=pending.Count,["state"]=dailyTask is { IsCompleted:false }?"processing":"ready",
            ["pendingIdentity"]=records.Count(e=>e["payload"]?["identityReviewed"]?.GetValue<bool>()!=true),["pendingFeatures"]=records.Count(e=>Store.Local("vector:"+S(e["entityId"])) is null),["unsupportedRecordings"]=records.Count(e=>e["payload"]?["supported"]?.GetValue<bool>()!=true),
            ["source"]="explicit_daily_tag_correction",["basis"]="track_tag_feedback",["selectionBias"]="self_selected_and_predictions_visible_not_independent",
            ["pendingReasons"]=new JsonArray(Store.LocalRows("daily-problem:",10000).Values.GroupBy(v=>v).Select(g=>(JsonNode)new JsonObject { ["reason"]=g.Key,["count"]=g.Count() }).ToArray()) };
    }
    public async Task<JsonObject> CaptureDailyFeedback()
    {
        if(!await dailyGate.WaitAsync(0,dailyStop.Token)) return DailyFeedbackStatus();
        try
        {
            var generation=Store.Generation;
            void Set(string key,string value)=>Store.SetLocalForGeneration(key,value,generation);
            var cursor=long.Parse(Store.Local("dailyCursor")??"0",CultureInfo.InvariantCulture);
            foreach(var review in app.Db.TagReviewsAfter(cursor))
            {
                var record=JsonNode.Parse(review.Record)?.AsObject();
                if(record?["explicitCorrections"] is JsonObject && S(record["learningGeneration"])==generation && S(record["learningDevice"])==Store.DeviceId)
                {
                    var key="daily-pending:"+review.Id.ToString("D20",CultureInfo.InvariantCulture);
                    record["trackKey"]=review.TrackKey; record["reviewId"]=review.Id;
                    Set(key,record.ToJsonString());
                }
                Set("dailyCursor",review.Id.ToString(CultureInfo.InvariantCulture));
            }
            int completed=0,hashed=0;
            foreach(var (key,json) in Store.LocalRows("daily-pending:",100))
            {
                dailyStop.Token.ThrowIfCancellationRequested();
                if(Store.Generation!=generation) break;
                var review=JsonNode.Parse(json)!.AsObject(); var trackKey=S(review["trackKey"]); var edits=review["explicitCorrections"]!.AsObject();
                var id=""; var content=""; var t=app.Library.ByKey(trackKey);
                var positives=edits.Any(kv=>kv.Value!=null) || Store.Current(Dataset,"answer").Any(e=>S(e["payload"]?["source"])=="explicit_daily_tag_correction" && S(e["payload"]?["answer"])!="WITHDRAWN");
                if(positives)
                {
                    var path=t is null?null:app.Library.PathOf(t); var stat=path is null?null:new FileInfo(path);
                    if(t is null || path is null || stat?.Exists!=true) { Set("daily-problem:"+key,"recording_offline"); continue; }
                    if(S(review["recordingSignature"])!=AnalysisFeature.Signature(t) || stat.Length!=t.Size || stat.LastWriteTimeUtc.Ticks!=t.Mtime) { Set("daily-problem:"+key,"recording_changed_review_needed"); continue; }
                    var binding=Store.Local("daily-content:"+trackKey);
                    var saved=binding is null?null:JsonNode.Parse(binding);
                    if(S(saved?["signature"])==AnalysisFeature.Signature(t)) { id=S(saved?["id"]); content=S(saved?["content"]); }
                    else
                    {
                        if(hashed>=6) break; hashed++;
                        using var input=File.OpenRead(path); content=Convert.ToHexStringLower(await SHA256.HashDataAsync(input,dailyStop.Token));
                        stat.Refresh(); if(stat.Length!=t.Size || stat.LastWriteTimeUtc.Ticks!=t.Mtime) { Set("daily-problem:"+key,"recording_changed_review_needed"); continue; }
                        id=Hash("sha256:"+content+(t.IsCue?$":cue:{t.StartMs??0}:{t.EndMs??((t.StartMs??0)+t.DurationMs)}":":whole"));
                    }
                    if(Store.Generation!=generation) break;
                    Set("daily-content:"+trackKey,new JsonObject { ["id"]=id,["content"]=content,["signature"]=AnalysisFeature.Signature(t) }.ToJsonString());
                    var records=Store.Current(Dataset,"recording").Where(e=>S(e["entityId"])==id).ToArray();
                    if(records.Length>1) { Set("daily-problem:"+key,"recording_identity_conflict"); continue; }
                    var r=records.FirstOrDefault()?["payload"]?.DeepClone().AsObject()??new JsonObject { ["recordingId"]=id,["contentSignature"]=content,["title"]=t.Title,["artist"]=t.ArtistDisplay,["album"]=t.Album,
                        ["artists"]=Strings(t.Artists.Select(a=>a.Normalize().ToUpperInvariant())),["workGroup"]=t.Title.Normalize().ToUpperInvariant(),["albumGroup"]=(t.AlbumArtist+":"+t.Album).Normalize().ToUpperInvariant(),["durationMs"]=t.DurationMs,["identityReviewed"]=false,["supported"]=!t.IsCue && t.DurationMs>=150000 };
                    if(r["selfSelected"]?.GetValue<bool>()!=true) { r["selfSelected"]=true; r["exposed"]=true; r["exposureSource"]="self_selected_visible_auto"; Store.Append(Dataset,id,"recording",r,expectedGeneration:generation); Training.Invalidate(); }
                    Set("binding:"+id,t.TrackKey); Set("signature:"+id,AnalysisFeature.Signature(t));
                }
                var blocked=false;
                foreach(var (dimension,value) in edits)
                {
                    if(!Dimensions.Contains(dimension)) continue;
                    var bindingKey="daily-answer:"+Hash(trackKey+":"+dimension);
                    var binding=Store.Local(bindingKey); var prior=binding is null?null:JsonNode.Parse(binding);
                    var recording=value is null?S(prior?["id"]):id; var signature=value is null?S(prior?["content"]):content;
                    if(value is null && recording.Length==0 && id.Length>0 && Store.Current(Dataset,"answer",true).Any(e=>S(e["entityId"])==Hash(Dataset+":"+ContextFor(dimension)+":"+id+":daily"))) { recording=id; signature=content; }
                    if(recording.Length==0) continue; // AUTO never withdraws a separately captured blind answer.
                    var entity=Hash(Dataset+":"+ContextFor(dimension)+":"+recording+":daily");
                    var label=value is null?"WITHDRAWN":S(value);
                    if(value!=null && !Binary(label,dimension)) continue;
                    try
                    {
                        Store.Append(Dataset,entity,"answer",new JsonObject { ["dimension"]=dimension,["contextId"]=ContextFor(dimension),["recordingId"]=recording,["contentSignature"]=signature,
                            ["answer"]=label,["basis"]="track_tag_feedback",["reason"]="",["source"]="explicit_daily_tag_correction",["exposure"]="self_selected_visible_auto",["role"]="training",["manifestId"]="daily-feedback-v1",["sourceReview"]=Hash(Store.DeviceId+":"+review["reviewId"]) },
                            resolve:value is null,expectedGeneration:generation,eventId:Hash("daily:"+Store.DeviceId+":"+review["reviewId"]+":"+dimension));
                        if(value!=null) Set(bindingKey,new JsonObject { ["id"]=recording,["content"]=signature }.ToJsonString());
                        Training.Invalidate(dimension);
                    }
                    catch(InvalidOperationException) { blocked=true; Set("daily-problem:"+key,"concurrent_answer_requires_review"); }
                }
                if(!blocked) { Store.RemoveLocal(key); Store.RemoveLocal("daily-problem:"+key); completed++; }
            }
            if(completed>0) Notify();
            var result=DailyFeedbackStatus(); result["completed"]=completed; result["hashedFiles"]=hashed; return result;
        }
        finally { dailyGate.Release(); }
    }
    void DisposeDailyFeedback()
    {
        dailyTimer?.Dispose(); app.SmartTags.ResultsChanged-=ScheduleDailyFeedback; app.Library.SnapshotChanged-=ScheduleDailyFeedback;
        dailyStop.Cancel(); try { dailyTask?.Wait(TimeSpan.FromSeconds(5)); } catch(AggregateException) { }
    }
}
