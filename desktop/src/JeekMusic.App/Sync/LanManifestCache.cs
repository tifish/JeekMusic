using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Sync;

/// <summary>Bounded process-local snapshots. Only usable with a healthy watcher and matching epoch/revision.</summary>
internal sealed class LanManifestCache
{
    internal const long LifetimeMs=900_000;
    const long MaximumBytes=64L*1024*1024;
    const int MaximumEntries=16;
    sealed record Snapshot(long Folder,string Root,string Path,string[] Selection,string Revision,long Created,long Bytes,long Version,IReadOnlyList<LanFiles.Entry> Files,IReadOnlyDictionary<string,LanFiles.Entry> Paths);
    sealed record Observation(long Version,LanFiles.Entry File);
    sealed class FolderState(long version) {
        public long Version=version,SharedBytes;
        public readonly Dictionary<string,long> Changes=new(StringComparer.Ordinal);
        public readonly Dictionary<string,Observation> Shared=new(StringComparer.Ordinal);
    }
    internal sealed class ScanTicket(long folder,string root,long version) { internal readonly long Folder=folder;internal readonly string Root=root;internal long Version=version; }
    readonly Dictionary<string,Snapshot> snapshots=new();
    readonly Dictionary<long,FolderState> folders=new();
    readonly object gate=new();
    long hits,misses,invalidated,clock,incrementalHits,refreshedPaths,libraryMetadataReused,fullScans;
    static string RootPath(string path)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    FolderState State(long folder)=>folders.TryGetValue(folder,out var state)?state:folders[folder]=new(++clock);
    static string[] Normalize(IEnumerable<string> selection)=>selection.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    static string Key(string root,string path,IEnumerable<string> selection)=>root+"\n"+RootPath(path)+"\n"+string.Join('\n',Normalize(selection));
    static long Size(IReadOnlyList<LanFiles.Entry> files)=>files.Sum(e=>192L+e.Path.Length*2L+(e.Dependencies?.Sum(p=>32L+p.Length*2L)??0));
    internal bool TryGet(string root,string path,IReadOnlyList<string> selection,string revision,bool reliable,long now,out IReadOnlyList<LanFiles.Entry> files,bool recordMiss=true)
    {
        lock(gate) {
            foreach(var key in snapshots.Where(p=>now-p.Value.Created>=LifetimeMs).Select(p=>p.Key).ToArray())snapshots.Remove(key);
            files=[];
            if(reliable) {
                bool Valid(Snapshot item)=>item.Root==root && item.Path==RootPath(path) && item.Revision==revision && item.Version==State(item.Folder).Version;
                if(snapshots.TryGetValue(Key(root,path,selection),out var exact) && Valid(exact)) { files=exact.Files;hits++;return true; }
                if(snapshots.TryGetValue(Key(root,path,[""]),out var full) && Valid(full)) {
                    if(!SelectionSpellingMatches(full.Files,selection)) { if(recordMiss)misses++;return false; }
                    bool Selected(string candidate)=>selection.Any(s=>s.Length==0 || candidate==s || candidate.StartsWith(s+"/",StringComparison.Ordinal));
                    var required=full.Files.Where(e=>Selected(e.Path)).SelectMany(e=>e.Dependencies??[]).ToHashSet(StringComparer.Ordinal);
                    files=full.Files.Where(e=>Selected(e.Path)||required.Contains(e.Path)).ToArray();hits++;return true;
                }
            }
            if(recordMiss)misses++;return false;
        }
    }
    internal void Store(long folder,string root,string path,IReadOnlyList<string> selection,string revision,bool reliable,long now,IReadOnlyList<LanFiles.Entry> files,long? expectedVersion=null)
    {
        if(!reliable)return;
        var bytes=Size(files);if(bytes>MaximumBytes)return;
        lock(gate) {
            var version=State(folder).Version;if(expectedVersion!=null && expectedVersion!=version)return;
            var key=Key(root,path,selection);snapshots.Remove(key);
            while(snapshots.Count>=MaximumEntries || snapshots.Values.Sum(s=>s.Bytes)+bytes>MaximumBytes) {
                if(snapshots.Count==0)break;
                snapshots.Remove(snapshots.MinBy(p=>p.Value.Created).Key);
            }
            snapshots[key]=new(folder,root,RootPath(path),Normalize(selection),revision,now,bytes,version,files,files.ToDictionary(e=>e.Path,StringComparer.OrdinalIgnoreCase));
        }
    }
    internal long Version(long folder) { lock(gate)return State(folder).Version; }
    internal void FullScanStarted() { lock(gate) { misses++;fullScans++; } }
    internal void Changed(long folder,string path,bool directory,bool unavailable=false) {
        path=path.Replace('\\','/');
        if(path.Split('/').Any(p=>p.StartsWith('.')))return;
        lock(gate) {
            if(directory || path.Contains('~') || unavailable && !snapshots.Values.Any(s=>s.Folder==folder && s.Paths.ContainsKey(path))) { Invalidate(folder);return; }
            try { LanFiles.Relative(path); }catch(IOException) { Invalidate(folder);return; }
            // Unknown names can affect the complete directory collision/layout checks.
            if(!LanFiles.SupportsMetadata(path)) { Invalidate(folder);return; }
            var state=State(folder);
            if(state.Changes.Count>=100_000) { Invalidate(folder);return; }
            state.Changes[path]=state.Version=++clock;
            if(state.Shared.Remove(path,out var old))state.SharedBytes-=Size([old.File]);
        }
    }
    internal ScanTicket BeginLibraryScan(long folder,string root) { lock(gate)return new(folder,RootPath(root),State(folder).Version); }
    internal void ObserveLibraryFile(ScanTicket ticket,string relative,long ticks,long size) {
        var path=relative.Replace('\\','/');
        if(!LanFiles.SupportsMetadata(path) || path.Split('/').Any(p=>p.StartsWith('.')))return;
        try { LanFiles.Relative(path); }catch(IOException) { return; }
        lock(gate) {
            var state=State(ticket.Folder);
            // The library supports more formats/paths than LAN. Only share leaves
            // already admitted by a complete safe inventory (never junction contents).
            if(state.Version!=ticket.Version)return;
            var admitted=snapshots.Values.Where(s=>s.Folder==ticket.Folder && s.Path.Equals(ticket.Root,StringComparison.OrdinalIgnoreCase) && s.Paths.ContainsKey(path)).ToArray();
            if(admitted.Length==0)return;
            var ns=checked((ticks-DateTime.UnixEpoch.Ticks)*100);
            // Re-observing unchanged CUE/audio metadata must not create fictitious
            // dirty work or force another full scan after the library debounce.
            if(admitted.All(s=>s.Paths[path].Size==size && s.Paths[path].ModifiedNs==ns && s.Version>=state.Changes.GetValueOrDefault(path)))return;
            if(state.Shared.Count>=100_000 || state.Changes.Count>=100_000 || folders.Values.Sum(s=>s.SharedBytes)>MaximumBytes) { Invalidate(ticket.Folder);return; }
            var entry=new LanFiles.Entry(path,size,ns,"");
            if(state.Shared.Remove(path,out var old))state.SharedBytes-=Size([old.File]);
            state.Changes[path]=state.Version=ticket.Version=++clock;state.Shared[path]=new(state.Version,entry);state.SharedBytes+=Size([entry]);
        }
    }
    static bool Selected(string path,IReadOnlyList<string> selection,StringComparison comparison=StringComparison.Ordinal)=>selection.Any(s=>s.Length==0 || path.Equals(s,comparison) || path.StartsWith(s+"/",comparison));
    static string DirectoryOf(string path)=>path.Contains('/')?path[..path.LastIndexOf('/')]:"";
    internal bool TryRefresh(long folder,string root,string path,IReadOnlyList<string> selection,string revision,bool reliable,long now,CancellationToken ct,
        out IReadOnlyList<LanFiles.Entry> files,out int refreshed,out int shared) {
        // Wait for the library's in-process scan/tag commit before capturing its
        // shared observations, rather than capturing a ticket that it will invalidate.
        lock(Online.TagWriteCommit.Gate)return RefreshCore(folder,root,path,selection,revision,reliable,now,ct,out files,out refreshed,out shared);
    }
    bool RefreshCore(long folder,string root,string path,IReadOnlyList<string> selection,string revision,bool reliable,long now,CancellationToken ct,
        out IReadOnlyList<LanFiles.Entry> files,out int refreshed,out int shared) {
        files=[];refreshed=shared=0;if(!reliable)return false;
        Snapshot snapshot;long ticket;string[] changes;Dictionary<string,Observation> common;
        lock(gate) {
            if(!snapshots.TryGetValue(Key(root,path,selection),out snapshot!) && !snapshots.TryGetValue(Key(root,path,[""]),out snapshot!))return false;
            if(snapshot.Folder!=folder || snapshot.Root!=root || snapshot.Path!=RootPath(path) || now-snapshot.Created>=LifetimeMs)return false;
            if(!SelectionSpellingMatches(snapshot.Files,selection))return false;
            var state=State(folder);ticket=state.Version;
            var dirty=state.Changes.Where(p=>p.Value>snapshot.Version).ToArray();
            var cueDirs=snapshot.Files.Where(e=>e.Dependencies?.Count>0).SelectMany(e=>(e.Dependencies??[]).Append(e.Path)).Select(DirectoryOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(dirty.Any(p=>cueDirs.Contains(DirectoryOf(p.Key)) || p.Key.EndsWith(".cue",StringComparison.OrdinalIgnoreCase) && Selected(p.Key,snapshot.Selection,StringComparison.OrdinalIgnoreCase)))return false;
            if(dirty.Any(p=>Selected(p.Key,snapshot.Selection,StringComparison.OrdinalIgnoreCase) && !Selected(p.Key,snapshot.Selection)))return false;
            changes=dirty.Select(p=>p.Key).Where(p=>Selected(p,snapshot.Selection)).ToArray();
            common=state.Shared.Where(p=>state.Changes.GetValueOrDefault(p.Key)==p.Value.Version).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        }
        var sharedCount=0;
        var next=LanFiles.RefreshMetadata(path,snapshot.Files,changes,p=> { if(common.TryGetValue(p,out var observation)) { sharedCount++;return observation.File; }return null; },ct);
        if(next==null)return false;
        lock(gate) {
            ct.ThrowIfCancellationRequested();
            // Never replace an invalidated/rebuilt snapshot with a partial refresh.
            if(State(folder).Version!=ticket || !snapshots.TryGetValue(Key(root,path,snapshot.Selection),out var current) || !ReferenceEquals(current,snapshot))return false;
            var bytes=Size(next);if(bytes>MaximumBytes)return false;
            snapshots[Key(root,path,snapshot.Selection)]=snapshot with { Files=next,Paths=next.ToDictionary(e=>e.Path,StringComparer.OrdinalIgnoreCase),Revision=revision,Version=ticket,Bytes=bytes };
            while(snapshots.Values.Sum(s=>s.Bytes)>MaximumBytes) snapshots.Remove(snapshots.MinBy(p=>p.Value.Created).Key);
            files=SelectedFiles(next,selection);refreshed=changes.Length;shared=sharedCount;
            hits++;incrementalHits++;refreshedPaths+=refreshed;libraryMetadataReused+=shared;
            return true;
        }
    }
    static IReadOnlyList<LanFiles.Entry> SelectedFiles(IReadOnlyList<LanFiles.Entry> all,IReadOnlyList<string> selection) {
        var required=all.Where(e=>Selected(e.Path,selection)).SelectMany(e=>e.Dependencies??[]).ToHashSet(StringComparer.Ordinal);
        return all.Where(e=>Selected(e.Path,selection)||required.Contains(e.Path)).ToArray();
    }
    static bool SelectionSpellingMatches(IReadOnlyList<LanFiles.Entry> files,IReadOnlyList<string> selection)=>
        !files.Any(e=>Selected(e.Path,selection,StringComparison.OrdinalIgnoreCase) && !Selected(e.Path,selection));
    internal void Invalidate(long folder) { lock(gate) {
        foreach(var key in snapshots.Where(p=>p.Value.Folder==folder).Select(p=>p.Key).ToArray()) { snapshots.Remove(key);invalidated++; }
        var state=State(folder);state.Version=++clock;state.Changes.Clear();state.Shared.Clear();state.SharedBytes=0;
    } }
    internal void Clear() { lock(gate) { snapshots.Clear();folders.Clear();clock++; } }
    internal JsonObject Diagnostics() { lock(gate)return new() { ["entries"]=snapshots.Count,["estimatedBytes"]=snapshots.Values.Sum(s=>s.Bytes),["hits"]=hits,["misses"]=misses,["invalidated"]=invalidated,["incrementalHits"]=incrementalHits,["refreshedPaths"]=refreshedPaths,["libraryMetadataReused"]=libraryMetadataReused,["fullScans"]=fullScans,["sharedEstimatedBytes"]=folders.Values.Sum(s=>s.SharedBytes),["maximumEntries"]=MaximumEntries,["maximumBytes"]=MaximumBytes,["maximumSharedBytes"]=MaximumBytes,["fallbackMs"]=LifetimeMs,["source"]="library_scan_and_path_watcher",["contentHashing"]=false }; }
}
