namespace JeekMusic.Infrastructure;

/// <summary>Release large, short-lived scan/snapshot allocations after work settles; never poll while idle.</summary>
public sealed class MemoryMaintenance : IDisposable
{
    readonly Lock _gate = new();
    CancellationTokenSource? _pending;
    bool _stopped;

    public void AfterLibraryWork(int tracks)
    {
        if (tracks < 10000) return;
        lock (_gate)
        {
            if (_stopped) return;
            _pending?.Cancel();
            var request = _pending = new CancellationTokenSource();
            _ = Reclaim(request);
        }
    }

    async Task Reclaim(CancellationTokenSource request)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), request.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_stopped || request.IsCancellationRequested) return;
                // Large native AOT desktop workloads can leave over 100 MB of dead managed buffers committed.
                // A single collection after the burst returns that memory without a recurring idle timer.
                if (GC.GetTotalMemory(false) > 96L * 1024 * 1024)
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate) if (ReferenceEquals(_pending, request)) _pending = null;
            request.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate) { _stopped = true; _pending?.Cancel(); }
    }
}
