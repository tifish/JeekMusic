using System.Text.Json.Nodes;

namespace JeekMusic.Sync;

public sealed partial class LanSyncService
{
    readonly string _watchEpoch=Guid.NewGuid().ToString("N");
    readonly Dictionary<long,long> _folderChanges=new();
    string _watchReason="startup";
    long _watchEvents;
    void OnFileDirectoriesChanged(long folder,IReadOnlyCollection<string> directories)=>FilesChanged(folder,"directory_change_or_overflow");
    void OnFileWatcherFailed(long folder)=>FilesChanged(folder,"watch_unavailable");
    readonly JeekMusic.Core.Sync.LanHashBudget _hashAdmission=new();
    void ReservePlanVerification(string device,long bytes)
    {
        if(!_hashAdmission.Reserve(FileOwner(device),bytes))throw Busy();
    }
    void FilesChanged(long folder,string reason)
    {
        lock(_gate) { _folderChanges[folder]=_folderChanges.GetValueOrDefault(folder)+1;_watchReason=reason;_watchEvents++; }
    }
    string WatchRevision(string root)
    {
        lock(_gate) {
            var folder=Roots[root]?["folderId"]?.GetValue<long>()??0;
            return _watchEpoch+":"+_folderChanges.GetValueOrDefault(folder)+":"+RootRevision;
        }
    }
    JsonObject WatchDiagnostics()=>new() { ["source"]="library_folder_watcher",["epoch"]=_watchEpoch,["events"]=_watchEvents,["lastTrigger"]=_watchReason,["debounceMs"]=2000,["remotePollSeconds"]=30,["fallbackSeconds"]=900,["connectionMode"]="bounded_authenticated_poll" };
}
