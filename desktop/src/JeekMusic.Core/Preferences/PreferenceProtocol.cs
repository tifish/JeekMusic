using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace JeekMusic.Core.Preferences;

/// <summary>Shared wire rules. Paths, automatic tags and listening behaviour never become answers.</summary>
public static class PreferenceProtocol
{
    public const int Version = 2;
    public const string Context = "general_workout_v1";
    public const string Extractor = "clap-htsat-unfused@8fa0f1c6d0433df6e97c127f64b2a1d6c0dcda8a/center10-rms01-v1";
    public static readonly string[] Dimensions = ["MOOD","VOICE","GENRE","CALMNESS"];
    public static readonly string[] Answers = ["SPORT", "NON_SPORT", "VOCAL", "INSTRUMENTAL", "CLASSICAL", "NON_CLASSICAL", "CALM", "NON_CALM", "UNCERTAIN", "UNRESOLVED", "SKIP", "WITHDRAWN"];
    public static string Dimension(JsonNode? payload) => string.IsNullOrEmpty(S(payload?["dimension"])) ? "MOOD" : S(payload?["dimension"]);
    public static string ContextFor(string dimension)=>dimension=="MOOD"?Context:dimension.ToLowerInvariant()+"_personal_v1";
    public static (string Positive,string Negative) Labels(string dimension)=>dimension switch { "MOOD"=>("SPORT","NON_SPORT"),"VOICE"=>("VOCAL","INSTRUMENTAL"),"GENRE"=>("CLASSICAL","NON_CLASSICAL"),"CALMNESS"=>("CALM","NON_CALM"),_=>throw new ArgumentException("Unknown dimension") };
    public static bool Binary(string label,string dimension) { var pair=Labels(dimension); return label==pair.Positive || label==pair.Negative; }
    public static readonly string[] Bases = ["whole_recording_familiar", "listened_full", "excerpts_only", "unclear"];
    public static string[] SupportedHeads(JsonNode study)
    {
        var heads=ReadStrings(study["supportedHeads"]);
        if(heads.Length>0) return heads;
        return study["headProtocols"]?["protocol"]?.GetValue<int>()==2 && ReadStrings(study["training"]).Length==48 && ReadStrings(study["development"]).Length==12 && ReadStrings(study["sealed_test"]).Length==48 ? Dimensions : ["MOOD"];
    }
    public static JsonObject HeadContract()
    {
        using var stream=typeof(PreferenceProtocol).Assembly.GetManifestResourceStream("JeekMusic.PersonalHeadContract")!;
        using var reader=new StreamReader(stream); return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string S(JsonNode? n) => n?.GetValue<string>() ?? "";
    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    public static string[] ReadStrings(JsonNode? n) => n is JsonArray a ? a.Select(S).ToArray() : [];

    public static void Validate(JsonObject e)
    {
        if (e["protocol"]?.GetValue<int>() is not (1 or Version)) throw new ArgumentException("Unsupported preference protocol");
        foreach (var key in new[] { "eventId", "profileId", "entityId", "deviceId" })
            if (S(e[key]).Length is < 1 or > 160 || S(e[key]).Any(char.IsControl)) throw new ArgumentException("Invalid " + key);
        if (S(e["kind"]) is not ("profile" or "recording" or "manifest" or "answer" or "model")) throw new ArgumentException("Invalid record kind");
        if (e["revision"]?.GetValue<long>() is not > 0 || e["at"]?.GetValue<long>() is not > 0 || e["parents"] is not JsonArray parents || parents.Count > 64 || e["payload"] is not JsonObject p)
            throw new ArgumentException("Invalid revision");
        if (ReadStrings(parents).Any(s => s.Length is < 1 or > 160) || e.ToJsonString().Length > 128_000) throw new ArgumentException("Record too large");
        if (S(e["kind"]) == "answer")
        {
            var dimension=Dimension(p);
            if (!Dimensions.Contains(dimension) || !Answers.Contains(S(p["answer"])) || (S(p["answer"]) is not ("UNCERTAIN" or "UNRESOLVED" or "SKIP" or "WITHDRAWN") && !Binary(S(p["answer"]),dimension)) || !Bases.Contains(S(p["basis"])) || S(p["reason"]).Length > 500 || S(p["contextId"]) != ContextFor(dimension) || S(p["recordingId"]).Length != 64 || S(p["contentSignature"]).Length != 64 || S(p["manifestId"]).Length == 0)
                throw new ArgumentException("Invalid explicit answer");
            if (S(p["role"]) is not ("training" or "development" or "sealed_test" or "calibration_regression")) throw new ArgumentException("Invalid purpose");
        }
    }

    // Concurrent revisions remain visible. No clock/device-id last-writer-wins for human truth.
    public static List<JsonObject> Heads(IEnumerable<JsonObject> events)
    {
        var all = events.ToList();
        var superseded = all.SelectMany(e => ReadStrings(e["parents"])).ToHashSet(StringComparer.Ordinal);
        return all.Where(e => !superseded.Contains(S(e["eventId"]))).OrderBy(e => S(e["eventId"]), StringComparer.Ordinal).ToList();
    }
    public static bool UsableAnswer(JsonObject e) => S(e["payload"]?["source"]) is "explicit_human_blind_batch" or "synthetic_fixture" && e["payload"]?["repeat"]?.GetValue<bool>() != true && S(e["kind"]) == "answer" && S(e["payload"]?["role"]) == "training" &&
        Binary(S(e["payload"]?["answer"]),Dimension(e["payload"])) && S(e["payload"]?["basis"]) is "whole_recording_familiar" or "listened_full";

    public sealed record Identity(string Id, string Content, string Work, string Album, string[] Artists, bool Reviewed, bool Exposed, bool Supported);
    public sealed record Grouping(Dictionary<string, string> Components, string[] Unresolved);
    public static Grouping Group(IReadOnlyList<Identity> rows)
    {
        var parent = rows.ToDictionary(r => r.Id, r => r.Id);
        string Root(string id) { while (parent[id] != id) { parent[id] = parent[parent[id]]; id = parent[id]; } return id; }
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
            foreach (var key in new[] { "content:" + r.Content, "work:" + r.Work, "album:" + r.Album }.Concat(r.Artists.Select(a => "artist:" + a)).Where(k => !k.EndsWith(':')))
                if (seen.TryGetValue(key, out var other)) { var a = Root(r.Id); var b = Root(other); if (a != b) parent[string.CompareOrdinal(a, b) < 0 ? b : a] = string.CompareOrdinal(a, b) < 0 ? a : b; }
                else seen[key] = r.Id;
        return new(rows.ToDictionary(r => r.Id, r => Root(r.Id)), rows.Where(r => !r.Reviewed || string.IsNullOrEmpty(r.Work) || string.IsNullOrEmpty(r.Album) || r.Artists.Length == 0).Select(r => r.Id).ToArray());
    }
    public static string Order(string seed, string id) => Hash(seed + ":" + id);
}
