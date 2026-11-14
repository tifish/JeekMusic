namespace JeekMusic.Core.Sync;

/// <summary>Content/presence three-way merge. An inaccessible or unscanned side is never a deletion.</summary>
public static class FileReconciliation
{
    public sealed record State(bool Known,string? Hash,long EditedAtMs=0);
    public sealed record Baseline(string? Hash,string Version,string? RemoteHash=null,bool Metadata=false,bool Migration=false);
    public sealed record Decision(string Action,string? Reason=null);
    public static Decision Decide(Baseline? baseline,State local,State remote,bool newestWins=false)
    {
        if(!local.Known || !remote.Known) return new("defer","state_unknown");
        if(newestWins && Metadata(local.Hash,out var localSize,out var localTime) && Metadata(remote.Hash,out var remoteSize,out var remoteTime)) {
            // A confirmed landing can round mtime on the destination filesystem.
            if(baseline is { Migration:false,Metadata:true } && local.Hash==baseline.Hash && remote.Hash==baseline.RemoteHash)return new("none");
            if(localTime!=remoteTime)return new(localTime>remoteTime?"upload":"download","newer_file_mtime");
            if(localSize==remoteSize)return new(baseline!=null && local.Hash==baseline.Hash && remote.Hash==(baseline.Metadata?baseline.RemoteHash:baseline.Hash)?"none":"ack");
            if(local.EditedAtMs!=remote.EditedAtMs)return new(local.EditedAtMs>remote.EditedAtMs?"upload":"download","newer_edit_time");
            return new("conflict","same_time_different_size");
        }
        if(baseline?.Migration==true) return local.Hash==remote.Hash?new("ack"):new("conflict","legacy_metadata_unconfirmed");
        if(baseline is null)
        {
            if(local.Hash==remote.Hash) return new(local.Hash is null?"none":"ack");
            if(local.Hash is null) return new("download");
            if(remote.Hash is null) return new("upload");
            return new("conflict","initial_different_content");
        }
        var remoteBase=baseline.Metadata?baseline.RemoteHash:baseline.Hash;
        if(local.Hash==baseline.Hash && remote.Hash==remoteBase) return new("none");
        if(local.Hash==remote.Hash) return new("ack");
        if(local.Hash==baseline.Hash) return new(remote.Hash is null?"delete_local":"download");
        if(remote.Hash==remoteBase) return new(local.Hash is null?"delete_remote":"upload");
        return new("conflict",local.Hash is null || remote.Hash is null?"modified_vs_deleted":"both_modified");
    }
    static bool Metadata(string? version,out long size,out long time) {
        size=time=0;var parts=version?.Split(':');
        return parts is { Length:3 } && parts[0]=="m2" && long.TryParse(parts[1],out size) && size>=0 && long.TryParse(parts[2],out time);
    }
}
