using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace JeekMusic.Core.Sync;

public sealed record SyncChange(string Kind, string Key, string DeviceId, string DeviceName);
public sealed record ReplacedPlaylist(JsonObject Old, bool Deleted, string DeviceName);
public sealed record MergeResult(JsonObject State, IReadOnlyList<SyncChange> Changes, IReadOnlyList<ReplacedPlaylist> Replaced);

/// <summary>版本 2 同步协议。纯合并函数；不认识的歌曲引用原样保留，不把缺席当作删除。</summary>
public static class SyncMerge
{
    public const long RetentionMs = 180L * 86400000;
    public static string LegacyUid(string name) => "legacy-" + Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(name)));
    public static string Str(JsonNode? n) => n?.GetValue<string>() ?? "";
    public static long Number(JsonNode? n) => n is JsonValue value
        ? value.TryGetValue<long>(out var wide) ? wide : value.TryGetValue<int>(out var integer) ? integer : 0
        : 0;
    public static IEnumerable<JsonObject> Objects(JsonNode? n) => n is JsonArray a ? a.OfType<JsonObject>() : [];
    static JsonObject Copy(JsonObject n) => (JsonObject)n.DeepClone();
    static string Id(JsonObject file) => Str(file["device"]?["id"]);
    public static JsonArray NormalizeNavigation(JsonNode? items)
    {
        string[] builtins = ["FOLDERS", "ARTISTS", "PLAYLISTS"];
        var result = new JsonArray();
        var seen = new HashSet<(string, string?)>();
        foreach (var item in Objects(items))
        {
            var type = Str(item["type"]);
            var reference = type == "ARTIST" ? Library.ArtistSplitter.NameKey(Str(item["ref"])) : null;
            if ((type == "ARTIST" ? string.IsNullOrEmpty(reference) : !builtins.Contains(type)) || !seen.Add((type, reference))) continue;
            result.Add((JsonNode)new JsonObject { ["type"] = type, ["ref"] = reference });
        }
        foreach (var type in builtins)
            if (seen.Add((type, null))) result.Add((JsonNode)new JsonObject { ["type"] = type, ["ref"] = null });
        return result;
    }
    public static JsonObject? ParseFile(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject file || Number(file["format"]) != 2 || Id(file).Length == 0) return null;
            static bool Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out _);
            static bool Stamp(JsonNode? n) => n is JsonValue v && (v.TryGetValue<long>(out var l) && l >= 0 || v.TryGetValue<int>(out var i) && i >= 0);
            static bool ArrayOf(JsonNode? n, Func<JsonNode?, bool> valid) => n is JsonArray a && a.All(valid);
            if (!Text(file["device"]?["name"]) || !Stamp(file["written_at"])) return null;
            if (file["playlists"] is { } lists && !ArrayOf(lists, p => p is JsonObject && Text(p["uid"]) && Text(p["name"]) && Stamp(p["updated_at"]) && ArrayOf(p["items"], Text))) return null;
            if (file["deleted_playlists"] is { } deleted && !ArrayOf(deleted, p => p is JsonObject && Text(p["uid"]) && Stamp(p["deleted_at"]))) return null;
            if (file["nav"] is { } nav && (nav is not JsonObject || !Stamp(nav["updated_at"]) || !ArrayOf(nav["items"], n => n is JsonObject && Text(n["type"]) && (n["ref"] is null || Text(n["ref"]))))) return null;
            if (file["rules"] is { } rules)
            {
                if (rules is not JsonObject || !Stamp(rules["updated_at"]) || rules["value"] is not JsonObject value) return null;
                foreach (var key in new[] { "artist_separators", "artist_exceptions" }) if (value[key] is { } a && !ArrayOf(a, Text)) return null;
                foreach (var key in new[] { "file_name_format", "legacy_charset" }) if (value[key] is { } s && !Text(s)) return null;
                if (value["infer_from_file_name"] is { } flag && (flag is not JsonValue v || !v.TryGetValue<bool>(out _))) return null;
            }
            foreach (var category in new[] { "tag_overrides", "lyrics_offsets" })
            {
                if (file[category] is null) continue;
                if (file[category] is not JsonObject map) return null;
                foreach (var item in map)
                {
                    if (item.Value is not JsonObject entry) return null;
                    if (category == "lyrics_offsets")
                    {
                        if (!Stamp(entry["updated_at"]) || entry["offset_ms"] is not JsonValue offset || !offset.TryGetValue<int>(out _)) return null;
                    }
                    else foreach (var dim in entry)
                        if (dim.Value is not JsonObject d || !Stamp(d["updated_at"]) || d["value"] is { } val && !Text(val)) return null;
                }
            }
            return file;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    public static JsonObject NormalizeReferences(JsonObject state, Func<string, string> canonical)
    {
        var result = Copy(state);
        foreach (var p in Objects(result["playlists"]))
            if (p["items"] is JsonArray items)
                p["items"] = new JsonArray(items.Select(i => (JsonNode?)JsonValue.Create(canonical(Str(i)))).ToArray());
        foreach (var category in new[] { "tag_overrides", "lyrics_offsets" })
        {
            if (state[category] is not JsonObject map) continue;
            var normalized = new JsonObject();
            foreach (var item in map.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var key = canonical(item.Key);
                if (category == "tag_overrides")
                {
                    if (item.Value is not JsonObject dims) continue;
                    if (normalized[key] is null) normalized[key] = new JsonObject();
                    foreach (var dim in dims)
                        if (normalized[key]![dim.Key] is not { } old || Number(dim.Value?["updated_at"]) > Number(old["updated_at"]))
                            normalized[key]![dim.Key] = dim.Value?.DeepClone();
                }
                else if (normalized[key] is not { } old || Number(item.Value?["updated_at"]) > Number(old["updated_at"]))
                    normalized[key] = item.Value?.DeepClone();
            }
            result[category] = normalized;
        }
        return result;
    }
    public static IReadOnlyList<JsonObject> LatestPerDevice(IEnumerable<JsonObject> files, string self) => files
        .Where(f => Number(f["format"]) == 2 && Id(f).Length > 0 && Id(f) != self)
        .GroupBy(Id).Select(g => g.MaxBy(f => Number(f["written_at"]))!).OrderBy(Id, StringComparer.Ordinal).ToList();

    public static MergeResult Merge(JsonObject local, IEnumerable<JsonObject> files, string self, long now)
    {
        var remotes = LatestPerDevice(files, self);
        var changes = new List<SyncChange>();
        var replaced = new List<ReplacedPlaylist>();
        void Change(string kind, string key, JsonObject file) => changes.Add(new(kind, key, Id(file), Str(file["device"]?["name"])));
        var result = new JsonObject();
        foreach (var (key, valueKey) in new[] { ("rules", "value"), ("nav", "items") })
        {
            var best = local[key] as JsonObject; JsonObject? from = null;
            foreach (var f in remotes)
                if (f[key] is JsonObject candidate && (best is null || Number(candidate["updated_at"]) > Number(best["updated_at"]))) { best = candidate; from = f; }
            if (best is not null) result[key] = best.DeepClone();
            if (from is not null && !JsonNode.DeepEquals(best?[valueKey], local[key]?[valueKey])) Change(key, "", from);
        }
        var playlists = new JsonArray(); var deleted = new JsonArray();
        var all = new[] { local }.Concat(remotes).ToList();
        var ids = all.SelectMany(f => Objects(f["playlists"]).Concat(Objects(f["deleted_playlists"]))).Select(p => Str(p["uid"])).Distinct().Order(StringComparer.Ordinal);
        foreach (var uid in ids)
        {
            JsonObject? Find(JsonObject f, string key) => Objects(f[key]).FirstOrDefault(p => Str(p["uid"]) == uid);
            var mine = Find(local, "playlists"); var best = mine;
            JsonObject? from = null, deletedFrom = null;
            long? deletedAt = Find(local, "deleted_playlists") is { } d ? Number(d["deleted_at"]) : null;
            foreach (var file in remotes)
            {
                if (Find(file, "playlists") is { } p && (best is null || Number(p["updated_at"]) > Number(best["updated_at"]))) { best = p; from = file; }
                if (Find(file, "deleted_playlists") is { } tombstone && (deletedAt is null || Number(tombstone["deleted_at"]) > deletedAt))
                { deletedAt = Number(tombstone["deleted_at"]); deletedFrom = file; }
            }
            if (deletedAt is { } at && now - at > RetentionMs) deletedAt = null;
            if (best is not null && (deletedAt is null || Number(best["updated_at"]) > deletedAt))
            {
                playlists.Add(best.DeepClone());
                if (from is not null && (mine is null || !JsonNode.DeepEquals(mine["name"], best["name"]) || !JsonNode.DeepEquals(mine["items"], best["items"])))
                { Change("playlist", uid, from); if (mine is not null) replaced.Add(new(Copy(mine), false, Str(from["device"]?["name"]))); }
            }
            else
            {
                if (deletedAt is not null) deleted.Add((JsonNode)new JsonObject { ["uid"] = uid, ["deleted_at"] = deletedAt.Value });
                if (mine is not null && deletedFrom is not null)
                { Change("playlist", uid, deletedFrom); replaced.Add(new(Copy(mine), true, Str(deletedFrom["device"]?["name"]))); }
            }
        }
        result["playlists"] = playlists; result["deleted_playlists"] = deleted;
        var overrides = new JsonObject();
        var refs = all.SelectMany(f => (f["tag_overrides"] as JsonObject ?? new()).Select(x => x.Key)).Distinct().Order(StringComparer.Ordinal);
        foreach (var reference in refs)
        {
            var dims = all.SelectMany(f => (f["tag_overrides"]?[reference] as JsonObject ?? new()).Select(x => x.Key)).Distinct().Order(StringComparer.Ordinal);
            foreach (var dim in dims)
            {
                var mine = local["tag_overrides"]?[reference]?[dim]; var best = mine; JsonObject? from = null;
                foreach (var file in remotes)
                    if (file["tag_overrides"]?[reference]?[dim] is { } candidate && (best is null || Number(candidate["updated_at"]) > Number(best["updated_at"]))) { best = candidate; from = file; }
                if (best is null || best["value"] is null && now - Number(best["updated_at"]) > RetentionMs) continue;
                if (overrides[reference] is null) overrides[reference] = new JsonObject();
                overrides[reference]![dim] = best.DeepClone();
                if (from is not null && !JsonNode.DeepEquals(best["value"], mine?["value"])) Change("tag_override", reference + "|" + dim, from);
            }
        }
        result["tag_overrides"] = overrides;
        var offsets = new JsonObject();
        refs = all.SelectMany(f => (f["lyrics_offsets"] as JsonObject ?? new()).Select(x => x.Key)).Distinct().Order(StringComparer.Ordinal);
        foreach (var reference in refs)
        {
            var mine = local["lyrics_offsets"]?[reference]; var best = mine; JsonObject? from = null;
            foreach (var file in remotes)
                if (file["lyrics_offsets"]?[reference] is { } candidate && (best is null || Number(candidate["updated_at"]) > Number(best["updated_at"]))) { best = candidate; from = file; }
            if (best is null || Number(best["offset_ms"]) == 0 && now - Number(best["updated_at"]) > RetentionMs) continue;
            offsets[reference] = best.DeepClone();
            if (from is not null && Number(best["offset_ms"]) != Number(mine?["offset_ms"])) Change("lyrics_offset", reference, from);
        }
        result["lyrics_offsets"] = offsets;
        return new(result, changes, replaced);
    }

    public static JsonObject? ParseLegacy(string text)
    {
        var old = JsonNode.Parse(text) as JsonObject;
        if (old is null || Number(old["format"]) != 1) return null;
        const long stamp = 1;
        var state = new JsonObject { ["format"] = 2, ["device"] = new JsonObject { ["id"] = "legacy-backup", ["name"] = "旧版备份" }, ["written_at"] = stamp,
            ["tag_overrides"] = new JsonObject(), ["lyrics_offsets"] = new JsonObject(), ["deleted_playlists"] = new JsonArray() };
        if (old["settings"] is JsonObject settings)
        {
            var rules = new JsonObject();
            foreach (var key in new[] { "artist_separators", "artist_exceptions", "infer_from_file_name", "file_name_format", "legacy_charset" }) rules[key] = settings[key]?.DeepClone();
            state["rules"] = new JsonObject { ["updated_at"] = stamp, ["value"] = rules };
        }
        if (old["pinned_tabs"] is JsonArray tabs) state["nav"] = new JsonObject { ["updated_at"] = stamp, ["items"] = tabs.DeepClone() };
        state["playlists"] = new JsonArray(Objects(old["playlists"]).Select(p => (JsonNode)new JsonObject { ["uid"] = LegacyUid(Str(p["name"])), ["name"] = p["name"]?.DeepClone(), ["updated_at"] = stamp, ["items"] = p["items"]?.DeepClone() ?? new JsonArray() }).ToArray());
        return state;
    }
}
