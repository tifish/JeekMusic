using System.Text.Json.Nodes;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Core.Preferences;

/// <summary>Pre-label identity partition; labels and CLAP scores are not inputs.</summary>
public static class PreferenceStudy
{
    public static JsonObject Freeze(IReadOnlyList<Identity> identities, IReadOnlySet<string> attempted, string seed,bool includeWorkout=true)
    {
        int developmentCount=includeWorkout?12:8,testCount=includeWorkout?48:24,trainingMaximum=includeWorkout?48:36;
        var graph=Group(identities);
        if(graph.Unresolved.Length>0) throw new InvalidOperationException($"Identity review incomplete ({graph.Unresolved.Length}); development/test remain locked");
        var groups=identities.GroupBy(r=>graph.Components[r.Id]).ToList();
        var pinned=groups.Where(g=>g.Any(r=>r.Exposed || attempted.Contains(r.Id))).Select(g=>g.Key).ToHashSet();
        var eligible=groups.Where(g=>!pinned.Contains(g.Key) && g.Any(r=>r.Supported)).OrderBy(g=>Order(seed+":reserve",g.Key),StringComparer.Ordinal).ToList();
        if(eligible.Count<developmentCount+testCount) throw new InvalidOperationException($"Need {developmentCount+testCount} unseen identity components for fixed development {developmentCount} + sealed test {testCount}; never split a component to fill a quota");
        var test=eligible.Take(testCount).ToList(); var development=eligible.Skip(testCount).Take(developmentCount).ToList();
        var reserved=test.Concat(development).Select(g=>g.Key).ToHashSet();
        string Choose(IEnumerable<Identity> group)=>group.Where(r=>r.Supported).OrderBy(r=>Order(seed+":recording",r.Id),StringComparer.Ordinal).First().Id;
        var pool=groups.Where(g=>!reserved.Contains(g.Key)).OrderBy(g=>Order(seed+":train",g.Key),StringComparer.Ordinal)
            .SelectMany(g=>g.Where(r=>r.Supported && !r.Exposed).OrderBy(r=>Order(seed+":recording",r.Id),StringComparer.Ordinal)).ToList();
        var train=new List<string>(); var artists=new Dictionary<string,int>(); var albums=new Dictionary<string,int>();
        // Existing pilot attempts stay on the training side and consume the original attempt budget.
        foreach(var row in pool.OrderBy(r=>attempted.Contains(r.Id)?0:1))
        {
            if(row.Artists.Any(a=>artists.GetValueOrDefault(a)>=4) || albums.GetValueOrDefault(row.Album)>=2) continue;
            train.Add(row.Id); foreach(var a in row.Artists) artists[a]=artists.GetValueOrDefault(a)+1; albums[row.Album]=albums.GetValueOrDefault(row.Album)+1;
            if(train.Count==trainingMaximum) break;
        }
        var budget=includeWorkout?48:new[] {36,24,12}.FirstOrDefault(n=>n<=train.Count);
        train=train.Take(budget).ToList();
        if(train.Count<(includeWorkout?48:12) || train.Select(id=>graph.Components[id]).Distinct().Count()<(includeWorkout?8:6) || attempted.Any(id=>!train.Contains(id))) throw new InvalidOperationException("Insufficient training frame under identity/artist/album caps; preserve attempts, do not split leakage groups");
        var assignments=new JsonObject(); foreach(var row in identities) assignments[row.Id]=graph.Components[row.Id];
        var p=new JsonObject { ["protocolVersion"]="personal-tags-v2",["supportedHeads"]=Strings(includeWorkout?Dimensions:Dimensions.Where(d=>d!="MOOD")),["seed"]=seed,["state"]="frozen",["training"]=Strings(train),["development"]=Strings(development.Select(Choose)),["sealed_test"]=Strings(test.Select(Choose)),["components"]=assignments,
            ["inventoryCount"]=identities.Count,["componentCount"]=groups.Count,["exposedCount"]=identities.Count(r=>r.Exposed),["unsupportedCount"]=identities.Count(r=>!r.Supported),["independent"]=true,
            ["sampling"]="component_uniform_reserved; deterministic_training_order; artist_cap4_album_cap2",["trainingSamplingDeviation"]="uniform deterministic order; production-tag enrichment omitted to keep blind pilot independent of automatic scores; frozen before any new CLAP queries" };
        p["hash"]=Hash(p.ToJsonString()); return p;
    }

    public static bool Contaminated(JsonObject frozen,IReadOnlyList<Identity> current)
    {
        var graph=Group(current);
        if(graph.Unresolved.Length>0) return true;
        var sides=new Dictionary<string,HashSet<string>>();
        foreach(var role in new[] { "training","development","sealed_test" })
            foreach(var id in ReadStrings(frozen[role]))
            {
                if(!graph.Components.TryGetValue(id,out var group)) return true;
                if(!sides.TryGetValue(group,out var roles)) sides[group]=roles=[];
                roles.Add(role);
            }
        return sides.Values.Any(s=>s.Count>1);
    }
}
