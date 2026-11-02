using System.Text.Json.Nodes;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

public sealed partial class PreferenceTraining
{
    string jobStage="orchestrate";
    readonly Dictionary<string,(long At,JsonObject Failure)> historicalFailures=new();
    static string Stage(string action)=>action=="regression_extract"?"extract":action is "prepare" or "extract" or "train" or "evaluate"?action:"orchestrate";
    static string? FailureForState(string status)=>status switch {
        "components_required"=>"dependency_missing","insufficient_material"=>"insufficient_material","validation_failed" or "training_node_failed"=>"validation_failed",
        "check_material_required" or "identity_incomplete" or "protocol_incomplete_for_head"=>"check_material_required","cancelled"=>"cancelled","interrupted"=>"interrupted","invalidated"=>"invalidated","incomplete"=>"work_budget",_=>null
    };
    void Fail(JsonObject failure,string status="failed")=>Set(status,LearningFailure.Summary(failure),failure);
    void LearningFail(JsonObject failure,string status="failed",int processed=0,int total=0,int uncovered=0)=>LearningSet(status,LearningFailure.Summary(failure),processed,total,uncovered,failure);
    JsonObject? HeadFailure(string dimension)
    {
        var persisted=service.Store.Current(service.ActiveProfile,"model").Where(e=>S(e["entityId"])=="job:"+dimension).ToArray();
        var entry=persisted.Length==1?persisted[0]:null;
        var saved=headStates.GetValueOrDefault(service.ActiveProfile+":"+dimension)??entry?["payload"]?.AsObject();
        if(saved?["failure"] is JsonObject existing) return LearningFailure.Sanitize(existing,dimension);
        var status=S(saved?["state"]);
        if(FailureForState(status) is { } code) return LearningFailure.Create(code,"check_material",dimension);
        if(status!="failed") return null;
        long at=entry?["at"]?.GetValue<long>()??0;
        var key=service.ActiveProfile+":"+dimension;
        if(historicalFailures.TryGetValue(key,out var cached) && cached.At==at) return cached.Failure.DeepClone().AsObject();
        var failure=LearningFailure.Create("history_unavailable","orchestrate",dimension,historical:true);
        try
        {
            if(at>0 && Directory.Exists(root))
            {
                // Recovery is read-only, bounded, and requires matching dimension and failure time.
                var dirs=Directory.EnumerateDirectories(root,"job-*").Take(4096).Select(p=>new DirectoryInfo(p))
                    .Where(d=>(d.Attributes&FileAttributes.ReparsePoint)==0).OrderByDescending(d=>d.LastWriteTimeUtc).Take(64);
                foreach(var dir in dirs)
                {
                    var result=new FileInfo(Path.Combine(dir.FullName,"result.json")); var input=new FileInfo(Path.Combine(dir.FullName,"input.json"));
                    if(!result.Exists || !input.Exists || result.Length>65536 || input.Length>4*1024*1024 || Math.Abs(new DateTimeOffset(result.LastWriteTimeUtc).ToUnixTimeMilliseconds()-at)>2000) continue;
                    var raw=JsonNode.Parse(File.ReadAllText(result.FullName)); var request=JsonNode.Parse(File.ReadAllText(input.FullName));
                    if(S(raw?["state"])!="failed" || S(request?["dimension"])!=dimension) continue;
                    var stage=request?["rows"] is JsonArray rows && rows.Any(r=>r?["path"]!=null)?"extract":"train";
                    failure=LearningFailure.FromWorker(raw,stage,dimension,null,historical:true);
                    failure["occurredAt"]=at; break;
                }
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { }
        if(historicalFailures.Count>=16) historicalFailures.Clear();
        historicalFailures[key]=(at,failure.DeepClone().AsObject()); return failure;
    }
    JsonObject? WorkflowFailure(JsonObject workflow)
    {
        if(workflow["failure"] is JsonObject failure) return LearningFailure.Sanitize(failure);
        if(S(workflow["state"])=="failed")
        {
            foreach(var dimension in Dimensions)
                if(HeadFailure(dimension) is { } head && S(head["code"]) is not ("insufficient_material" or "check_material_required")) return head;
            return LearningFailure.Create("history_unavailable","orchestrate",historical:true);
        }
        return FailureForState(S(workflow["state"])) is { } code?LearningFailure.Create(code,"orchestrate"):null;
    }
    // Only this exact decoder-only revision may reuse successful vectors/validated heads from the previous worker.
    // Previously successful inputs were already exactly 480000 finite samples; trim changes only rejected overshoot.
    static readonly string[] PreviousCompatibleWorkers=["cc9ea8a8e6daffa64cc62619c94d5a62cc2454afdc0c1743934e0e5feed1f237","c6c7d0f8bf572afd2cb010d6dec94fb2eb95e39c44909304f991f8cea9aca181"];
    static readonly string[] CompatibleDecoderWorkers=["a0acd881c29e0c38c6f6dd3706e7e9d17ec64223c998efc9f19e84d23461b15b","9785d092009d773aff38fa2fb6787cc0c7f3f574aa114f4d71015740028ab140"];
    bool CompatibleWorker(string hash)=>hash==FileHash(Worker) || PreviousCompatibleWorkers.Contains(hash) && CompatibleDecoderWorkers.Contains(FileHash(Worker));
    static async Task<string> DrainWorker(StreamReader reader,CancellationToken ct)
    {
        var saved=new System.Text.StringBuilder(); var buffer=new char[4096]; int read;
        while((read=await reader.ReadAsync(buffer,ct))>0) if(saved.Length<262144) saved.Append(buffer,0,Math.Min(read,262144-saved.Length));
        return saved.ToString();
    }
}
