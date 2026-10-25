using JeekMusic.Core.Queue;

namespace JeekMusic.Playback;

public sealed partial class PlayerService
{
    Func<IReadOnlyList<long>>? _resolveSource;
    public QueueContext? PendingSourceContext { get; private set; }
    public long SourceSwitchRevision { get; private set; }
    public string SourceSwitchStatus { get; private set; } = "none";

    /// <summary>Prepare an explicit source handoff without touching the current stream/position.
    /// The resolver MUST use already captured in-memory data only: no scan, disk read or async wait.
    /// It is invoked only if Next/Previous/EOF beats the async list result. Navigation policy belongs to the caller.</summary>
    internal long BeginSourceSwitch(QueueContext context, Func<IReadOnlyList<long>> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        SourceSwitchRevision++;
        if (Queue.Current is null) { SourceSwitchStatus = "empty"; return SourceSwitchRevision; }
        PendingSourceContext = context;
        _resolveSource = resolve;
        SourceSwitchStatus = "pending";
        SyncNext(); // Invalidates/removes the old preload immediately, including queued late StartFile callbacks.
        StateChanged?.Invoke();
        return SourceSwitchRevision;
    }

    internal bool CompleteSourceSwitch(long revision, IReadOnlyList<long> list)
    {
        if (revision != SourceSwitchRevision || PendingSourceContext is not { } context) return false;
        PendingSourceContext = null;
        _resolveSource = null;
        SourceSwitchStatus = "committed";
        Queue.SwitchSource(context, list);
        SyncNext();
        StateChanged?.Invoke();
        return true;
    }

    internal void CancelSourceSwitch(bool resync = true)
    {
        if (PendingSourceContext is null) return;
        SourceSwitchRevision++;
        PendingSourceContext = null;
        _resolveSource = null;
        SourceSwitchStatus = "cancelled";
        if (resync) SyncNext();
    }

    bool ResolveSourceSwitch()
    {
        if (_resolveSource is not { } resolve) return true;
        var revision = SourceSwitchRevision;
        return CompleteSourceSwitch(revision, resolve());
    }
}
