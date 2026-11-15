namespace JeekMusic.Core.Sync;

public static partial class LanFiles
{
    public static bool SupportsMetadata(string path) => Extensions.Contains(System.IO.Path.GetExtension(path));

    /// <summary>Refresh leaf metadata in a complete, continuously watched inventory.
    /// Null requests full enumeration for directory/link changes. CUE dependency invalidation
    /// is decided by the inventory owner before calling this method. Shared observations
    /// are change hints only; all transfer/commit preconditions remain live.</summary>
    public static IReadOnlyList<Entry>? RefreshMetadata(string root,IReadOnlyList<Entry> previous,IReadOnlyList<string> changed,
        Func<string,Entry?>? observed=null,CancellationToken cancel=default,Budget? budget=null)
    {
        budget??=new();Resolve(root,"",true);
        var files=previous.ToDictionary(e=>e.Path,StringComparer.Ordinal);
        var checkedDirectories=new HashSet<string>(StringComparer.Ordinal);
        var visited=0;
        foreach(var relative in changed.Distinct(StringComparer.Ordinal)) {
            cancel.ThrowIfCancellationRequested();Relative(relative);
            if(!files.ContainsKey(relative) && files.Keys.Any(p=>p.StartsWith(relative+"/",StringComparison.OrdinalIgnoreCase)))return null;
            string full;
            try {
                full=Resolve(root,relative);
                if((File.GetAttributes(full)&FileAttributes.Directory)!=0)return null;
            } catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException) {
                Resolve(root,"",true);files.Remove(relative);continue;
            }
            // A newly discovered spelling must satisfy the same sibling-name rules as
            // full enumeration, including collisions with empty dirs/unsupported files.
            if(!files.ContainsKey(relative)) {
                var parent=System.IO.Path.GetDirectoryName(full)!;
                var relParent=relative.Contains('/')?relative[..relative.LastIndexOf('/')]:"";
                if(checkedDirectories.Add(parent)) {
                    var names=new List<string>();
                    foreach(var item in Directory.EnumerateFileSystemEntries(parent)) {
                        cancel.ThrowIfCancellationRequested();if(++visited>budget.Visited)throw new IOException("Enumeration budget exceeded");
                        var name=System.IO.Path.GetFileName(item);if(name.StartsWith('.'))continue;
                        Relative(Join(relParent,name));names.Add(name);
                    }
                    CheckCollisions(names);
                }
            }
            if(!SupportsMetadata(relative))continue;
            var shared=observed?.Invoke(relative);
            if(shared!=null && shared.Path==relative && shared.Size>=0 && shared.ModifiedNs%100==0) {
                files[relative]=new(relative,shared.Size,shared.ModifiedNs,"",files.GetValueOrDefault(relative)?.Dependencies);
            } else {
                var row=FileMetadata.Observe(root,relative);
                if(row["exists"]!.GetValue<bool>())files[relative]=new(relative,row["size"]!.GetValue<long>(),row["modifiedNs"]!.GetValue<long>(),"",files.GetValueOrDefault(relative)?.Dependencies);
                else files.Remove(relative);
            }
        }
        CheckCollisions(files.Keys);
        if(files.Count>budget.Files)throw new IOException("Selection exceeds 100000 files; choose smaller folders");
        if(files.Values.Sum(e=>e.Size)>budget.Bytes)throw new IOException("Selection exceeds 500 GiB; choose smaller folders");
        if(files.Values.Sum(e=>192L+e.Path.Length*2L+(e.Dependencies?.Count??0)*32L)>budget.Memory)throw new IOException("Manifest memory budget exceeded");
        return files.Values.OrderBy(e=>e.Path,StringComparer.Ordinal).ToArray();
    }
}
