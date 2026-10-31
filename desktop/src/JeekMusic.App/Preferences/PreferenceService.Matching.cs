using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

public sealed partial class PreferenceService
{
    public async Task<JsonObject> MatchLocalBatch(CancellationToken token=default)
    {
        if(!await preparation.WaitAsync(0,token)) throw new InvalidOperationException("Identity preparation already running");
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var profile=RequireProfile(); var batch=Batch(); var manifest=S(batch["manifestId"]);
            var ids=batch["items"]!.AsArray().Select(n=>S(n?["recordingId"])).ToHashSet();
            var records=Store.Current(profile,"recording").Where(e=>ids.Contains(S(e["entityId"]))).Select(e=>e["payload"]!).ToArray();
            var candidates=app.Library.Snapshot.Tracks.Where(t=>!t.IsCue && app.Library.PathOf(t)!=null && records.Any(r=>Math.Abs((r["durationMs"]?.GetValue<long>()??0)-t.DurationMs)<=1000))
                .OrderBy(t=>Order("identity-match-v2",t.TrackKey),StringComparer.Ordinal).ToArray();
            int offset=int.TryParse(Store.Local("matchCursor:"+manifest),out var n)?n:0;
            if(offset>=candidates.Length) offset=0;
            int checkedFiles=0,matched=0,stale=0;
            foreach(var t in candidates.Skip(offset).Take(6))
            {
                deadline.Token.ThrowIfCancellationRequested(); var path=app.Library.PathOf(t)!; var stat=new FileInfo(path); checkedFiles++;
                if(!stat.Exists || stat.Length!=t.Size || stat.LastWriteTimeUtc.Ticks!=t.Mtime) { stale++; continue; }
                using var stream=File.OpenRead(path); var signature=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,deadline.Token));
                stat.Refresh(); if(stat.Length!=t.Size || stat.LastWriteTimeUtc.Ticks!=t.Mtime) { stale++; continue; }
                var id=Hash("sha256:"+signature+":whole");
                if(!ids.Contains(id) || !records.Any(r=>S(r["recordingId"])==id && S(r["contentSignature"])==signature)) continue;
                Store.SetLocal("binding:"+id,t.TrackKey); Store.SetLocal("signature:"+id,$"{t.Mtime}:{t.Size}"); matched++;
            }
            Store.SetLocal("matchCursor:"+manifest,(offset+checkedFiles).ToString(System.Globalization.CultureInfo.InvariantCulture)); Notify();
            return new JsonObject { ["checkedFiles"]=checkedFiles,["matched"]=matched,["stale"]=stale,["hasMoreCandidates"]=offset+checkedFiles<candidates.Length,["unavailableReferences"]=ids.Count(id=>FindLocal(id)==null),["bindingMethod"]="exact_full_content_sha256_never_title" };
        }
        finally { preparation.Release(); }
    }
}
