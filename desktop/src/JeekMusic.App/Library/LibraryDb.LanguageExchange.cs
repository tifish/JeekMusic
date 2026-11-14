using System.Text.Json.Nodes;

namespace JeekMusic.Library;

public sealed partial class LibraryDb
{
    public JsonObject ExportLanguages(IReadOnlyDictionary<string,string> references)
    {
        lock(Gate) {
            var tracks=AllTracks().ToDictionary(t=>t.TrackKey);
            var items=new JsonObject();
            using var cmd=Command("SELECT track_key,signature,data,source,updated_at FROM track_language");
            using var reader=cmd.ExecuteReader();
            while(reader.Read()) {
                var key=reader.GetString(0);
                if(!tracks.TryGetValue(key,out var track) || !references.TryGetValue(key,out var reference) || reader.GetString(1)!=SingingLanguage.Signature(track)) continue;
                var value=SingingLanguage.Parse(reader.GetString(2),reader.GetString(3));
                var rows=items[reference] as JsonArray;
                if(rows is null) items[reference]=rows=new JsonArray();
                rows.Add((JsonNode)new JsonObject { ["source"]=value.Source,["state"]=value.State,["languages"]=new JsonArray(value.Languages.Select(l=>(JsonNode?)JsonValue.Create(l)).ToArray()),
                    ["confidence"]=value.Confidence,["updated_at"]=reader.GetInt64(4),["size"]=track.Size,["duration_ms"]=track.DurationMs,["start_ms"]=track.StartMs??0 });
            }
            return new JsonObject { ["version"]=1,["items"]=items };
        }
    }
    public bool ImportLanguages(JsonObject? input,Func<string,string?> match)
    {
        if(input is null) return false;
        if(input["version"]?.GetValue<int>()!=1 || input["items"] is not JsonObject items || items.Count>100000) throw new ArgumentException("Unsupported vocal-language exchange");
        var changed=false;
        InTransaction(()=> {
            var tracks=AllTracks().ToDictionary(t=>t.TrackKey);
            foreach(var (reference,node) in items) {
                var key=match(reference);
                if(key is null || !tracks.TryGetValue(key,out var track)) continue;
                if(node is not JsonArray rows || rows.Count>2) throw new ArgumentException("Invalid vocal-language records");
                var seen=new HashSet<string>(StringComparer.Ordinal);
                foreach(var row in rows.OfType<JsonObject>()) {
                    var source=row["source"]!.GetValue<string>(); var state=row["state"]!.GetValue<string>();
                    if(!seen.Add(source)) throw new ArgumentException("Duplicate vocal-language source");
                    var languages=row["languages"]!.AsArray().Select(l=>l!.GetValue<string>()).ToArray();
                    var at=row["updated_at"]!.GetValue<long>(); var confidence=row["confidence"]?.GetValue<double>();
                    if(source is not ("AUTO" or "MANUAL") || state is not ("known" or "unknown" or "instrumental" or "auto") || state=="auto" && source!="MANUAL" ||
                        languages.Length>16 || languages.Distinct().Count()!=languages.Length || languages.Any(l=>!System.Text.RegularExpressions.Regex.IsMatch(l,"^(?:[a-z]{2,3}|other)$")) ||
                        (state=="known")!=(languages.Length>0) || at<0 || confidence is { } p && (!double.IsFinite(p) || p<0 || p>1)) throw new ArgumentException("Invalid vocal-language evidence");
                    if(row["size"]!.GetValue<long>()!=track.Size || Math.Abs(row["duration_ms"]!.GetValue<long>()-track.DurationMs)>1000 || row["start_ms"]!.GetValue<long>()!=(track.StartMs??0)) continue;
                    using var prior=Command("SELECT updated_at,data FROM track_language WHERE track_key=$k AND source=$s AND signature=$sig");
                    prior.Parameters.AddWithValue("$k",key); prior.Parameters.AddWithValue("$s",source); prior.Parameters.AddWithValue("$sig",SingingLanguage.Signature(track));
                    using(var reader=prior.ExecuteReader()) if(reader.Read()) {
                        var existing=reader.GetInt64(0);
                        if(existing>at) continue;
                        if(existing==at) {
                            var saved=SingingLanguage.Parse(reader.GetString(1),source);
                            var previousKey=saved.State+":"+string.Join(",",saved.Languages.Order(StringComparer.Ordinal));
                            var incomingKey=state+":"+string.Join(",",languages.Order(StringComparer.Ordinal));
                            var order=string.CompareOrdinal(previousKey,incomingKey);
                            if(order>0 || order==0 && (saved.Confidence??-1)>=(confidence??-1)) continue;
                        }
                    }
                    var value=new SingingLanguage(state,languages,source,confidence);
                    Exec("INSERT INTO track_language(track_key,signature,data,source,updated_at) VALUES($k,$sig,$d,$s,$at) ON CONFLICT(track_key,source) DO UPDATE SET signature=excluded.signature,data=excluded.data,updated_at=excluded.updated_at",null,
                        ("$k",key),("$sig",SingingLanguage.Signature(track)),("$d",value.ToJson().ToJsonString()),("$s",source),("$at",at));
                    changed=true;
                }
            }
        });
        return changed;
    }
}
