using System.Text.Json.Nodes;
using JeekMusic.Core.Library;
using JeekMusic.Core.Sync;
using static JeekMusic.Core.Sync.SyncMerge;

namespace JeekMusic.Sync;

public sealed partial class SyncFeature
{
    internal sealed record GroupContext(Dictionary<string,string> References,Func<string,string> Canonical,Func<string,string?> Match);
    internal GroupContext PrepareGroupContext(IReadOnlyDictionary<string,string> bindings)
    {
        string Canonical(string reference)
        {
            if(reference.StartsWith("root:",StringComparison.Ordinal))
            {
                int slash=reference.IndexOf('/',5);
                if(slash>5) { var root=reference[5..slash];return "collection:"+bindings.GetValueOrDefault(root,root)+reference[slash..]; }
            }
            return BackupRefs.IsManagedReference(reference)?reference:"device:"+DeviceId+"/"+reference;
        }
        var legacy=References();
        var references=legacy.ToDictionary(p=>p.Key,p=>BackupRefs.IsManagedReference(p.Value)?Canonical(p.Value):"device:"+DeviceId+"/local:"+p.Key);
        var index=references.GroupBy(p=>p.Value,StringComparer.Ordinal).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single().Key,StringComparer.Ordinal);
        foreach(var pair in legacy.Where(p=>BackupRefs.IsManagedReference(p.Value)))index.TryAdd(pair.Value,pair.Key);
        string? Match(string reference)=>index.GetValueOrDefault(reference)??index.GetValueOrDefault(Canonical(reference));
        return new(references,Canonical,Match);
    }
    internal JsonObject CaptureGroupState(GroupContext context)
    {
        var state=NormalizeReferences(Capture(context.References),context.Canonical);
        state["vocal_languages"]=_app.Db.ExportLanguages(context.References);
        var cache=new JsonArray();
        foreach(var row in _app.Db.AllFeatures().Where(r=>!r.Failed && r.AnalyzerVersion==Analysis.AnalysisFeature.AnalyzerVersion))
            if(context.References.TryGetValue(row.TrackKey,out var reference) && _app.Library.ByKey(row.TrackKey) is { } track && row.Signature==Analysis.AnalysisFeature.Signature(track) && row.Data is not null && Analysis.FeaturesData.Parse(row.Data)?.Complete==true)
                cache.Add((JsonNode)new JsonObject { ["reference"]=reference,["analyzer_version"]=row.AnalyzerVersion,["size"]=track.Size,["duration_ms"]=track.DurationMs,["start_ms"]=track.StartMs??0L,["features"]=JsonNode.Parse(row.Data) });
        state["analysis_cache"]=cache;return state;
    }
    internal bool NormalizeGroupLocal(GroupContext context)
    {
        var oldKeys=_app.Db.TagOverrides().Select(t=>t.TrackKey).Concat(_app.Db.LyricsOffsets().Select(t=>t.TrackKey)).Where(k=>k.StartsWith("foreign:",StringComparison.Ordinal)).Distinct().ToArray();
        bool changed=oldKeys.Any(key=>context.Match(context.Canonical(key[8..])) is not null || context.Canonical(key[8..])!=key[8..]) ||
            _app.Db.AllPlaylistItems().Values.SelectMany(items=>items).Any(item=>item.ForeignRef is { } reference && (context.Match(context.Canonical(reference)) is not null || context.Canonical(reference)!=reference));
        if(!changed)return false;
        _applying=true;
        try
        {
            Apply(new(CaptureGroupState(context),[],[]),context.Match,prune:false);
            foreach(var key in oldKeys.Where(k=>context.Canonical(k[8..])!=k[8..]))
            {
                _app.Db.Exec("DELETE FROM tag_override WHERE track_key=$key",null,("$key",key));
                _app.Db.Exec("DELETE FROM lyrics_offset WHERE track_key=$key",null,("$key",key));
            }
        }
        finally { _applying=false; }
        return true;
    }
    internal void ApplyGroupRegisters(GroupContext context,IReadOnlyList<GroupMutation> mutations)
    {
        var local=CaptureGroupState(context);
        var newest=GroupUserData.Flatten(local).Values.Select(s=>s.EditedAtMs).DefaultIfEmpty(0).Max();
        if(newest==long.MaxValue)throw new GroupSyncException("clock_exhausted");
        var stamp=Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),newest+1);
        var changes=GroupUserData.Materialize(mutations.Where(m=>!GroupUserData.IsCache(m.Key)),stamp);
        foreach(var mutation in mutations.Where(m=>GroupUserData.IsCache(m.Key)))
        {
            var cache=GroupUserData.Materialize([mutation],stamp);
            if(cache["vocal_languages"] is JsonObject languages)_app.Db.ImportLanguages(languages,context.Match);
            foreach(var entry in Objects(cache["analysis_cache"]))
            {
                if(Number(entry["analyzer_version"])!=Analysis.AnalysisFeature.AnalyzerVersion || context.Match(Str(entry["reference"])) is not { } key || _app.Library.ByKey(key) is not { } track ||
                    Number(entry["size"])!=track.Size || Number(entry["duration_ms"])!=track.DurationMs || Number(entry["start_ms"])!=(track.StartMs??0L) || entry["features"] is not JsonObject data || Analysis.FeaturesData.Parse(data.ToJsonString())?.Complete!=true)continue;
                if(_app.Db.Features(key) is { Failed:false } old && old.AnalyzerVersion==Analysis.AnalysisFeature.AnalyzerVersion && old.Signature==Analysis.AnalysisFeature.Signature(track) && Analysis.FeaturesData.Parse(old.Data)?.Complete==true)continue;
                _app.Db.SaveFeatures(new Library.FeaturesRow(key,Analysis.AnalysisFeature.Signature(track),Analysis.AnalysisFeature.AnalyzerVersion,data.ToJsonString(),false));
            }
        }
        changes["format"]=2;changes["device"]=new JsonObject { ["id"]="group-projection",["name"]="同步组" };changes["written_at"]=stamp;
        var merged=Merge(local,[changes],DeviceId,stamp,retainTombstones:true);
        // Rules live in a JSON settings file. Persist intent inside the DB transaction, then apply after commit.
        if(changes["rules"] is { } rules) _app.Db.SetValue("group_pending_rules",rules.ToJsonString());
        merged.State.Remove("rules");
        _applying=true;
        try
        {
            Apply(merged,context.Match,prune:false);
            _app.Db.Exec("DELETE FROM sync_history WHERE replaced_at<$cutoff",null,("$cutoff",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-30L*86_400_000));
        }
        finally { _applying=false; }
    }
    internal void ApplyPendingGroupRules()
    {
        var text=_app.Db.GetValue("group_pending_rules");if(text is null)return;
        var rules=JsonNode.Parse(text)!.AsObject();var value=rules["value"]!;
        var settings=_app.Settings.Current;
        settings.ArtistSeparators=ReadStrings(value["artist_separators"]);settings.ArtistExceptions=ReadStrings(value["artist_exceptions"]);
        settings.InferFromFileName=value["infer_from_file_name"]?.GetValue<bool>()??true;
        settings.FileNameFormat=Str(value["file_name_format"]);settings.LegacyCharset=value["legacy_charset"]?.GetValue<string>();
        settings.RulesUpdatedAt=Number(rules["updated_at"]);_app.Settings.Save("sync-rules");_app.Settings.WriteNow();
        _app.Db.SetValue("group_pending_rules",null);
    }
    internal Dictionary<string,GroupSeed> GroupProjection(GroupContext context)
    {
        var state=CaptureGroupState(context);
        if(_app.Db.GetValue("group_pending_rules") is { } rules)state["rules"]=JsonNode.Parse(rules);
        return GroupUserData.Flatten(state);
    }
    internal void GroupProjectionChanged()
    {
        _app.Playlists.Invalidate();Analysis.AnalysisFeature.Current.Invalidate();Changed?.Invoke();
    }
}
