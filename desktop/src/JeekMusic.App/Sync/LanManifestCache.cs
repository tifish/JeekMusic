using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Sync;

/// <summary>Bounded process-local snapshots. Only usable with a healthy watcher and matching epoch/revision.</summary>
internal sealed class LanManifestCache
{
    internal const long LifetimeMs=900_000;
    const long MaximumBytes=64L*1024*1024;
    const int MaximumEntries=16;
    sealed record Snapshot(long Folder,string Root,string Path,string[] Selection,string Revision,long Created,long Bytes,IReadOnlyList<LanFiles.Entry> Files);
    readonly Dictionary<string,Snapshot> snapshots=new();
    readonly object gate=new();
    long hits,misses,invalidated;
    static string[] Normalize(IEnumerable<string> selection)=>selection.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    static string Key(string root,string path,IEnumerable<string> selection)=>root+"\n"+path+"\n"+string.Join('\n',Normalize(selection));
    static long Size(IReadOnlyList<LanFiles.Entry> files)=>files.Sum(e=>192L+e.Path.Length*2L+(e.Dependencies?.Sum(p=>32L+p.Length*2L)??0));
    internal bool TryGet(string root,string path,IReadOnlyList<string> selection,string revision,bool reliable,long now,out IReadOnlyList<LanFiles.Entry> files)
    {
        lock(gate) {
            foreach(var key in snapshots.Where(p=>now-p.Value.Created>=LifetimeMs).Select(p=>p.Key).ToArray())snapshots.Remove(key);
            files=[];
            if(reliable) {
                bool Valid(Snapshot item)=>item.Root==root && item.Path==path && item.Revision==revision;
                if(snapshots.TryGetValue(Key(root,path,selection),out var exact) && Valid(exact)) { files=exact.Files;hits++;return true; }
                if(snapshots.TryGetValue(Key(root,path,[""]),out var full) && Valid(full)) {
                    bool Selected(string candidate)=>selection.Any(s=>s.Length==0 || candidate==s || candidate.StartsWith(s+"/",StringComparison.Ordinal));
                    var required=full.Files.Where(e=>Selected(e.Path)).SelectMany(e=>e.Dependencies??[]).ToHashSet(StringComparer.Ordinal);
                    files=full.Files.Where(e=>Selected(e.Path)||required.Contains(e.Path)).ToArray();hits++;return true;
                }
            }
            misses++;return false;
        }
    }
    internal void Store(long folder,string root,string path,IReadOnlyList<string> selection,string revision,bool reliable,long now,IReadOnlyList<LanFiles.Entry> files)
    {
        if(!reliable)return;
        var bytes=Size(files);if(bytes>MaximumBytes)return;
        lock(gate) {
            var key=Key(root,path,selection);snapshots.Remove(key);
            while(snapshots.Count>=MaximumEntries || snapshots.Values.Sum(s=>s.Bytes)+bytes>MaximumBytes) {
                if(snapshots.Count==0)break;
                snapshots.Remove(snapshots.MinBy(p=>p.Value.Created).Key);
            }
            snapshots[key]=new(folder,root,path,Normalize(selection),revision,now,bytes,files);
        }
    }
    internal void Invalidate(long folder) { lock(gate)foreach(var key in snapshots.Where(p=>p.Value.Folder==folder).Select(p=>p.Key).ToArray()) { snapshots.Remove(key);invalidated++; } }
    internal void Clear() { lock(gate)snapshots.Clear(); }
    internal JsonObject Diagnostics() { lock(gate)return new() { ["entries"]=snapshots.Count,["estimatedBytes"]=snapshots.Values.Sum(s=>s.Bytes),["hits"]=hits,["misses"]=misses,["invalidated"]=invalidated,["maximumEntries"]=MaximumEntries,["maximumBytes"]=MaximumBytes,["fallbackMs"]=LifetimeMs,["source"]="immediate_folder_watcher",["contentHashing"]=false }; }
}
