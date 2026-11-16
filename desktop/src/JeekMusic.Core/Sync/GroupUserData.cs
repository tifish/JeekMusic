using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JeekMusic.Core.Sync;

public sealed record GroupSeed(string? Payload, long EditedAtMs = 0);

/// <summary>Group register identities and values, independent of legacy timestamps and receive order.</summary>
public static class GroupUserData
{
    static GroupSyncException Invalid() => new("invalid_user_data");
    static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];
    static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw Invalid();
    static long Stamp(JsonNode? node) => SyncMerge.Number(node);
    public static string TupleKey(string kind, string reference, string suffix) => kind + ":" + reference.Length.ToString(CultureInfo.InvariantCulture) + ":" + reference + ":" + suffix;

    public static string RebindKey(string key,string sourcePrefix,string targetPrefix)
        =>RebindKey(key,reference=>reference.StartsWith(sourcePrefix,StringComparison.Ordinal)?targetPrefix+reference[sourcePrefix.Length..]:reference);
    public static string RebindKey(string key,Func<string,string> referenceMap)
    {
        if(key.StartsWith("offset:",StringComparison.Ordinal))return "offset:"+referenceMap(key[7..]);
        foreach(var kind in new[] { "tag","language","features" })
            if(key.StartsWith(kind+":",StringComparison.Ordinal))
            {
                var tuple=ReadTuple(key,kind);return TupleKey(kind,referenceMap(tuple.Reference),tuple.Suffix);
            }
        return key;
    }

    static (string Reference, string Suffix) ReadTuple(string key, string kind)
    {
        var rest = key[(kind.Length + 1)..];
        int colon = rest.IndexOf(':');
        if (colon < 1 || !int.TryParse(rest[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 1 || length > 2048) throw Invalid();
        rest = rest[(colon + 1)..];
        if (rest.Length <= length || rest[length] != ':') throw Invalid();
        return (rest[..length], rest[(length + 1)..]);
    }

    public static Dictionary<string, GroupSeed> Flatten(JsonObject state)
    {
        var result = new Dictionary<string, GroupSeed>(StringComparer.Ordinal);
        foreach (var playlist in Objects(state["playlists"]))
        {
            var uid = Text(playlist["uid"]);
            result["playlist:" + uid] = new(Canonical(new JsonObject { ["name"] = Text(playlist["name"]), ["items"] = playlist["items"]?.DeepClone() ?? new JsonArray() }), Stamp(playlist["updated_at"]));
        }
        foreach (var deleted in Objects(state["deleted_playlists"]))
        {
            var key = "playlist:" + Text(deleted["uid"]);
            var stamp = Stamp(deleted["deleted_at"]);
            if (!result.TryGetValue(key, out var live) || stamp >= live.EditedAtMs) result[key] = new(null, stamp);
        }
        if (state["tag_overrides"] is JsonObject tags)
            foreach (var (reference, dimensions) in tags)
                foreach (var dimension in dimensions!.AsObject())
                    result[TupleKey("tag", reference, dimension.Key)] = new(dimension.Value?["value"] is { } value ? Canonical(value) : null, Stamp(dimension.Value?["updated_at"]));
        if (state["lyrics_offsets"] is JsonObject offsets)
            foreach (var (reference, entry) in offsets)
                result["offset:" + reference] = new(Canonical(entry!["offset_ms"]), Stamp(entry["updated_at"]));
        if (state["rules"] is JsonObject rules) result["rules"] = new(Canonical(rules["value"]), Stamp(rules["updated_at"]));
        if (state["nav"] is JsonObject nav) result["navigation"] = new(Canonical(nav["items"]), Stamp(nav["updated_at"]));
        if(state["vocal_languages"]?["items"] is JsonObject languages)
            foreach(var (reference,entries) in languages)
                foreach(var entry in Objects(entries))
                {
                    var payload=entry.DeepClone().AsObject();payload.Remove("updated_at");
                    result[TupleKey("language",reference,CacheIdentity(payload,true))]=new(Canonical(payload),Stamp(entry["updated_at"]));
                }
        foreach(var entry in Objects(state["analysis_cache"]))
        {
            var payload=entry.DeepClone().AsObject();payload.Remove("reference");payload.Remove("updated_at");
            result[TupleKey("features",Text(entry["reference"]),CacheIdentity(payload,false))]=new(Canonical(payload),Stamp(entry["updated_at"]));
        }
        return result;
    }

    /// <summary>Only explicitly supplied registers are materialized; missing keys never delete other data.</summary>
    public static JsonObject Materialize(IEnumerable<GroupMutation> mutations, long applyStamp)
    {
        var state = new JsonObject();
        foreach (var mutation in mutations)
        {
            GroupSyncContract.Validate(mutation);
            var key = mutation.Key;
            var value = mutation.Payload is { } payload ? ParsePayload(payload) : null;
            if (key.StartsWith("playlist:", StringComparison.Ordinal))
            {
                var uid = key[9..];
                if (uid.Length == 0 || uid.Length > 256) throw Invalid();
                if (mutation.Payload is null)
                {
                    state["deleted_playlists"] ??= new JsonArray();
                    state["deleted_playlists"]!.AsArray().Add((JsonNode)new JsonObject { ["uid"] = uid, ["deleted_at"] = applyStamp });
                }
                else
                {
                    if (value is not JsonObject playlist || playlist["items"] is not JsonArray items || items.Any(item => item is not JsonValue v || !v.TryGetValue<string>(out _))) throw Invalid();
                    state["playlists"] ??= new JsonArray();
                    state["playlists"]!.AsArray().Add((JsonNode)new JsonObject { ["uid"] = uid, ["name"] = Text(playlist["name"]), ["items"] = items.DeepClone(), ["updated_at"] = applyStamp });
                }
            }
            else if (key.StartsWith("tag:", StringComparison.Ordinal))
            {
                var (reference, dimension) = ReadTuple(key, "tag");
                if (dimension is not ("MOOD" or "VOICE" or "GENRE" or "CALMNESS")) throw Invalid();
                if (mutation.Payload is not null) _ = Text(value);
                state["tag_overrides"] ??= new JsonObject();
                state["tag_overrides"]![reference] ??= new JsonObject();
                state["tag_overrides"]![reference]![dimension] = new JsonObject { ["value"] = value?.DeepClone(), ["updated_at"] = applyStamp };
            }
            else if (key.StartsWith("offset:", StringComparison.Ordinal))
            {
                var reference = key[7..];
                if (reference.Length == 0) throw Invalid();
                long offset = mutation.Payload is null ? 0 : value is JsonValue integer && integer.TryGetValue<long>(out var wide) ? wide : throw Invalid();
                if (offset is < int.MinValue or > int.MaxValue) throw Invalid();
                state["lyrics_offsets"] ??= new JsonObject();
                state["lyrics_offsets"]![reference] = new JsonObject { ["offset_ms"] = offset, ["updated_at"] = applyStamp };
            }
            else if (key == "rules")
            {
                if (value is not JsonObject rules || rules["infer_from_file_name"] is not JsonValue boolean || !boolean.TryGetValue<bool>(out _) || rules["file_name_format"] is not JsonValue format || !format.TryGetValue<string>(out _)) throw Invalid();
                foreach(var field in new[] { "artist_separators","artist_exceptions" })
                    if(rules[field] is not JsonArray strings || strings.Count>1000 || strings.Any(n=>n is not JsonValue text || !text.TryGetValue<string>(out _)))throw Invalid();
                if(rules["legacy_charset"] is { } charset)_=Text(charset);
                state["rules"] = new JsonObject { ["value"] = value.DeepClone(), ["updated_at"] = applyStamp };
            }
            else if (key == "navigation")
            {
                if (value is not JsonArray navigation || navigation.Count>1000) throw Invalid();
                foreach(var entry in navigation) { if(entry is not JsonObject item)throw Invalid();_=Text(item["type"]);if(item["ref"] is { } reference)_=Text(reference); }
                state["nav"] = new JsonObject { ["items"] = value.DeepClone(), ["updated_at"] = applyStamp };
            }
            else if(IsCache(key))
            {
                var language=key.StartsWith("language:",StringComparison.Ordinal);var kind=language?"language":"features";
                var (reference,suffix)=ReadTuple(key,kind);
                if(value is not JsonObject cache || CacheIdentity(cache,language)!=suffix)throw Invalid();
                if(language)
                {
                    var source=Text(cache["source"]);var status=Text(cache["state"]);
                    if(source is not ("AUTO" or "MANUAL") || status is not ("known" or "unknown" or "instrumental" or "auto") || status=="auto" && source!="MANUAL")throw Invalid();
                    if(cache["languages"] is not JsonArray codes || codes.Count>16 || codes.Any(c=>c is not JsonValue text || !text.TryGetValue<string>(out var code) || !System.Text.RegularExpressions.Regex.IsMatch(code,"^(?:[a-z]{2,3}|other)$")) || codes.Select(Text).Distinct().Count()!=codes.Count || (status=="known")!=(codes.Count>0))throw Invalid();
                    if(cache["confidence"] is { } confidence && (confidence is not JsonValue number || !number.TryGetValue<double>(out var score) || !double.IsFinite(score) || score is <0 or >1))throw Invalid();
                    state["vocal_languages"]??=new JsonObject { ["version"]=1,["items"]=new JsonObject() };
                    var items=state["vocal_languages"]!["items"]!;items[reference]??=new JsonArray();
                    var row=cache.DeepClone().AsObject();row["updated_at"]=applyStamp;items[reference]!.AsArray().Add((JsonNode)row);
                }
                else
                {
                    if(cache["features"] is not JsonObject)throw Invalid();
                    state["analysis_cache"]??=new JsonArray();var row=cache.DeepClone().AsObject();row["reference"]=reference;row["updated_at"]=applyStamp;
                    state["analysis_cache"]!.AsArray().Add((JsonNode)row);
                }
            }
            else throw Invalid();
        }
        return state;
    }

    public static bool DeletesWhenMissing(string key) => key.StartsWith("playlist:", StringComparison.Ordinal) || key.StartsWith("tag:", StringComparison.Ordinal) || key.StartsWith("offset:", StringComparison.Ordinal);
    public static bool IsCache(string key)=>key.StartsWith("language:",StringComparison.Ordinal) || key.StartsWith("features:",StringComparison.Ordinal);
    static string CacheIdentity(JsonObject cache,bool language)
    {
        long Integer(string key) { if(cache[key] is not JsonValue value)throw Invalid();long result;if(value.TryGetValue<long>(out var wide))result=wide;else if(value.TryGetValue<int>(out var narrow))result=narrow;else throw Invalid();if(result<0 || result>GroupSyncContract.MaxInteger)throw Invalid();return result; }
        var prefix=language?Text(cache["source"]):Integer("analyzer_version").ToString(CultureInfo.InvariantCulture);
        return prefix+":"+Integer("size").ToString(CultureInfo.InvariantCulture)+":"+Integer("duration_ms").ToString(CultureInfo.InvariantCulture)+":"+Integer("start_ms").ToString(CultureInfo.InvariantCulture);
    }
    static JsonNode? ParsePayload(string payload)
    {
        var bytes=Encoding.UTF8.GetBytes(payload);var reader=new Utf8JsonReader(bytes,new JsonReaderOptions { MaxDepth=32 });
        var objects=new Stack<HashSet<string>>();int tokens=0;
        while(reader.Read())
        {
            if(++tokens>1_000_000)throw Invalid();
            if(reader.TokenType==JsonTokenType.StartObject)objects.Push(new(StringComparer.Ordinal));
            else if(reader.TokenType==JsonTokenType.EndObject)objects.Pop();
            else if(reader.TokenType==JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))throw Invalid();
            else if(reader.TokenType==JsonTokenType.String)_=reader.GetString();
        }
        return JsonNode.Parse(bytes,documentOptions:new JsonDocumentOptions { MaxDepth=32 });
    }

    // Quotes are specified explicitly so decoded Unicode, slash and HTML characters have one representation.
    public static string Canonical(JsonNode? value)
    {
        if (value is null) return "null";
        if (value is JsonObject obj) return "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Quote(p.Key) + ":" + Canonical(p.Value))) + "}";
        if (value is JsonArray array) return "[" + string.Join(",", array.Select(Canonical)) + "]";
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return Quote(text);
        return value.ToJsonString();
    }
    static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var c in value)
            result.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture), _ => c.ToString() });
        return result.Append('"').ToString();
    }
}
