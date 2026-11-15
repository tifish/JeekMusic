using System.Text.Json.Nodes;

namespace JeekMusic.Sync;

public sealed partial class LanSyncService
{
    // Bounded by the paired-device limit. Counts only; never retain references or payloads.
    readonly Dictionary<string,JsonObject> _metadataDiagnostics=[];

    JsonObject MetadataPeer(string device)
    {
        if(!_metadataDiagnostics.TryGetValue(device,out var row)) {
            foreach(var revoked in _metadataDiagnostics.Keys.Where(k=>!Devices.ContainsKey(k)).ToArray()) _metadataDiagnostics.Remove(revoked);
            row=new() { ["deviceId"]=device,["requests"]=0L,["responsesSent"]=0L,["failedRequests"]=0L };
            _metadataDiagnostics[device]=row;
        }
        return row;
    }
    void MetadataRequested(string device,JsonObject state)
    {
        lock(_gate) {
            var row=MetadataPeer(device);
            row["requests"]=row["requests"]!.GetValue<long>()+1;
            row["lastRequestAt"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            row["peerAppVersion"]=(state["device"] as JsonObject)?["app_version"] is JsonValue v && v.TryGetValue<string>(out var version) &&
                version.Length<=64 && version.All(c=>char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')?version:null;
            row["peerHasLanguageExtension"]=state["vocal_languages"] is JsonObject;
            row["peerExportedLanguageTracks"]=(state["vocal_languages"] as JsonObject)?["items"] is JsonObject items?items.Count:0;
        }
    }
    void MetadataFailed(string device,Exception error)
    {
        lock(_gate) {
            var row=MetadataPeer(device);
            row["failedRequests"]=row["failedRequests"]!.GetValue<long>()+1;
            row["lastErrorType"]=error.GetType().Name;
        }
    }
    void MetadataSent(string device,JsonObject response)
    {
        lock(_gate) {
            var row=MetadataPeer(device);
            row["responsesSent"]=row["responsesSent"]!.GetValue<long>()+1;
            row["lastResponseAt"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            row["lastSentLanguageTracks"]=(response["state"]?["vocal_languages"] as JsonObject)?["items"] is JsonObject items?items.Count:0;
            row["lastErrorType"]=null;
        }
    }
    JsonObject MetadataDiagnostics()=>new() {
        ["scope"]="current_process",["responseMeans"]="written_to_tls_not_peer_import_acknowledgement",
        ["devices"]=new JsonArray(_metadataDiagnostics.Where(p=>Devices.ContainsKey(p.Key)).Select(p=>p.Value.DeepClone()).ToArray())
    };
}
