using System.Diagnostics;
using System.Text.Json.Nodes;

namespace JeekMusic.Analysis;

/// <summary>Bounded explicit production updates. Completed atomic track commits survive cancellation.</summary>
public sealed class AnalysisBatchJob : IDisposable
{
    readonly object gate = new();
    CancellationTokenSource? stop;
    readonly Stopwatch elapsed = new();
    readonly List<JsonObject> results = [];
    string state = "idle";
    long[] ids = [];
    int budget;
    long? current;

    public void Start(long[] trackIds, int seconds, Func<long, CancellationToken, Task<JsonObject>> analyze)
    {
        if (trackIds.Length is < 1 or > 20 || trackIds.Distinct().Count() != trackIds.Length)
            throw new ArgumentException("Requires 1–20 distinct trackIds");
        if (seconds is < 15 or > 180) throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (gate)
        {
            if (state is "running" or "cancelling") throw new InvalidOperationException("Analysis batch already running");
            stop?.Dispose(); stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var token = stop.Token;
            ids = trackIds.ToArray(); budget = seconds; results.Clear(); current = null; state = "running"; elapsed.Restart();
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var id in trackIds)
                    {
                        token.ThrowIfCancellationRequested();
                        lock (gate) current = id;
                        JsonObject result;
                        try { result = await analyze(id, token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception e) { result = new JsonObject { ["trackId"] = id, ["state"] = "failed", ["error"] = e.GetType().Name }; }
                        lock (gate) results.Add(result);
                        token.ThrowIfCancellationRequested();
                    }
                    lock (gate) { state = "completed"; current = null; elapsed.Stop(); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { lock (gate) { state = state == "cancelling" ? "cancelled" : "budget_exhausted"; current = null; elapsed.Stop(); } }
            });
        }
    }
    public JsonObject Read()
    {
        lock (gate) return new JsonObject {
            ["state"] = state, ["total"] = ids.Length, ["processed"] = results.Count, ["currentTrackId"] = current,
            ["budgetSeconds"] = budget, ["elapsedMs"] = elapsed.ElapsedMilliseconds,
            ["results"] = new JsonArray(results.Select(r => r.DeepClone()).ToArray()), ["writesAudio"] = false,
            ["writesFeaturesAndAutomaticTags"] = true, ["completedCommitsRetained"] = true };
    }
    public void Cancel() { lock (gate) if (state == "running") { state = "cancelling"; stop!.Cancel(); } }
    public void Dispose() => Cancel();
}
