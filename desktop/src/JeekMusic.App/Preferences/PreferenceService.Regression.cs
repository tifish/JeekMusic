using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

public sealed partial class PreferenceService
{
    /// <summary>Explicit owner/version review, private profile only. Never bundled global human truth.</summary>
    public async Task<JsonObject> ImportWorkoutRegression(string path,bool ownerConfirmed,CancellationToken token=default)
    {
        if(!ownerConfirmed) throw new ArgumentException("Confirm this file belongs to the active profile and the current recording versions");
        var profile=RequireProfile(); var info=new FileInfo(path);
        if(!info.Exists || info.Length>1_000_000) throw new ArgumentException("A local feedback file up to 1 MB is required");
        var text=await File.ReadAllTextAsync(path,token); var input=JsonNode.Parse(text)?["records"] as JsonArray ?? throw new ArgumentException("Feedback records required");
        if(input.Count!=11 || input.Any(n=>S(n?["label"]) is not ("SPORT" or "NON_SPORT")) || input.Select(n=>S(n?["recording_key_sha256"])).Distinct().Count()!=11)
            throw new ArgumentException("The frozen workout regression protocol requires exactly eleven distinct explicit recording answers");
        var manifestHash=Hash(text); var changes=new List<(string,string,string,JsonObject)>(); var bindings=new List<(string Id,string Key,string Signature)>(); var ids=new List<string>();
        foreach(var item in input)
        {
            token.ThrowIfCancellationRequested(); var keyHash=S(item?["recording_key_sha256"]);
            var matches=app.Library.Snapshot.Tracks.Where(t=>Hash(t.TrackKey)==keyHash).ToArray();
            if(matches.Length!=1 || matches[0].IsCue) throw new InvalidOperationException("Regression identity unresolved; titles are never used for binding");
            var t=matches[0]; var audio=app.Library.PathOf(t) ?? throw new InvalidOperationException("Regression recording offline");
            if(Math.Abs((item?["durationMs"]?.GetValue<long>()??t.DurationMs)-t.DurationMs)>1000) throw new InvalidOperationException("Regression recording version/duration changed");
            var stat=new FileInfo(audio); if(!stat.Exists || stat.Length!=t.Size || stat.LastWriteTimeUtc.Ticks!=t.Mtime) throw new InvalidOperationException("Regression recording stale; rescan and review its version");
            using var stream=File.OpenRead(audio); var content=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,token));
            stat.Refresh(); if(stat.Length!=t.Size || stat.LastWriteTimeUtc.Ticks!=t.Mtime) throw new InvalidOperationException("Regression recording changed during verification");
            var id=Hash("sha256:"+content+":whole"); ids.Add(id); bindings.Add((id,t.TrackKey,$"{t.Mtime}:{t.Size}"));
            var old=Store.Current(profile,"recording").SingleOrDefault(e=>S(e["entityId"])==id)?["payload"]?.DeepClone().AsObject();
            var recording=old??new JsonObject { ["recordingId"]=id,["contentSignature"]=content,["title"]=t.Title,["artist"]=t.ArtistDisplay,["album"]=t.Album,["artists"]=Strings(t.Artists.Select(a=>a.Normalize().ToUpperInvariant())),["workGroup"]=t.Title.Normalize().ToUpperInvariant(),["albumGroup"]=(t.AlbumArtist+":"+t.Album).Normalize().ToUpperInvariant(),["durationMs"]=t.DurationMs,["identityReviewed"]=false,["supported"]=t.DurationMs>=150_000 };
            recording["exposed"]=true; recording["exposureSource"]="reviewed_profile_regression_import";
            changes.Add((profile,id,"recording",recording));
            changes.Add((profile,Hash(profile+":"+Context+":"+id+":calibration_regression"),"answer",new JsonObject { ["dimension"]="MOOD",["recordingId"]=id,["contentSignature"]=content,["contextId"]=Context,["answer"]=S(item?["label"]),["basis"]="unclear",
                ["reason"]=S(item?["reason"])[..Math.Min(500,S(item?["reason"]).Length)],["source"]="legacy_explicit_feedback_reviewed",["role"]="calibration_regression",["manifestId"]="legacy-regression",["manifestHash"]=manifestHash,["legacyRecordingKeyHash"]=keyHash,["baselineAtFeedback"]=S(item?["verified"]?["automatic"]?["MOOD"]),["exposure"]="previously_used_for_tuning_not_independent" }));
        }
        if(ActiveProfile!=profile || Deleted(profile)) throw new InvalidOperationException("Active profile changed during review; no imported answers committed");
        changes.Add((profile,"legacy-regression","manifest",new JsonObject { ["dimension"]="MOOD",["role"]="calibration_regression",["recordings"]=Strings(ids),["state"]="owner_and_version_reviewed",["hash"]=manifestHash,["independent"]=false }));
        Store.AppendBatch(changes);
        foreach(var b in bindings) { Store.SetLocal("binding:"+b.Id,b.Key); Store.SetLocal("signature:"+b.Id,b.Signature); }
        Training.Invalidate(); Notify(); return new JsonObject { ["imported"]=11,["role"]="calibration_regression",["trainingRowsAdded"]=0,["profileId"]=profile };
    }
}
