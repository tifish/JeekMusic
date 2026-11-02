namespace JeekMusic.Core.Sync;

/// <summary>Content/presence three-way merge. An inaccessible or unscanned side is never a deletion.</summary>
public static class FileReconciliation
{
    public sealed record State(bool Known,string? Hash);
    public sealed record Baseline(string? Hash,string Version);
    public sealed record Decision(string Action,string? Reason=null);
    public static Decision Decide(Baseline? baseline,State local,State remote)
    {
        if(!local.Known || !remote.Known) return new("defer","state_unknown");
        if(baseline is null)
        {
            if(local.Hash==remote.Hash) return new(local.Hash is null?"none":"ack");
            if(local.Hash is null) return new("download");
            if(remote.Hash is null) return new("upload");
            return new("conflict","initial_different_content");
        }
        if(local.Hash==remote.Hash) return new(local.Hash==baseline.Hash?"none":"ack");
        if(local.Hash==baseline.Hash) return new(remote.Hash is null?"delete_local":"download");
        if(remote.Hash==baseline.Hash) return new(local.Hash is null?"delete_remote":"upload");
        return new("conflict",local.Hash is null || remote.Hash is null?"modified_vs_deleted":"both_modified");
    }
}
