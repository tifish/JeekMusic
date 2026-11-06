using System.Diagnostics;
using System.Text.Json.Nodes;

namespace JeekMusic.Infrastructure;

/// <summary>Shared daily/NativeAOT diagnostics; only stage names and elapsed times are retained.</summary>
internal static class LifecycleTimings
{
    static readonly Lock Gate = new();
    static readonly Dictionary<string, long> Stages = new();
    static Stopwatch? shutdownWatch;
    static string shutdownState = "running";
    static readonly DateTime Started = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    internal static void Mark(string stage)
    {
        var elapsed = (long)(DateTime.UtcNow - Started).TotalMilliseconds;
        lock (Gate) Stages[stage] = elapsed;
        Log.Info("lifecycle", $"{stage}: {elapsed} ms since process start");
    }

    internal static IDisposable Measure(string stage) => new Measurement(stage);

    internal static void RequestShutdown()
    {
        lock (Gate) { shutdownWatch = Stopwatch.StartNew(); shutdownState = "requested"; }
    }

    internal static void ShutdownUiHidden()
    {
        lock (Gate) { Stages["shutdown.uiHidden"] = shutdownWatch?.ElapsedMilliseconds ?? 0; shutdownState = "cleaning"; }
        Log.Info("lifecycle", $"shutdown.uiHidden: {shutdownWatch?.ElapsedMilliseconds ?? 0} ms after exit request");
    }

    sealed class Measurement(string stage) : IDisposable
    {
        readonly Stopwatch watch = Stopwatch.StartNew();
        public void Dispose()
        {
            lock (Gate) Stages[stage] = watch.ElapsedMilliseconds;
            Log.Info("lifecycle", $"{stage}: {watch.ElapsedMilliseconds} ms");
        }
    }

    internal static JsonObject Diagnostics()
    {
        lock (Gate) return new JsonObject
        {
            ["units"] = "milliseconds",
            ["shutdownState"] = shutdownState,
            ["compositeImagePresent"] = File.Exists(Path.Combine(AppContext.BaseDirectory, "JeekMusic.r2r.dll")),
            ["startupWork"] = new JsonObject(Stages.Where(p => p.Key.StartsWith("startupWork.")).Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key[12..], JsonValue.Create(p.Value)))),
            ["startup"] = new JsonObject(Stages.Where(p => p.Key.StartsWith("startup.")).Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key[8..], JsonValue.Create(p.Value)))),
            ["shutdown"] = new JsonObject(Stages.Where(p => p.Key.StartsWith("shutdown.")).Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key[9..], JsonValue.Create(p.Value))))
        };
    }
}
