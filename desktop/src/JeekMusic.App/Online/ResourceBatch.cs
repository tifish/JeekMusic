using System.Text.Json.Nodes;
using JeekMusic.Covers;
using JeekMusic.Infrastructure;
using JeekMusic.Library;

namespace JeekMusic.Online;

/// <summary>UI/MCP 共用的单任务入口。只捕获目标；获取、缓存和保存仍由 OnlineFeature.Fetch 完成。
/// 所有状态操作在 UI 线程，去重在后台，顺序获取并定期让出 UI。
/// </summary>
internal sealed class ResourceBatch(Func<Track, DownloadKind, CancellationToken, Task<bool>> fetch, CancellationToken lifetime)
{
    internal sealed class Job : IDisposable
    {
        internal readonly Track[] Tracks;
        internal readonly CancellationTokenSource Cancellation;
        internal readonly DownloadKind Kind;
        internal readonly string Scope, Source;
        internal string State = "preparing", Title = "";
        internal int Total, Done, Saved, Failed;
        internal Job(IReadOnlyList<Track> tracks, DownloadKind kind, string scope, string source, CancellationToken lifetime)
        { Tracks = tracks.ToArray(); Kind = kind; Scope = scope; Source = source; Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime); }
        public void Dispose() => Cancellation.Dispose();
    }

    internal Job? Current { get; private set; }
    internal bool Busy => Current?.State is "preparing" or "running" or "cancelling";
    internal event Action? Changed;

    internal Job Reserve(IReadOnlyList<Track> tracks, DownloadKind kind, string scope, string source)
    {
        if (Busy) throw new InvalidOperationException(L.T("已有批量获取任务，请等待完成或取消。"));
        if (tracks.Count == 0) throw new ArgumentException(L.T("当前列表没有可获取的歌曲。"));
        Current?.Dispose();
        Current = new Job(tracks, kind, scope, source, lifetime);
        Changed?.Invoke();
        return Current;
    }

    internal void Cancel() => Cancel(Current);

    internal void Cancel(Job? job)
    {
        if (!ReferenceEquals(Current, job) || !Busy) return;
        Current!.State = "cancelling";
        Current.Cancellation.Cancel();
        Changed?.Invoke();
    }

    internal void FinishPreparation(Job job)
    {
        job.State = "cancelled";
        Changed?.Invoke();
    }

    internal async Task Run(Job job)
    {
        if (!ReferenceEquals(Current, job) || job.State is not ("preparing" or "cancelling"))
            throw new InvalidOperationException("Batch is no longer pending");
        var ct = job.Cancellation.Token;
        try
        {
            var targets = await Task.Run(() => job.Tracks.DistinctBy(t =>
                job.Kind == DownloadKind.Covers ? CoverService.CacheKey(t) : t.TrackKey).ToArray(), ct);
            job.Total = targets.Length;
            ct.ThrowIfCancellationRequested();
            job.State = "running";
            Changed?.Invoke();
            foreach (var track in targets)
            {
                ct.ThrowIfCancellationRequested();
                job.Title = track.Title;
                Changed?.Invoke();
                try { if (await fetch(track, job.Kind, ct)) job.Saved++; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { job.Failed++; Log.Warn("online", "获取失败", e); }
                job.Done++;
                Changed?.Invoke();
                // Cached hits may complete synchronously, even for tens of thousands of tracks.
                if (job.Done % 32 == 0) await Task.Delay(1, ct);
            }
            ct.ThrowIfCancellationRequested();
            job.State = "completed";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { job.State = "cancelled"; }
        catch (Exception e) { job.State = "failed"; Log.Warn("online", "批量获取失败", e); }
        finally { Changed?.Invoke(); }
    }

    internal JsonObject Diagnostics() => Current is not { } j ? new() { ["state"] = "idle", ["busy"] = false } : new()
    {
        ["state"] = j.State, ["busy"] = Busy, ["kind"] = j.Kind == DownloadKind.Covers ? "covers" : "lyrics",
        ["scope"] = j.Scope, ["source"] = j.Source, ["targetSongCount"] = j.Tracks.Length,
        ["totalUnits"] = j.Total, ["processedUnits"] = j.Done, ["savedUnits"] = j.Saved, ["failedUnits"] = j.Failed,
        ["unit"] = j.Kind == DownloadKind.Covers ? "album-or-track" : "track", ["currentTitle"] = j.Title,
        ["rangePolicy"] = "captured-before-confirmation; existing-resources-first; no-forced-overwrite"
    };
}
