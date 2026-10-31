using System.Globalization;
using System.Text.Json.Nodes;
using JeekMusic.Analysis;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;

// Explicit single-file read-only probe. No library import, network access or audio output.
if (args.Length is < 3 or > 4 || args.Length == 4 && args[3] is not ("--sampling-comparison" or "--diagnostics"))
{ Console.Error.WriteLine("AnalysisProbe <audio-path> <start-ms> <duration-ms> [--sampling-comparison|--diagnostics]"); return 2; }
var start = long.Parse(args[1], CultureInfo.InvariantCulture);
var duration = long.Parse(args[2], CultureInfo.InvariantCulture);
if (start < 0 || duration <= 0) throw new ArgumentException("Invalid logical track bounds");
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
AudioTagger? model = null;
try {
    var track = new Track { TrackKey = "probe", Title = "", ArtistDisplay = "", FileName = "", Dir = "", StartMs = start, EndMs = start + duration, DurationMs = duration };
    if (args.Length == 4 && args[3] == "--diagnostics")
    {
        cancel.CancelAfter(TimeSpan.FromSeconds(180));
        Console.WriteLine((await AnalysisDiagnostics.Run(args[0], track, cancel.Token)).ToJsonString());
        return 0;
    }
    if (args.Length == 4)
    {
        Console.WriteLine((await SamplingComparison.Run(args[0], track, cancel.Token)).ToJsonString());
        return 0;
    }
    var result = await TrackAnalyzer.Analyze(args[0], track, cancel.Token, (pcm, ct) => (model ??= new AudioTagger()).AnalyzeWindows(pcm, ct));
    var output = JsonNode.Parse(result.ToJson())!.AsObject();
    output["provenance"] = "desktop_production_v6_onnx";
    output["analyzerVersion"] = AnalysisVersions.AnalyzerVersion;
    output["rulesVersion"] = AnalysisVersions.RulesVersion;
    var classifications = new JsonObject();
    foreach (var sensitivity in Enum.GetValues<Sensitivity>())
    {
        var tags = new JsonObject();
        foreach (var (dimension, tag) in TagClassifier.Classify(result.ToFeatures(AnalysisVersions.AnalyzerVersion), sensitivity))
            tags[dimension.ToString()] = tag?.ToString();
        classifications[sensitivity.ToString()] = tags;
    }
    output["classifications"] = classifications;
    output["model_sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Models", "yamnet.onnx")))).ToLowerInvariant();
    Console.WriteLine(output.ToJsonString());
    return result.Complete ? 0 : 1;
} finally { model?.Dispose(); }
