using System.Security.Cryptography;
using System.Text.Json.Nodes;
using JeekMusic.Core.Preferences;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

/// <summary>UI and MCP share this service; never changes playback or the four production tag dimensions.</summary>
public sealed partial class PreferenceService : IDisposable
{
    readonly AppServices app;
    readonly SemaphoreSlim preparation = new(1,1);
    public PreferenceStore Store { get; }
    public PreferenceTraining Training { get; }
    public event Action? Changed;
    public string ActiveDimension=>Store.Local("activeDimension")??"MOOD";
    public void SelectDimension(string dimension)
    {
        if(!Dimensions.Contains(dimension)) throw new ArgumentException("Unknown independent dimension");
        Store.SetLocal("activeDimension",dimension);
        var p=ActiveProfile;
        if(p.Length>0 && !Deleted(p) && !Store.Current(p,"manifest").Any(e=>Dimension(e["payload"])==dimension && e["payload"]?["items"] is JsonArray))
        {
            var source=Store.Current(p,"manifest").Where(e=>S(e["payload"]?["role"])=="training" && e["payload"]?["items"] is JsonArray).OrderByDescending(e=>e["at"]!.GetValue<long>()).FirstOrDefault();
            if(source!=null)
            {
                var id=Guid.NewGuid().ToString("N"); var payload=source["payload"]!.DeepClone().AsObject(); payload["dimension"]=dimension; payload["hash"]=Hash(payload.ToJsonString());
                Store.Append(p,id,"manifest",payload); Store.SetLocal(BatchKey(p,dimension),id);
            }
        }
        Notify();
    }
    static string BatchKey(string p,string d)=>"batch:"+p+(d=="MOOD"?"":":"+d);
    public string ActiveProfile => Store.Local("activeProfile") ?? "";
    public PreferenceService(AppServices app,string? databasePath=null)
    {
        this.app=app;
        var device=app.Db.GetValue("preference_device");
        if(string.IsNullOrEmpty(device)) app.Db.SetValue("preference_device",device=Guid.NewGuid().ToString("N"));
        Store=new(databasePath??Path.Combine(AppPaths.DataDir,"preferences.db"),device);
        Training=new(this,app,databasePath==":memory:");
    }
    public void Notify() => Changed?.Invoke();
    public string CreateProfile(string name,string purpose="personal")
    {
        if(purpose is not ("personal" or "synthetic_validation")) throw new ArgumentException("Unknown archive purpose");
        if(name.Trim().Length is <1 or >80) throw new ArgumentException("Profile name requires 1-80 characters");
        var id=Guid.NewGuid().ToString("N");
        Store.Append(id,id,"profile",new JsonObject { ["purpose"]=purpose,["name"]=name.Trim(),["contextId"]=Context,["deleted"]=false });
        Store.SetLocal("activeProfile",id); Notify(); return id;
    }
    public void SelectProfile(string id)
    {
        if(!Store.Profiles().Contains(id) || Deleted(id)) throw new ArgumentException("Unknown/deleted profile");
        Store.SetLocal("activeProfile",id); Notify();
    }
    public bool Deleted(string profile) => Store.Current(profile,"profile").Any(e => e["payload"]?["deleted"]?.GetValue<bool>()==true);
    string RequireProfile()
    {
        var id=ActiveProfile;
        if(id.Length==0 || Deleted(id)) throw new InvalidOperationException("Create or select a live personal profile first");
        if(Store.Current(id,"profile").Count!=1) throw new InvalidOperationException("Profile revisions conflict");
        return id;
    }
    public JsonObject Status()
    {
        var profile=ActiveProfile;
        var answers=Store.Current(profile,"answer").Where(e=>Dimension(e["payload"])==ActiveDimension).ToList(); // sealed answers deliberately excluded
        var grouped=answers.GroupBy(e=>S(e["entityId"])).ToList();
        var manifests=Store.Current(profile,"manifest");
        return new JsonObject { ["protocol"]=JeekMusic.Core.Preferences.PreferenceProtocol.Version,["contextId"]=ContextFor(ActiveDimension),["dimension"]=ActiveDimension,["profileId"]=profile,["deleted"]=profile.Length>0 && Deleted(profile),
            ["profiles"]=new JsonArray(Store.Profiles().Select(id=>(JsonNode)new JsonObject { ["profileId"]=id,["active"]=id==profile,["deleted"]=Deleted(id),["name"]=S(Store.Current(id,"profile").FirstOrDefault()?["payload"]?["name"]) }).ToArray()),
            ["answered"]=grouped.Count(g=>g.Count()==1 && S(g.First()["payload"]?["answer"]) is not ("SKIP" or "WITHDRAWN")),
            ["uncertain"]=grouped.Count(g=>g.Count()==1 && S(g.First()["payload"]?["answer"]) is "UNCERTAIN" or "UNRESOLVED"),
            ["conflicts"]=grouped.Count(g=>g.Count()>1),["trainingLabels"]=grouped.Count(g=>g.Count()==1 && UsableAnswer(g.First())),
            ["manifestCount"]=manifests.Count,["personalModelEnabled"]=false,["modelState"]="candidate_only",
            ["heads"]=HeadStatus(profile),["sync"]=Store.SyncStatus(),["training"]=Training.Status() };
    }
    public JsonObject Batch(bool revealEvaluationAnswers=true,string? dimension=null)
    {
        var d=dimension??ActiveDimension; var p=RequireProfile();
        var manifestId=Store.Local(BatchKey(p,d));
        var m=Store.Current(p,"manifest").SingleOrDefault(e=>S(e["entityId"])==manifestId) ?? Store.Current(p,"manifest").Where(e=>Dimension(e["payload"])==d && e["payload"]?["items"] is JsonArray a && a.Count<=6).OrderByDescending(e=>e["at"]!.GetValue<long>()).FirstOrDefault();
        manifestId=m is null ? manifestId : S(m["entityId"]);
        if(m!=null) Store.SetLocal(BatchKey(p,d),manifestId!);
        if(m is null) return new JsonObject { ["items"]=new JsonArray(),["state"]="not_prepared" };
        var payload=m["payload"]!; var rows=new JsonArray();
        var recordings=Store.Current(p,"recording").GroupBy(e=>S(e["entityId"])).ToDictionary(g=>g.Key,g=>g.ToList());
        var answers=Store.Current(p,"answer",true).GroupBy(e=>S(e["entityId"])).ToDictionary(g=>g.Key,g=>g.ToList());
        foreach(var id in ReadStrings(payload["items"]))
        {
            var r=recordings.GetValueOrDefault(id)?.SingleOrDefault()?["payload"];
            if(r is null) continue;
            var a=answers.GetValueOrDefault(AnswerEntity(p,id,m,d));
            rows.Add((JsonNode)new JsonObject { ["recordingId"]=id,["title"]=S(r["title"]),["artist"]=S(r["artist"]),["album"]=S(r["album"]),["durationMs"]=r["durationMs"]?.DeepClone(),
                ["contentSignature"]=S(r["contentSignature"]),["answer"]=!revealEvaluationAnswers && S(payload["role"]) is "development" or "sealed_test" ? "" : a?.Count==1 ? S(a[0]["payload"]?["answer"]) : "",["completed"]=a?.Count==1,["conflict"]=a?.Count>1,
                ["basis"]=a?.Count==1 ? S(a[0]["payload"]?["basis"]) : "",["available"]=FindLocal(id)!=null });
        }
        return new JsonObject { ["manifestId"]=manifestId,["manifestHash"]=S(payload["hash"]),["state"]=S(payload["state"]),["items"]=rows,["dimension"]=d,["question"]=Question(d),["previewAvailable"]=false };
    }
    static string AnswerId(string p,string id,string d)=>Hash(p+":"+ContextFor(d)+":"+id);
    static string AnswerEntity(string p,string id,JsonObject manifest,string d)=>ReadStrings(manifest["payload"]?["repeatIds"]).Contains(id)?Hash(p+":"+ContextFor(d)+":"+id+":repeat:"+S(manifest["entityId"])):AnswerId(p,id,d);
    public static string Question(string d)=>d switch { "MOOD"=>"按你平时运动选歌的标准，这个录音的整首歌适合放进运动歌单吗？","VOICE"=>"按你的标签标准，这个完整录音有明显人声（演唱或说话）吗？","GENRE"=>"按你的标签标准，这个完整录音属于古典音乐吗？",_=>"按你的安静聆听标准，这个完整录音适合安静聆听吗？（与运动标签独立）" };
    internal bool Synthetic(string profile)=>Store.Current(profile,"profile").Any(e=>S(e["payload"]?["purpose"])=="synthetic_validation");
    JsonArray HeadStatus(string profile)
    {
        var result=new JsonArray(); var contract=HeadContract();
        foreach(var d in Dimensions)
        {
            var answers=Store.Current(profile,"answer").Where(e=>Dimension(e["payload"])==d).GroupBy(e=>S(e["entityId"])).ToArray();
            var usable=answers.Where(g=>g.Count()==1).Select(g=>g.Single()).Where(UsableAnswer).ToArray();
            var models=Store.Current(profile,"model").Where(e=>Dimension(e["payload"])==d).TakeLast(20).ToArray();
            result.Add((JsonNode)new JsonObject { ["dimension"]=d,["contextId"]=ContextFor(d),["positive"]=Labels(d).Positive,["negative"]=Labels(d).Negative,["trainingLabels"]=usable.Length,
                ["positiveCount"]=usable.Count(e=>S(e["payload"]?["answer"])==Labels(d).Positive),["negativeCount"]=usable.Count(e=>S(e["payload"]?["answer"])==Labels(d).Negative),
                ["uncertainCount"]=answers.Count(g=>g.Count()==1 && S(g.First()["payload"]?["answer"]) is "UNCERTAIN" or "UNRESOLVED"),["conflictCount"]=answers.Count(g=>g.Count()>1),
                ["protocol"]=contract["heads"]![d]!.DeepClone(),["availability"]="candidate_only_until_all_gates",["enabled"]=false,
                ["models"]=new JsonArray(models.Select(e=>(JsonNode)new JsonObject { ["modelId"]=S(e["entityId"]),["state"]=S(e["payload"]?["state"]),["trainingSetHash"]=S(e["payload"]?["trainingSetHash"]),["extractor"]=S(e["payload"]?["extractor"]),["enabled"]=false }).ToArray()) });
        }
        return result;
    }
    public Track? FindLocal(string id)
    {
        var key=Store.Local("binding:"+id); var t=key is null ? null : app.Library.ByKey(key);
        return t!=null && Store.Local("signature:"+id)==$"{t.Mtime}:{t.Size}" && app.Library.PathOf(t)!=null ? t : null;
    }
    public async Task<JsonObject> PrepareBatch(CancellationToken token=default, bool inventoryOnly=false)
    {
        if(!await preparation.WaitAsync(0,token)) throw new InvalidOperationException("Batch preparation already running");
        try
        {
            var p=RequireProfile();
            var existing=Batch();
            if(!inventoryOnly && existing["items"] is JsonArray old && old.Count>0 && old.Any(n=>S(n?["answer"])=="" || n?["conflict"]?.GetValue<bool>()==true)) return existing;
            var attempted=Store.Current(p,"manifest").Where(e=>Dimension(e["payload"])==ActiveDimension).SelectMany(e=>ReadStrings(e["payload"]?["items"])).ToHashSet();
            if(inventoryOnly) attempted.UnionWith(Store.Current(p,"recording").Select(e=>S(e["entityId"])));
            if(!inventoryOnly && attempted.Count>=(ActiveDimension=="MOOD"?48:36)) throw new InvalidOperationException("Protocol training attempt budget (48) reached");
            if(!inventoryOnly && Study() is { } study) return PrepareFrozenBatch(study);
            var seed=Store.Local("seed:"+p) ?? "personal-workout-v1"; Store.SetLocal("seed:"+p,seed);
            // Group metadata before sampling. Unverified identities can support an explicitly non-independent pilot only.
            var tracks=app.Library.Snapshot.Tracks.ToArray();
            var knownExposed=Store.Current(p,"recording").Where(e=>e["payload"]?["exposed"]?.GetValue<bool>()==true).Select(e=>S(e["entityId"])).ToHashSet();
            var exposure=ReadExposure(p);
            var candidates=tracks.Where(t=>!exposure.Contains(Hash(t.TrackKey)) &&!t.IsCue && t.DurationMs>=150_000 && t.Artists.Count>0 && !string.IsNullOrWhiteSpace(t.Album) && app.Library.PathOf(t)!=null)
                .OrderBy(t=>Order(seed,t.TrackKey),StringComparer.Ordinal).ToList();
            var selected=new List<string>(); var artists=new Dictionary<string,int>(); var albums=new Dictionary<string,int>();
            foreach(var t in candidates)
            {
                token.ThrowIfCancellationRequested();
                if(t.Artists.Any(a=>artists.GetValueOrDefault(a)>=4) || albums.GetValueOrDefault(t.AlbumKey ?? t.Album!)>=2) continue;
                var path=app.Library.PathOf(t)!;
                var info=new FileInfo(path); if(!info.Exists || info.Length!=t.Size || info.LastWriteTimeUtc.Ticks!=t.Mtime) continue;
                var content=await Task.Run(async ()=> { using var stream=File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,token)); },token);
                info.Refresh(); if(info.Length!=t.Size || info.LastWriteTimeUtc.Ticks!=t.Mtime) continue;
                var id=Hash("sha256:"+content+":whole");
                if(attempted.Contains(id) || selected.Contains(id) || knownExposed.Contains(id)) continue;
                if(Store.Current(p,"recording").All(e=>S(e["entityId"])!=id))
                    Store.Append(p,id,"recording",new JsonObject { ["recordingId"]=id,["contentSignature"]=content,["title"]=t.Title,["artist"]=t.ArtistDisplay,["album"]=t.Album,
                        ["artists"]=Strings(t.Artists.Select(a=>a.Normalize().ToUpperInvariant())),["workGroup"]=t.Title.Normalize().ToUpperInvariant(),["albumGroup"]=(t.AlbumArtist+":"+t.Album).Normalize().ToUpperInvariant(),
                        ["durationMs"]=t.DurationMs,["identityReviewed"]=false,["exposed"]=app.Db.TagReviews(t.TrackKey,1).Count>0,["supported"]=true });
                Store.SetLocal("binding:"+id,t.TrackKey); Store.SetLocal("signature:"+id,$"{t.Mtime}:{t.Size}");
                selected.Add(id); foreach(var a in t.Artists) artists[a]=artists.GetValueOrDefault(a)+1;
                var album=t.AlbumKey??t.Album!; albums[album]=albums.GetValueOrDefault(album)+1;
                if(selected.Count==6) break;
            }
            if(selected.Count==0) throw new InvalidOperationException("No new supported recordings; no answers were inferred");
            if(inventoryOnly) { Store.SetLocal("inventoryItems:"+p,Strings(selected).ToJsonString()); Notify(); return Inventory(); }
            var manifest=Guid.NewGuid().ToString("N");
            var payload=new JsonObject { ["dimension"]=ActiveDimension,["protocolVersion"]="v2-operational-pilot",["seed"]=seed,["items"]=Strings(selected),["role"]="training",["state"]="identity_incomplete_pilot",
                ["independent"]=false,["inventoryCount"]=tracks.Length,["eligibleMetadataCount"]=candidates.Count,["exposure"]="blind_ui_no_predictions",
                ["limitation"]="Unreviewed work/artist aliases and full-library content identity; development and sealed test remain locked. Pilot is training-side only." };
            payload["hash"]=Hash(payload.ToJsonString()); Store.Append(p,manifest,"manifest",payload);
            Store.SetLocal(BatchKey(p,ActiveDimension),manifest); Notify(); return Batch();
        }
        finally { preparation.Release(); }
    }
    public JsonObject Answer(string recording,string answer,string basis,string reason="",bool resolve=false, string? dimension=null)
    {
        var d=dimension??ActiveDimension; if(!Dimensions.Contains(d)) throw new ArgumentException("Unknown independent dimension");
        var p=RequireProfile();
        if(!Answers.Contains(answer) || !Bases.Contains(basis) || answer is not ("UNCERTAIN" or "UNRESOLVED" or "SKIP" or "WITHDRAWN") && !Binary(answer,d)) throw new ArgumentException("Explicit answer and basis required");
        var m=Store.Current(p,"manifest").FirstOrDefault(e=>S(e["entityId"])==Store.Local(BatchKey(p,d)) && Dimension(e["payload"])==d && ReadStrings(e["payload"]?["items"]).Contains(recording)) ?? throw new ArgumentException("Recording not in a frozen batch");
        var r=Store.Current(p,"recording").SingleOrDefault(e=>S(e["entityId"])==recording) ?? throw new ArgumentException("Recording identity conflict");
        var entity=AnswerEntity(p,recording,m,d);
        var revisesExisting=Store.Current(p,"answer",true).Any(a=>S(a["entityId"])==entity);
        var e=Store.Append(p,entity,"answer",new JsonObject { ["recordingId"]=recording,["contentSignature"]=S(r["payload"]?["contentSignature"]),["contextId"]=ContextFor(d),["dimension"]=d,
            ["answer"]=answer,["basis"]=basis,["reason"]=reason,["repeat"]=ReadStrings(m["payload"]?["repeatIds"]).Contains(recording),["source"]=Synthetic(p)?"synthetic_fixture":"explicit_human_blind_batch",["exposure"]=S(m["payload"]?["exposure"]),["role"]=S(m["payload"]?["role"]),
            ["manifestId"]=S(m["entityId"]),["manifestHash"]=S(m["payload"]?["hash"]) },resolve);
        if(S(m["payload"]?["role"])=="training" && !ReadStrings(m["payload"]?["repeatIds"]).Contains(recording) || answer=="WITHDRAWN" || revisesExisting) Training.Invalidate(d); Notify(); return new JsonObject { ["eventId"]=S(e["eventId"]),["revision"]=e["revision"]!.DeepClone(),["saved"]=true };
    }
    public JsonObject Conflicts(string? dimension=null)
    {
        var p=RequireProfile();
        // Sealed answer text never appears in diagnostics; review through the dedicated labeling screen only.
        return new JsonObject { ["items"]=new JsonArray(Store.Read(p,"answer").Where(e=>Dimension(e["payload"])==(dimension??ActiveDimension)).GroupBy(e=>S(e["entityId"])).Select(Heads).Where(h=>h.Count>1).Select(h=>(JsonNode)new JsonObject {
            ["recordingId"]=S(h[0]["payload"]?["recordingId"]),["revisions"]=new JsonArray(h.Select(e=>(JsonNode)new JsonObject { ["eventId"]=S(e["eventId"]),["deviceId"]=S(e["deviceId"]),["answer"]=S(e["payload"]?["answer"]),["basis"]=S(e["payload"]?["basis"]) }).ToArray()) }).ToArray()) };
    }
    public JsonObject Delta(JsonObject request)
    {
        if(request["protocol"]?.GetValue<int>()!=JeekMusic.Core.Preferences.PreferenceProtocol.Version) throw new ArgumentException("Unsupported preference delta protocol");
        if(S(request["sender"]).Length is <1 or >160) throw new ArgumentException("Invalid sender identity");
        var added=Store.AcceptPage(S(request["sender"]),request["page"]!.AsObject());
        if(added>0)
        {
            var events=request["page"]!["events"]!.AsArray();
            foreach(var profile in events.Where(n=>S(n?["kind"]) is "recording" or "profile").Select(n=>S(n?["profileId"])).Distinct()) Training.Invalidate(profileId:profile);
            foreach(var profile in events.Where(n=>S(n?["kind"])=="manifest" && S(n?["entityId"])=="global-seal-exposure").Select(n=>S(n?["profileId"])).Distinct())
                if(Store.Current(profile,"manifest").Count(e=>S(e["entityId"])=="global-seal-exposure")>1) Training.Invalidate(profileId:profile);
            foreach(var item in events.Where(n=>S(n?["kind"])=="answer" && ((S(n?["payload"]?["role"])=="training" && n?["payload"]?["repeat"]?.GetValue<bool>()!=true) || S(n?["payload"]?["answer"])=="WITHDRAWN" || ReadStrings(n?["parents"]).Length>0 || Store.Current(S(n?["profileId"]),"answer",true).Count(e=>S(e["entityId"])==S(n?["entityId"]))>1)).Select(n=>(Profile:S(n?["profileId"]),Dim:Dimension(n?["payload"]))).Distinct()) Training.Invalidate(item.Dim,item.Profile);
        }
        foreach(var profile in Store.Profiles().Where(Deleted)) Training.PurgeProfile(profile);
        if(added>0) Notify();
        var page=Store.Page(request["after"]?.GetValue<long>()??0); page["acceptedCursor"]=request["page"]!["nextCursor"]!.DeepClone(); return page;
    }
    public void DeleteProfile() { var p=RequireProfile(); Training.PurgeProfile(p); Store.DeleteProfile(p); Training.Invalidate(); Notify(); }
    HashSet<string> ReadExposure(string profile)=>Store.Current(profile,"answer").Where(e=>S(e["payload"]?["role"])=="calibration_regression").Select(e=>S(e["payload"]?["legacyRecordingKeyHash"])).Where(s=>s.Length==64).ToHashSet();
    public void ReviewIdentity(string id,string work,string album,string[] artists,bool exposed)
    {
        if(work.Trim().Length is <1 or >200 || album.Trim().Length is <1 or >200 || artists.Length is <1 or >20 || artists.Any(a=>a.Trim().Length is <1 or >100)) throw new ArgumentException("Explicit reviewed work, release album and artist identities required");
        var p=RequireProfile();
        var r=Store.Current(p,"recording").Single(e=>S(e["entityId"])==id)["payload"]!.DeepClone().AsObject();
        r["workGroup"]=work.Trim().Normalize().ToUpperInvariant(); r["albumGroup"]=album.Trim().Normalize().ToUpperInvariant(); r["artists"]=Strings(artists.Select(a=>a.Trim().Normalize().ToUpperInvariant())); r["identityReviewed"]=true; r["exposed"]=exposed;
        Store.Append(p,id,"recording",r); Training.Invalidate(); Notify();
    }
    public void Dispose() { Training.Dispose(); Store.Dispose(); preparation.Dispose(); }
}
