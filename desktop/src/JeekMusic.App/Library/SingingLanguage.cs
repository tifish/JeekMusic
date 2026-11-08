using System.Text.Json.Nodes;

namespace JeekMusic.Library;

/// <summary>Actual vocal languages from audio or explicit user correction; no title heuristics.</summary>
public sealed record SingingLanguage(string State, IReadOnlyList<string> Languages, string Source, double? Confidence = null, JsonArray? Samples = null)
{
    public static string Signature(Track t) => $"{t.Mtime}:{t.Size}:{t.StartMs}:{t.EndMs}:{t.DurationMs}";
    public const string Policy = "audio-vocal-language-v1-known-only-negatives-mixed-any-positive";
    public bool Matches(string tag)
    {
        if (State != "known") return false;
        var present = tag is "CHINESE" or "NON_CHINESE"
            ? Languages.Any(l => l is "zh" or "yue") : Languages.Contains("en");
        return tag is "NON_CHINESE" or "NON_ENGLISH" ? !present : present;
    }
    public JsonObject ToJson() => new() {
        ["state"]=State, ["languages"]=new JsonArray(Languages.Select(l=>(JsonNode?)JsonValue.Create(l)).ToArray()),
        ["source"]=Source, ["confidence"]=Confidence, ["samples"]=Samples?.DeepClone(), ["policy"]=Policy };
    public static SingingLanguage Parse(string data, string source)
    {
        var a=JsonNode.Parse(data)!.AsObject();
        return new(a["state"]!.GetValue<string>(), a["languages"]!.AsArray().Select(l=>l!.GetValue<string>()).ToArray(),
            source,a["confidence"]?.GetValue<double>(),a["samples"]?.DeepClone().AsArray());
    }
    public static SingingLanguage Manual(string value) => value switch {
        "CHINESE" => new("known",["zh"],"MANUAL"), "ENGLISH" => new("known",["en"],"MANUAL"),
        "CHINESE_ENGLISH" => new("known",["zh","en"],"MANUAL"), "OTHER" => new("known",["other"],"MANUAL"),
        "INSTRUMENTAL" => new("instrumental",[],"MANUAL"), "UNKNOWN" => new("unknown",[],"MANUAL"),
        _ => throw new ArgumentException("Unknown vocal-language value") };
}
